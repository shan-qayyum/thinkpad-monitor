// Locks the PC after a stretch with no keyboard, mouse or touchpad input, even while a video is playing.
// Windows' own idle lock never fires during video playback, because the player tells Windows the display is
// in use; this counts only real input, so falling asleep in front of a video still ends in a locked PC.

using System;
using System.Runtime.InteropServices;

namespace ThinkPadMonitor
{
    public class IdleLock
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LastInputInfo { public uint Size; public uint Time; }

        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);
        [DllImport("user32.dll")] static extern bool LockWorkStation();

        public const int WarningSeconds = 30;

        // Replaceable so tests can check the decisions without locking anyone's screen.
        public Func<int> IdleSecondsSource = ReadIdleSeconds;
        public Func<bool> LockAction = LockWorkStation;

        public bool SessionLocked;           // set from SystemEvents.SessionSwitch
        bool warned;

        public static int ReadIdleSeconds()
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
            if (!GetLastInputInfo(ref info)) return 0;
            return (int)(unchecked((uint)Environment.TickCount - info.Time) / 1000);
        }

        public enum Step { Nothing, Warn, Locked }

        // Called once a second. Returns what happened so the caller can notify the user.
        public Step Tick(bool enabled, int minutes)
        {
            if (!enabled || SessionLocked) { warned = false; return Step.Nothing; }
            int idle = IdleSecondsSource();
            int limit = minutes * 60;
            if (idle < limit - WarningSeconds) { warned = false; return Step.Nothing; }
            if (idle < limit)
            {
                if (warned) return Step.Nothing;
                warned = true;
                return Step.Warn;
            }
            warned = false;
            if (!LockAction()) return Step.Nothing;
            SessionLocked = true;            // SessionSwitch confirms it; this stops a second lock in the meantime
            return Step.Locked;
        }
    }
}
