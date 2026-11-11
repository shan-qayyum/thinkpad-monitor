# Fan kick-start for a ThinkPad X230 with a sticky fan bearing.
#
# Cuts the fan drive (EC 0x2F = 0x00) and slams it back to full (0x40) in several patterns - plain cycles,
# rapid pulses, a long rest - watching the EC tachometer (0x84/0x85, 0xFFFF = no rotation) and stopping as
# soon as the fan is seen turning. Always leaves the fan commanded to full speed.
# During the OFF phases the register is rewritten every 250 ms so the "ThinkPad Fan Full" background
# service (which re-applies full speed every 5 s) cannot cut an OFF phase short.
#
# Usage:  FanKick.ps1            run the kick sequence, show the result in a popup (used by the shortcut)
#         FanKick.ps1 -NoPopup   same, print to console only

param([switch]$NoPopup)
$ErrorActionPreference = 'Stop'

$dll = Join-Path $PSScriptRoot 'inpoutx64.dll'
if (-not ([System.Management.Automation.PSTypeName]'ThinkPadEC').Type) {
    Add-Type -TypeDefinition @"
using System; using System.Diagnostics; using System.Runtime.InteropServices;
public static class ThinkPadEC {
  [DllImport(@"$dll")] public static extern bool IsInpOutDriverOpen();
  [DllImport(@"$dll")] static extern byte DlPortReadPortUchar(ushort port);
  [DllImport(@"$dll")] static extern void DlPortWritePortUchar(ushort port, byte value);
  const ushort DATA = 0x62, CMD = 0x66;
  const int TIMEOUT_MS = 300, RETRIES = 5;
  static bool Wait(int mask, int want) {
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < TIMEOUT_MS) if ((DlPortReadPortUchar(CMD) & mask) == want) return true;
    return false;
  }
  static int TryRead(byte addr) {
    if ((DlPortReadPortUchar(CMD) & 1) != 0) DlPortReadPortUchar(DATA);
    if (!Wait(2, 0)) return -1; DlPortWritePortUchar(CMD, 0x80);
    if (!Wait(2, 0)) return -1; DlPortWritePortUchar(DATA, addr);
    if (!Wait(1, 1)) return -1; return DlPortReadPortUchar(DATA);
  }
  static bool TryWrite(byte addr, byte value) {
    if (!Wait(2, 0)) return false; DlPortWritePortUchar(CMD, 0x81);
    if (!Wait(2, 0)) return false; DlPortWritePortUchar(DATA, addr);
    if (!Wait(2, 0)) return false; DlPortWritePortUchar(DATA, value);
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

$FAN = 0x2F; $OFF = 0x00; $FULL = 0x40
$log = New-Object System.Collections.Generic.List[string]
function Log($m) { $line = '{0:HH:mm:ss.f}  {1}' -f (Get-Date), $m; $log.Add($line); Write-Host $line }   # Write-Host: must not leak into function return values
function Get-Rpm { ([int][ThinkPadEC]::Read(0x85) -shl 8) -bor [ThinkPadEC]::Read(0x84) }
function Test-Spinning($r) { $r -gt 0 -and $r -lt 65535 }
function Set-Off([double]$seconds) {       # hold OFF, re-asserting so the background service cannot override it
    $end = (Get-Date).AddSeconds($seconds)
    do { [ThinkPadEC]::Write($FAN, $OFF); Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $end)
}
function Watch-Full([int]$seconds) {       # command FULL and watch the tach; returns rpm if it starts turning, else -1
    [ThinkPadEC]::Write($FAN, $FULL)
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        Start-Sleep -Milliseconds 500
        $r = Get-Rpm
        if (Test-Spinning $r) { return $r }
    }
    Log ('   no rotation (rpm reads {0})' -f $r)
    return -1
}

$result = $null
try {
    if (-not [ThinkPadEC]::IsInpOutDriverOpen()) { throw "The InpOut driver is not installed. Run InstallDriver.exe in $PSScriptRoot once." }

    $r0 = Get-Rpm
    Log ('start: level=0x{0:X2} rpm={1}' -f [ThinkPadEC]::Read($FAN), $r0)
    if (Test-Spinning $r0) {
        $result = "Fan is already turning ($r0 rpm) - no kick needed."
    }
    else {
        $patterns = @(
            @{ name = 'cycle 1: off 2 s -> full';  off = 2;   pulses = 0  },
            @{ name = 'cycle 2: off 2 s -> full';  off = 2;   pulses = 0  },
            @{ name = 'pulse: 12 x 0.3 s on/off'; off = 0.3; pulses = 12 },
            @{ name = 'rest: off 6 s -> full';     off = 6;   pulses = 0  },
            @{ name = 'pulse: 20 x 0.2 s on/off'; off = 0.2; pulses = 20 },
            @{ name = 'cycle 3: off 3 s -> full';  off = 3;   pulses = 0  }
        )
        foreach ($p in $patterns) {
            Log $p.name
            if ($p.pulses -gt 0) {
                for ($k = 0; $k -lt $p.pulses; $k++) {
                    [ThinkPadEC]::Write($FAN, $FULL); Start-Sleep -Milliseconds ([int]($p.off * 1000))
                    [ThinkPadEC]::Write($FAN, $OFF);  Start-Sleep -Milliseconds ([int]($p.off * 1000))
                }
            }
            else { Set-Off $p.off }
            $rpm = Watch-Full 4
            if ($rpm -gt 0) { $result = "Fan started after '$($p.name)': $rpm rpm."; break }
        }
        if (-not $result) { $result = "No rotation detected after all kick patterns (tachometer still reads none). The fan is left commanded to full speed." }
    }
}
catch { $result = "Error: $($_.Exception.Message)" }
finally { try { [ThinkPadEC]::Write($FAN, $FULL) } catch {} }

Log $result
Add-Content -Path (Join-Path $PSScriptRoot 'FanKick.log') -Value ($log + '')
if (-not $NoPopup) {
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($result, 'Fan Kick', 'OK', 'Information')
}
