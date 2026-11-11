// Windows power-plan and power-state helpers: CPU speed limit ("maximum processor state"), wake timers,
// hibernate, and a timer that powers the machine back on.

using System;
using System.Runtime.InteropServices;

namespace ThinkPadMonitor
{
    public static class PowerPlan
    {
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll", SetLastError = true)] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
        [DllImport("powrprof.dll")] static extern bool IsPwrHibernateAllowed();
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

        static Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static Guid ProcThrottleMax = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        static Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        static Guid RtcWake = new Guid("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d");

        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;

        static bool ActiveScheme(out Guid scheme)
        {
            scheme = Guid.Empty;
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return false;
            scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            LocalFree(p);
            return true;
        }

        static bool Write(Guid sub, Guid setting, uint value)
        {
            Guid scheme;
            if (!ActiveScheme(out scheme)) return false;
            if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value) != 0) return false;
            if (PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value) != 0) return false;
            return PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;   // re-apply so the change takes effect now
        }

        // Maximum processor state of the active power plan (on AC), 1-100, or -1 if it cannot be read.
        public static int GetCpuMaxPercent()
        {
            Guid scheme;
            uint v;
            if (!ActiveScheme(out scheme)) return -1;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref ProcThrottleMax, out v) == 0 ? (int)v : -1;
        }

        // Sets the maximum processor state for both AC and battery. 99 or lower also switches Turbo Boost off.
        public static bool SetCpuMaxPercent(int percent)
        {
            return Write(SubProcessor, ProcThrottleMax, (uint)Math.Max(5, Math.Min(100, percent)));
        }

        public static bool EnableWakeTimers() { return Write(SubSleep, RtcWake, 1); }

        public static bool HibernateAvailable { get { try { return IsPwrHibernateAllowed(); } catch { return false; } } }

        // Blocks until the machine is running again. False if Windows refused.
        public static bool Hibernate() { return SetSuspendState(true, false, false); }

        // After a timer wake Windows goes back to sleep within minutes unless something asks it to stay up.
        public static void KeepAwake(bool on)
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
        }
    }

    // A timer that powers the machine back on from sleep or hibernate. Keep it alive until after the resume.
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
        public bool Arm(int secondsFromNow)
        {
            handle = CreateWaitableTimer(IntPtr.Zero, true, null);
            if (handle == IntPtr.Zero) return false;
            long due = DateTime.UtcNow.AddSeconds(secondsFromNow).ToFileTimeUtc();
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
