// Applies the chosen fan mode once per sample. While the app is controlling the fan it holds the named object
// Global\ThinkPadMonitor_FanControl, which tells the optional FanFull.ps1 background service to stand back.

using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace ThinkPadMonitor
{
    public static class FanController
    {
        const string OwnerName = @"Global\ThinkPadMonitor_FanControl";
        static readonly object gate = new object();
        static Mutex ownerSignal;

        // Set while a kick-start or restart check is driving the fan itself.
        public static volatile bool Suspended;

        // Smart curve: level by temperature, stepping down only once 4 C below the step's threshold.
        static readonly int[] StepTemp = { 0, 56, 64, 72, 80 };
        static readonly byte[] StepLevel = { 1, 3, 5, 7, Ec.FAN_FULL };
        static int step;

        public static void SignalOwner(bool on)
        {
            lock (gate)
            {
                if (on && ownerSignal == null)
                {
                    try
                    {
                        var security = new MutexSecurity();
                        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                            MutexRights.FullControl, AccessControlType.Allow));
                        bool created;
                        ownerSignal = new Mutex(false, OwnerName, out created, security);
                    }
                    catch (UnauthorizedAccessException) { }     // no service can see us; harmless
                }
                else if (!on && ownerSignal != null)
                {
                    ownerSignal.Dispose();
                    ownerSignal = null;
                }
            }
        }

        static byte Target(string mode, int temp)
        {
            if (mode == "auto") return Ec.FAN_AUTO;
            if (mode == "full") return Ec.FAN_FULL;
            if (mode == "smart")
            {
                while (step < StepTemp.Length - 1 && temp >= StepTemp[step + 1]) step++;
                while (step > 0 && temp < StepTemp[step] - 4) step--;
                return StepLevel[step];
            }
            int level;
            return int.TryParse(mode, out level) && level >= 1 && level <= 7 ? (byte)level : Ec.FAN_AUTO;
        }

        // Called after every sample. Writes only when the EC register differs from what the mode wants.
        public static void Apply(Sample s, string mode)
        {
            if (Suspended) return;
            if (mode == "none") { SignalOwner(false); return; }
            SignalOwner(true);
            if (!s.EcOk) return;
            byte want = Target(mode, s.CpuTemp);
            if (s.FanLevel == want) return;
            try { EmbeddedController.Write(Ec.FAN_LEVEL, want); }
            catch (TimeoutException) { }                        // retried on the next sample
        }

        // Hands the fan back to the firmware, e.g. when the app exits while it was holding a manual level.
        public static void Release(string mode)
        {
            SignalOwner(false);
            if (mode == "none") return;
            try { EmbeddedController.Write(Ec.FAN_LEVEL, Ec.FAN_AUTO); }
            catch (Exception) { }
        }
    }
}
