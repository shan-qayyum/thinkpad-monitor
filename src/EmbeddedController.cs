// ThinkPad embedded controller (EC) access through the ACPI EC ports 0x62/0x66, using the InpOut driver.
//
// Every transaction holds the cross-process mutex "Global\Access_EC" (the name TPFanControl and other
// ThinkPad tools use), so this app, background scripts and other EC tools never interleave commands.
// Status polling is a tight spin: Windows' own ACPI driver also services the EC, and a reply byte that
// is not collected within microseconds is lost. A lost reply is a timeout and is simply retried.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace ThinkPadMonitor
{
    public static class EmbeddedController
    {
        [DllImport("inpoutx64.dll")] static extern bool IsInpOutDriverOpen();
        [DllImport("inpoutx64.dll")] static extern byte DlPortReadPortUchar(ushort port);
        [DllImport("inpoutx64.dll")] static extern void DlPortWritePortUchar(ushort port, byte value);

        const ushort DATA = 0x62, CMD = 0x66;          // status bits: 0 = output buffer full, 1 = input buffer full
        const byte RD_EC = 0x80, WR_EC = 0x81;
        const int TIMEOUT_MS = 300, RETRIES = 5, MUTEX_WAIT_MS = 500;
        const string MUTEX_NAME = @"Global\Access_EC";

        static Mutex mutex;

        public static bool DriverAvailable()
        {
            try { return IsInpOutDriverOpen(); }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        static Mutex GetMutex()
        {
            if (mutex != null) return mutex;
            try
            {
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                mutex = new Mutex(false, MUTEX_NAME, out created, security);
            }
            catch (UnauthorizedAccessException)
            {
                // Created by a SYSTEM process with a stricter ACL: open it with just the rights we need.
                mutex = Mutex.OpenExisting(MUTEX_NAME, MutexRights.Synchronize | MutexRights.Modify);
            }
            return mutex;
        }

        static T Locked<T>(Func<T> body)
        {
            Mutex m = GetMutex();
            bool owned;
            try { owned = m.WaitOne(MUTEX_WAIT_MS); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new TimeoutException("Another program is holding the EC lock.");
            try { return body(); }
            finally { m.ReleaseMutex(); }
        }

        static bool Wait(int mask, int want)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < TIMEOUT_MS)
                if ((DlPortReadPortUchar(CMD) & mask) == want) return true;
            return false;
        }

        static int TryRead(byte addr)
        {
            if ((DlPortReadPortUchar(CMD) & 1) != 0) DlPortReadPortUchar(DATA);   // drain a stale byte
            if (!Wait(2, 0)) return -1; DlPortWritePortUchar(CMD, RD_EC);
            if (!Wait(2, 0)) return -1; DlPortWritePortUchar(DATA, addr);
            if (!Wait(1, 1)) return -1; return DlPortReadPortUchar(DATA);
        }

        static bool TryWrite(byte addr, byte value)
        {
            if (!Wait(2, 0)) return false; DlPortWritePortUchar(CMD, WR_EC);
            if (!Wait(2, 0)) return false; DlPortWritePortUchar(DATA, addr);
            if (!Wait(2, 0)) return false; DlPortWritePortUchar(DATA, value);
            return Wait(2, 0);
        }

        public static byte Read(byte addr)
        {
            return Locked(delegate
            {
                for (int i = 0; i < RETRIES; i++) { int v = TryRead(addr); if (v >= 0) return (byte)v; }
                throw new TimeoutException(string.Format("EC did not answer a read of 0x{0:X2}.", addr));
            });
        }

        public static void Write(byte addr, byte value)
        {
            Locked(delegate
            {
                for (int i = 0; i < RETRIES; i++) if (TryWrite(addr, value)) return true;
                throw new TimeoutException(string.Format("EC did not accept a write to 0x{0:X2}.", addr));
            });
        }

        // Reads until two consecutive values agree within the tolerance, so a single garbled reply (possible when
        // another program talks to the EC without taking the shared lock) is never reported.
        public static int ReadStable(byte addr, int tolerance)
        {
            int a = Read(addr);
            for (int i = 0; i < 3; i++)
            {
                int b = Read(addr);
                if (Math.Abs(a - b) <= tolerance) return b;
                a = b;
            }
            return a;
        }

        public static int Read16Stable(byte lowAddr, int tolerance)
        {
            int a = Read16(lowAddr);
            for (int i = 0; i < 3; i++)
            {
                int b = Read16(lowAddr);
                if (Math.Abs(a - b) <= tolerance) return b;
                a = b;
            }
            return a;
        }

        // Reads a 16-bit little-endian value from two bytes under one lock, so both halves come from the same moment.
        public static int Read16(byte lowAddr)
        {
            return Locked(delegate
            {
                int lo = -1, hi = -1;
                for (int i = 0; i < RETRIES && lo < 0; i++) lo = TryRead(lowAddr);
                for (int i = 0; i < RETRIES && hi < 0; i++) hi = TryRead((byte)(lowAddr + 1));
                if (lo < 0 || hi < 0) throw new TimeoutException("EC did not answer a 16-bit read.");
                return (hi << 8) | lo;
            });
        }
    }
}
