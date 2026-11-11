// Minimal wrapper over the Windows PDH API. Used instead of System.Diagnostics.PerformanceCounter, which
// fails outright on machines whose .NET counter-name registry data is damaged ("invalid index"); PDH with
// English counter paths keeps working there and on non-English Windows.

using System;
using System.Runtime.InteropServices;

namespace ThinkPadMonitor
{
    public sealed class PdhCounter : IDisposable
    {
        [StructLayout(LayoutKind.Explicit)]
        struct FmtValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

        const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
        IntPtr query, counter;

        PdhCounter() { }

        // Returns null if the counter does not exist on this machine.
        public static PdhCounter Open(string englishPath)
        {
            var c = new PdhCounter();
            if (PdhOpenQuery(null, IntPtr.Zero, out c.query) != 0) return null;
            if (PdhAddEnglishCounter(c.query, englishPath, IntPtr.Zero, out c.counter) != 0) { c.Dispose(); return null; }
            PdhCollectQueryData(c.query);   // rate counters need a first sample to diff against
            return c;
        }

        // Collects a new sample and returns the value, or -1 if it could not be read.
        public double Next()
        {
            if (PdhCollectQueryData(query) != 0) return -1;
            uint type; FmtValue v;
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out type, out v) != 0 || v.Status > 1) return -1;
            return v.Value;
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
        }
    }
}
