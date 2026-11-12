// Windows power-state helpers: hibernate (for the emergency protection), restart, and the time of the last boot.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ThinkPadMonitor
{
    public static class PowerPlan
    {
        [DllImport("powrprof.dll", SetLastError = true)] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
        [DllImport("powrprof.dll")] static extern bool IsPwrHibernateAllowed();
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int length, IntPtr previous, IntPtr returnLength);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TokenPrivilege { public int Count; public long Luid; public int Attributes; }

        public static bool HibernateAvailable { get { try { return IsPwrHibernateAllowed(); } catch { return false; } } }

        public static DateTime LastBoot { get { return DateTime.Now.AddMilliseconds(-(double)GetTickCount64()); } }

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
        public static int Hibernate()
        {
            EnableShutdownPrivilege();
            return SetSuspendState(true, false, false) ? 0 : Math.Max(1, Marshal.GetLastWin32Error());
        }

        // Starts a normal Windows restart. Programs get the usual chance to save or object. False if it could not start.
        public static bool Restart()
        {
            try
            {
                var psi = new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false };
                using (Process p = Process.Start(psi)) { p.WaitForExit(10000); return p.ExitCode == 0; }
            }
            catch (Exception) { return false; }
        }
    }
}
