// User settings, kept as key=value lines in %AppData%\ThinkPadMonitor\settings.ini.

using System;
using System.Collections.Generic;
using System.IO;

namespace ThinkPadMonitor
{
    public static class Settings
    {
        public static string FanMode = "none";      // none | auto | smart | full | 1..7
        public static bool EmergencyEnabled = false;
        public static int EmergencyTemp = 98;       // C
        public static bool AutoLockEnabled = false;
        public static int AutoLockMinutes = 10;
        public static bool LogCsv = false;
        public static bool AlwaysOnTop = false;
        public static long FanCheckAfter = 0;       // ticks of the "Restart fan" click; checked after the next boot

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThinkPadMonitor"); }
        }

        public static string LogFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ThinkPadMonitor"); }
        }

        static string FilePath { get { return Path.Combine(Folder, "settings.ini"); } }

        public static void Load()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (IOException) { return; }                    // first run: keep the defaults
            catch (UnauthorizedAccessException) { return; }

            FanMode = Str(map, "FanMode", FanMode);
            EmergencyEnabled = Bool(map, "EmergencyEnabled", EmergencyEnabled);
            EmergencyTemp = Int(map, "EmergencyTemp", EmergencyTemp, 80, 104);
            AutoLockEnabled = Bool(map, "AutoLockEnabled", AutoLockEnabled);
            AutoLockMinutes = Int(map, "AutoLockMinutes", AutoLockMinutes, 1, 120);
            LogCsv = Bool(map, "LogCsv", LogCsv);
            AlwaysOnTop = Bool(map, "AlwaysOnTop", AlwaysOnTop);
            string v; long l;
            FanCheckAfter = map.TryGetValue("FanCheckAfter", out v) && long.TryParse(v, out l) ? l : 0;
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(FilePath, new[]
                {
                    "FanMode=" + FanMode,
                    "EmergencyEnabled=" + EmergencyEnabled,
                    "EmergencyTemp=" + EmergencyTemp,
                    "AutoLockEnabled=" + AutoLockEnabled,
                    "AutoLockMinutes=" + AutoLockMinutes,
                    "LogCsv=" + LogCsv,
                    "AlwaysOnTop=" + AlwaysOnTop,
                    "FanCheckAfter=" + FanCheckAfter,
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string Str(Dictionary<string, string> m, string key, string fallback)
        {
            string v;
            return m.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
        }

        static bool Bool(Dictionary<string, string> m, string key, bool fallback)
        {
            string v; bool b;
            return m.TryGetValue(key, out v) && bool.TryParse(v, out b) ? b : fallback;
        }

        static int Int(Dictionary<string, string> m, string key, int fallback, int min, int max)
        {
            string v; int i;
            return m.TryGetValue(key, out v) && int.TryParse(v, out i) ? Math.Max(min, Math.Min(max, i)) : fallback;
        }
    }
}
