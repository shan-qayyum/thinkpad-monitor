# ThinkPad Monitor

A tiny Windows dashboard + tray app for older ThinkPads that talks **directly to the embedded controller (EC)**,
so it shows what the laptop's firmware actually sees, including the things general-purpose monitors miss:

| Metric | Source |
|---|---|
| CPU temperature | EC register `0x78` |
| Fan speed (RPM) and **stall detection** | EC tachometer `0x84/0x85` (`0xFFFF` = not turning) |
| Fan mode (auto / level / full speed) | EC register `0x2F` |
| CPU clock, plus a **"throttled by firmware" flag** (minimum clock while busy) | Windows PDH counters |
| CPU load, AC / battery | Windows |
| ThinkLight on/off | EC register `0x3B` bit 1 |

Built for a **ThinkPad X230** whose fan bearing was failing and whose keyboard was dead. Nothing else in one place
showed fan stalls, the firmware's speed clamp and a ThinkLight switch at once, so this was written.

![dashboard](docs/screenshot.png)

## Features
- Live tiles + a 5-minute temperature/clock graph, with stopped-fan stretches marked in red.
- **Tray icon shows the CPU temperature as a number**; it turns red when the fan is stopped.
- Balloon alerts when the fan stops or the CPU passes 90 °C.
- **Toggle ThinkLight** without the Fn key.
- **Kick-start fan**: cuts and restores fan drive in several patterns, watching the tachometer, for sticky bearings.
- Always-on-top and start-with-Windows (to tray) options.
- Shares the `Global\Access_EC` lock used by TPFanControl and similar tools, so EC commands never collide.

## Requirements
- 64-bit Windows 7-11 with .NET Framework 4.x (already part of Windows).
- A ThinkPad with the classic EC layout (roughly 2008-2013 models: X200-X230, T400-T430, W5xx...).
  **Tested on X230 only.** On other models, check the offsets against your DSDT first.
- The **InpOut** port-I/O driver (included in `third_party/inpout`, MIT licensed).

## Install
1. Run `bin\InstallDriver.exe` once and accept the admin prompt. This installs the InpOut driver; after that the
   app runs without admin rights.
2. Run `bin\ThinkPadMonitor.exe`. Closing the window hides it to the tray; use the tray menu to exit.

## Build
```
build.cmd
```
Uses the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).
No Visual Studio needed. Output: `bin\ThinkPadMonitor.exe`.

## Extra scripts (`scripts/`)
PowerShell tools from the same project. Put them next to `inpoutx64.dll` to use them.
- `ThinkLight.ps1 [toggle|on|off|status]` and `ThinkLight.vbs` (silent launcher for a hotkey shortcut).
- `FanFull.ps1`: keeps the fan at full speed (re-applied every 5 s). `-Mode auto` returns control to the firmware.
- `Install-FanTask.ps1`: registers `FanFull.ps1` as a SYSTEM task at boot plus a resume-from-sleep trigger (admin).
- `FanKick.ps1` / `FanKick.vbs`: the kick-start sequence as a standalone script.

## Warning
This writes to embedded-controller registers. Wrong values can switch off the fan or other hardware.
The offsets here are verified for the X230; use at your own risk on anything else.
A stopped fan is a hardware fault: software can monitor and limit the damage, but the real fix is cleaning,
lubricating or replacing the fan.

## How the offsets were found
They came from the machine's own ACPI DSDT (`HKLM\HARDWARE\ACPI\DSDT`). The EC field definitions name each bit,
for example `KBLT` (keyboard/ThinkLight), `HFSP` (fan speed), `HSPD` (fan RPM) and `TMP0` (CPU temperature),
and they match what Linux's `thinkpad_acpi` uses.

## License
MIT, see `LICENSE`. InpOut is (c) Phil Gibbons / logix4u.net under the MIT license, see `third_party/inpout/license.txt`.
