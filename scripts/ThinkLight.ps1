# ThinkLight switch for a ThinkPad X230 (no backlit keyboard, so no Fn key needed).
#
# The light is owned by the laptop's embedded controller (EC). This machine's own firmware
# table (DSDT) names the switch KBLT = EC RAM byte 0x3B, bit 1. We read that byte, flip the
# bit and write it back, through the InpOut port I/O driver (same technique as TPFanControl).
#
# The EC handshake is done in compiled C# with a tight spin-loop: Windows' own ACPI driver
# also talks to the EC, so the reply byte must be picked up within microseconds or it is lost.
# A lost reply shows up as a timeout and is simply retried.
#
# Usage:  ThinkLight.ps1 [toggle|on|off|status]      (default: toggle)

param([ValidateSet('toggle', 'on', 'off', 'status')][string]$Action = 'toggle')
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

$KBLT_ADDR = 0x3B
$KBLT_BIT  = 0x02

try {
    if (-not [ThinkPadEC]::IsInpOutDriverOpen()) {
        throw "The InpOut driver is not installed. Run InstallDriver.exe in $PSScriptRoot once (it asks for admin), then try again."
    }

    $cur  = [ThinkPadEC]::Read($KBLT_ADDR)
    $isOn = ($cur -band $KBLT_BIT) -ne 0

    if ($Action -eq 'status') {
        if ($isOn) { 'ThinkLight: ON' } else { 'ThinkLight: OFF' }
        return
    }

    $new = switch ($Action) {
        'on'     { $cur -bor $KBLT_BIT }
        'off'    { $cur -band (0xFF -bxor $KBLT_BIT) }
        'toggle' { $cur -bxor $KBLT_BIT }
    }
    [ThinkPadEC]::Write($KBLT_ADDR, [byte]$new)

    $after = [ThinkPadEC]::Read($KBLT_ADDR)
    if (($after -band $KBLT_BIT) -ne 0) { 'ThinkLight: ON' } else { 'ThinkLight: OFF' }
}
catch {
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'ThinkLight', 'OK', 'Error')
    exit 1
}
