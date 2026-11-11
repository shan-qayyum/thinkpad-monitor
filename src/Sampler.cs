// Collects one snapshot of every metric: EC registers (temperature, fan, ThinkLight) plus Windows CPU
// counters and power status. EC offsets come from the ThinkPad X230 DSDT (HKLM\HARDWARE\ACPI\DSDT) and
// match the layout thinkpad_acpi uses on most 2008-2013 ThinkPads.

using System;
using System.Windows.Forms;

namespace ThinkPadMonitor
{
    public static class Ec
    {
        public const byte FAN_LEVEL = 0x2F;    // HFSP: bits 0-2 level, 0x40 full speed, 0x80 automatic
        public const byte FAN_RPM = 0x84;      // HSPD: 16-bit tachometer, 0xFFFF = no rotation
        public const byte CPU_TEMP = 0x78;     // TMP0: degrees C
        public const byte THINKLIGHT = 0x3B;   // KBLT: bit 1
        public const byte THINKLIGHT_BIT = 0x02;

        public const byte FAN_OFF = 0x00, FAN_FULL = 0x40, FAN_AUTO = 0x80;
    }

    public class Sample
    {
        public DateTime Time;
        public bool EcOk;
        public string EcError;
        public int CpuTemp;          // C
        public int FanLevel;         // raw 0x2F
        public int FanRpm;           // -1 when not turning
        public bool ThinkLightOn;
        public int CpuMhz;
        public int CpuMaxMhz;
        public int CpuLoad;          // % processor time
        public bool OnAc;
        public int BatteryPercent;   // -1 when no battery
        public string BatteryState;

        public bool FanSpinning { get { return FanRpm > 0; } }
        public bool CpuAtMinimum { get { return CpuMhz > 0 && CpuMhz <= 1300; } }
        // At minimum clock while busy = something is holding the CPU down, not idle power saving. The caller
        // decides whether that is the firmware (PROCHOT) or the user's own CPU limit.
        public bool HeldAtMinimum { get { return CpuAtMinimum && CpuLoad >= 60; } }

        public string FanModeText
        {
            get
            {
                if ((FanLevel & 0x80) != 0) return "Automatic";
                if ((FanLevel & 0x40) != 0) return "Full speed";
                return FanLevel == 0 ? "Off" : "Level " + (FanLevel & 0x07);
            }
        }
    }

    public class Sampler : IDisposable
    {
        readonly PdhCounter freq, load;
        readonly int maxMhz;

        public Sampler()
        {
            freq = PdhCounter.Open(@"\Processor Information(_Total)\Processor Frequency");
            load = PdhCounter.Open(@"\Processor Information(_Total)\% Processor Time")
                ?? PdhCounter.Open(@"\Processor(_Total)\% Processor Time");
            maxMhz = ReadMaxMhz();
        }

        static int ReadMaxMhz()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    return key == null ? 0 : Convert.ToInt32(key.GetValue("~MHz", 0));
            }
            catch { return 0; }
        }

        public int MaxMhz { get { return maxMhz; } }

        public Sample Take()
        {
            var s = new Sample { Time = DateTime.Now, CpuMaxMhz = maxMhz };
            try
            {
                s.CpuTemp = EmbeddedController.ReadStable(Ec.CPU_TEMP, 2);
                s.FanLevel = EmbeddedController.ReadStable(Ec.FAN_LEVEL, 0);
                int rpm = EmbeddedController.Read16Stable(Ec.FAN_RPM, 300);
                s.FanRpm = (rpm == 0 || rpm == 0xFFFF) ? -1 : rpm;
                s.ThinkLightOn = (EmbeddedController.Read(Ec.THINKLIGHT) & Ec.THINKLIGHT_BIT) != 0;
                s.EcOk = true;
            }
            catch (Exception ex) { s.EcError = ex.Message; }

            s.CpuMhz = freq == null ? 0 : Math.Max(0, (int)freq.Next());
            s.CpuLoad = load == null ? 0 : (int)Math.Round(Math.Min(100, Math.Max(0, load.Next())));

            PowerStatus p = SystemInformation.PowerStatus;
            s.OnAc = p.PowerLineStatus == PowerLineStatus.Online;
            if ((p.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0)
            {
                s.BatteryPercent = -1; s.BatteryState = "No battery";
            }
            else
            {
                s.BatteryPercent = (int)Math.Round(p.BatteryLifePercent * 100);
                s.BatteryState = (p.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0 ? "Charging"
                               : s.OnAc ? "Plugged in" : "On battery";
            }
            return s;
        }

        public void Dispose()
        {
            if (freq != null) freq.Dispose();
            if (load != null) load.Dispose();
        }
    }
}
