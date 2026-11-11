# Fan control for a ThinkPad X230 via the embedded controller (EC), same route as ThinkLight.ps1.
#
# EC byte 0x2F (HFSP in this machine's DSDT) is the fan speed register:
#   bits 0-2 = level 0..7,  bit 6 (0x40) = full speed / unregulated,  bit 7 (0x80) = automatic (firmware control)
#
# Usage:
#   FanFull.ps1                 keep the fan at full speed forever (re-applies every 5 s; used by the startup task)
#   FanFull.ps1 -Once           set full speed once and exit
#   FanFull.ps1 -Mode auto      hand the fan back to the firmware (also stop the "ThinkPad Fan Full" task)
#   FanFull.ps1 -Mode status    print the current register value

param(
    [ValidateSet('full', 'auto', 'status')][string]$Mode = 'full',
    [switch]$Once,
    [int]$IntervalSeconds = 5
)
$ErrorActionPreference = 'Stop'

$dll = Join-Path $PSScriptRoot 'inpoutx64.dll'
if (-not ([System.Management.Automation.PSTypeName]'ThinkPadEC').Type) {
    Add-Type -TypeDefinition @"
using System; using System.Diagnostics; using System.Runtime.InteropServices;
public static class ThinkPadEC {
  [DllImport(@"$dll")] public static extern bool IsInpOutDriverOpen();
  [DllImport(@"$dll")] static extern byte DlPortReadPortUchar(ushort port);
  [DllImport(@"$dll")] static extern void DlPortWritePortUchar(ushort port, byte value);

  const ushort DATA = 0x62, CMD = 0x66;      // CMD/status bits: 0 = output buffer full, 1 = input buffer full
  const int TIMEOUT_MS = 300, RETRIES = 5;

  static bool Wait(int mask, int want) {
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < TIMEOUT_MS)
      if ((DlPortReadPortUchar(CMD) & mask) == want) return true;
    return false;
  }

  static int TryRead(byte addr) {
    if ((DlPortReadPortUchar(CMD) & 1) != 0) DlPortReadPortUchar(DATA);   // drain a stale byte
    if (!Wait(2, 0)) return -1;
    DlPortWritePortUchar(CMD, 0x80);                                       // RD_EC
    if (!Wait(2, 0)) return -1;
    DlPortWritePortUchar(DATA, addr);
    if (!Wait(1, 1)) return -1;
    return DlPortReadPortUchar(DATA);
  }

  static bool TryWrite(byte addr, byte value) {
    if (!Wait(2, 0)) return false;
    DlPortWritePortUchar(CMD, 0x81);                                       // WR_EC
    if (!Wait(2, 0)) return false;
    DlPortWritePortUchar(DATA, addr);
    if (!Wait(2, 0)) return false;
    DlPortWritePortUchar(DATA, value);
    return Wait(2, 0);
  }

  // Cross-process lock shared with ThinkPadMonitor and other EC tools, so transactions never interleave.
  static System.Threading.Mutex mtx;
  static System.Threading.Mutex Acquire() {
    if (mtx == null) {
      try {
        var sec = new System.Security.AccessControl.MutexSecurity();
        sec.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(
          new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
          System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        bool created; mtx = new System.Threading.Mutex(false, "Global\\Access_EC", out created, sec);
      } catch (UnauthorizedAccessException) {
        mtx = System.Threading.Mutex.OpenExisting("Global\\Access_EC",
          System.Security.AccessControl.MutexRights.Synchronize | System.Security.AccessControl.MutexRights.Modify);
      }
    }
    try { if (!mtx.WaitOne(500)) throw new Exception("Another program is holding the EC lock."); }
    catch (System.Threading.AbandonedMutexException) { }
    return mtx;
  }

  public static byte Read(byte addr) {
    var m = Acquire();
    try {
      for (int i = 0; i < RETRIES; i++) { int v = TryRead(addr); if (v >= 0) return (byte)v; }
      throw new Exception("The embedded controller did not answer a read request.");
    } finally { m.ReleaseMutex(); }
  }

  public static void Write(byte addr, byte value) {
    var m = Acquire();
    try {
      for (int i = 0; i < RETRIES; i++) if (TryWrite(addr, value)) return;
      throw new Exception("The embedded controller did not accept a write request.");
    } finally { m.ReleaseMutex(); }
  }
}
"@
}

$FAN_ADDR = 0x2F
$FAN_FULL = 0x40
$FAN_AUTO = 0x80

if (-not [ThinkPadEC]::IsInpOutDriverOpen()) {
    throw "The InpOut driver is not installed. Run InstallDriver.exe in $PSScriptRoot once (it asks for admin)."
}

switch ($Mode) {
    'status' { 'Fan register 0x2F = 0x{0:X2}' -f [ThinkPadEC]::Read($FAN_ADDR); return }
    'auto'   { [ThinkPadEC]::Write($FAN_ADDR, $FAN_AUTO); 'Fan: automatic (firmware control)'; return }
}

# full: apply, then keep re-applying so the EC cannot drift back to automatic (e.g. after sleep/resume)
do {
    try {
        $cur = [ThinkPadEC]::Read($FAN_ADDR)
        if ($cur -ne $FAN_FULL) {
            [ThinkPadEC]::Write($FAN_ADDR, $FAN_FULL)
            'Fan: full speed (was 0x{0:X2})' -f $cur
        }
    }
    catch { "Fan: EC error - $($_.Exception.Message)" }
    if (-not $Once) { Start-Sleep -Seconds $IntervalSeconds }
} while (-not $Once)
