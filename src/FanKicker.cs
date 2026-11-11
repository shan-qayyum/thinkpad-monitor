// Kick-start for a sticky fan bearing: cut the drive, slam it back to full, in several patterns, watching the
// tachometer and stopping as soon as the fan turns. OFF phases re-assert every 250 ms so a background
// "keep fan at full" service cannot cut them short. The fan is always left at full speed.

using System;
using System.Threading;

namespace ThinkPadMonitor
{
    public static class FanKicker
    {
        struct Pattern
        {
            public string Name; public int OffMs; public int Pulses;
            public Pattern(string name, int offMs, int pulses) { Name = name; OffMs = offMs; Pulses = pulses; }
        }

        static readonly Pattern[] Patterns =
        {
            new Pattern("off 2 s, then full", 2000, 0),
            new Pattern("12 quick pulses", 300, 12),
            new Pattern("off 6 s, then full", 6000, 0),
            new Pattern("20 fast pulses", 200, 20),
            new Pattern("off 3 s, then full", 3000, 0),
        };

        static bool Spinning()
        {
            int r = EmbeddedController.Read16(Ec.FAN_RPM);
            return r > 0 && r < 0xFFFF;
        }

        static void HoldOff(int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            do { EmbeddedController.Write(Ec.FAN_LEVEL, Ec.FAN_OFF); Thread.Sleep(Math.Min(250, ms)); }
            while (DateTime.Now < end);
        }

        // Returns a one-line result. progress is called with a status line before each step.
        public static string Run(Action<string> progress)
        {
            try
            {
                if (Spinning()) return "Fan is already turning - no kick needed.";
                for (int i = 0; i < Patterns.Length; i++)
                {
                    Pattern p = Patterns[i];
                    progress(string.Format("Kick {0}/{1}: {2}...", i + 1, Patterns.Length, p.Name));
                    if (p.Pulses > 0)
                        for (int k = 0; k < p.Pulses; k++)
                        {
                            EmbeddedController.Write(Ec.FAN_LEVEL, Ec.FAN_FULL); Thread.Sleep(p.OffMs);
                            EmbeddedController.Write(Ec.FAN_LEVEL, Ec.FAN_OFF); Thread.Sleep(p.OffMs);
                        }
                    else
                        HoldOff(p.OffMs);

                    EmbeddedController.Write(Ec.FAN_LEVEL, Ec.FAN_FULL);
                    for (int t = 0; t < 8; t++)
                    {
                        Thread.Sleep(500);
                        // Two readings in a row, so one garbled tach value cannot claim success.
                        if (Spinning()) { Thread.Sleep(300); if (Spinning()) return "Fan started after: " + p.Name + "."; }
                    }
                }
                return "No rotation after " + Patterns.Length + " kick patterns. Fan left at full speed.";
            }
            catch (Exception ex) { return "Kick failed: " + ex.Message; }
            finally { try { EmbeddedController.Write(Ec.FAN_LEVEL, Ec.FAN_FULL); } catch { } }
        }
    }
}
