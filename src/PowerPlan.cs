// Windows power-state helpers: hibernate and the two independent wake-ups that bring the laptop back from it.
// The app never hibernates unless at least one wake-up is armed, and it never restarts Windows.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ThinkPadMonitor
{
    public static class PowerPlan
    {
        [DllImport("powrprof.dll", SetLastError = true)] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
        [DllImport("powrprof.dll")] static extern bool IsPwrHibernateAllowed();
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int length, IntPtr previous, IntPtr returnLength);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TokenPrivilege { public int Count; public long Luid; public int Attributes; }

        static Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        static Guid RtcWake = new Guid("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d");
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;
        public const string WakeTaskName = "ThinkPadMonitor power-on after hibernate";

        public static bool HibernateAvailable { get { try { return IsPwrHibernateAllowed(); } catch { return false; } } }

        // Turns on "Allow wake timers" in the active power plan, for both AC and battery.
        public static bool EnableWakeTimers()
        {
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return false;
            Guid scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            LocalFree(p);
            if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubSleep, ref RtcWake, 1) != 0) return false;
            if (PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref SubSleep, ref RtcWake, 1) != 0) return false;
            return PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
        }

        // Windows refuses to suspend unless the caller has switched on its shutdown privilege.
        static void EnableShutdownPrivilege()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0028 /* ADJUST_PRIVILEGES | QUERY */, out token)) return;
            var tp = new TokenPrivilege { Count = 1, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            if (LookupPrivilegeValue(null, "SeShutdownPrivilege", out tp.Luid))
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            CloseHandle(token);
        }

        // Blocks until the machine is running again. Returns 0 on success, otherwise the Windows error code.
        // Callers must arm a wake-up first (see GuardedHibernate in MainForm).
        public static int Hibernate()
        {
            EnableShutdownPrivilege();
            return SetSuspendState(true, false, false) ? 0 : Math.Max(1, Marshal.GetLastWin32Error());
        }

        // After a timer wake Windows goes back to sleep within minutes unless something asks it to stay up.
        public static void KeepAwake(bool on)
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
        }

        // Second, independent wake-up: a one-time scheduled task allowed to wake the computer. It does nothing when
        // it runs (rundll32 without arguments exits at once); its only job is to power the laptop on.
        public static bool ScheduleWakeTask(DateTime at)
        {
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>ThinkPad Monitor: powers the laptop back on after a hibernate. Removed again after waking.</Description></RegistrationInfo>\r\n" +
                "  <Triggers><TimeTrigger><StartBoundary>" + at.ToString("yyyy-MM-dd'T'HH:mm:ss") + "</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><WakeToRun>true</WakeToRun><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>false</StartWhenAvailable>" +
                "<ExecutionTimeLimit>PT1M</ExecutionTimeLimit><Hidden>true</Hidden><Enabled>true</Enabled></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>%windir%\\System32\\rundll32.exe</Command></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string file = Path.Combine(Path.GetTempPath(), "ThinkPadMonitor-wake.xml");
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode);
                return RunSchtasks("/Create /TN \"" + WakeTaskName + "\" /XML \"" + file + "\" /F") == 0;
            }
            catch (Exception) { return false; }
            finally { try { File.Delete(file); } catch (Exception) { } }
        }

        public static void RemoveWakeTask()
        {
            try { RunSchtasks("/Delete /TN \"" + WakeTaskName + "\" /F"); } catch (Exception) { }
        }

        static int RunSchtasks(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }
    }

    // First wake-up: a timer that powers the machine back on from sleep or hibernate. Keep it alive until after
    // the resume.
    public sealed class WakeTimer : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWaitableTimer(IntPtr attributes, bool manualReset, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] static extern bool CancelWaitableTimer(IntPtr timer);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        const int ERROR_NOT_SUPPORTED = 50;
        IntPtr handle;

        // Arms the timer for an absolute time. False if this machine cannot wake itself.
        public bool Arm(DateTime at)
        {
            handle = CreateWaitableTimer(IntPtr.Zero, true, null);
            if (handle == IntPtr.Zero) return false;
            long due = at.ToUniversalTime().ToFileTimeUtc();
            if (!SetWaitableTimer(handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, true)) return false;
            return Marshal.GetLastWin32Error() != ERROR_NOT_SUPPORTED;
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            CancelWaitableTimer(handle);
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }
}
