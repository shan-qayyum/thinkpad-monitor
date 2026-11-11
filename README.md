# ThinkPad Monitor

A tiny Windows dashboard + tray app for older ThinkPads that talks **directly to the embedded controller (EC)**,
so it shows what the laptop's firmware actually sees, including the things general-purpose monitors miss:
a fan that has **stopped turning**, and a CPU the firmware has **clamped to minimum speed**.

![dashboard](docs/screenshot.png)

Built for a **ThinkPad X230** whose fan bearing was failing and whose keyboard was dead. Nothing else in one place
showed fan stalls, the firmware's speed clamp and a ThinkLight switch at once, so this was written.

- [Quick start](#quick-start)
- [Using it](#using-it)
- [Compatibility](#compatibility)
- [Troubleshooting](#troubleshooting)
- [Uninstall](#uninstall)
- [Build from source](#build-from-source)
- [Extra scripts](#extra-scripts)
- [How it works](#how-it-works)
- [Contributing](#contributing)

## Quick start
1. Download `ThinkPadMonitor-vX.Y.Z-win-x64.zip` from [Releases](../../releases) and extract it anywhere.
2. Run **`InstallDriver.exe`** once and accept the admin prompt. It installs the InpOut port-I/O driver
   (MIT licensed). After this, the app itself needs no admin rights.
3. Run **`ThinkPadMonitor.exe`**.

Requirements: 64-bit Windows 7-11 with .NET Framework 4.x (already part of Windows), and a supported ThinkPad
(see [Compatibility](#compatibility)).

## Using it

### The tiles
| Tile | What it means |
|---|---|
| **CPU temperature** | Read from the EC. Green below 75 °C, amber from 75 °C, red from 90 °C. Intel's limit for these CPUs is about 105 °C. |
| **Fan** | Live RPM from the fan's tachometer. A red **STOPPED** means the tachometer sees no rotation. If the second line says "Commanded: Full speed" at the same time, the fan is being told to spin but isn't, which usually means a seized bearing or a dead fan. |
| **CPU speed** | Current clock. **"THROTTLED by firmware"** (red) means the CPU is at its minimum clock while it is busy (load of 60 % or more). That is the firmware holding it down, typically because of heat or a failed fan, not normal power saving. "Minimum clock" (amber) at low load is normal. |
| **CPU load** | Total CPU usage across all cores. |
| **Power** | Battery percentage and charging state, or "AC" when no battery is fitted. |
| **ThinkLight** | Whether the ThinkLight (the LED above the screen) is on. |

### The graph
The last 5 minutes of CPU temperature (coloured line, left scale) and CPU clock (blue line, right scale).
A **red strip** along the bottom marks the moments when the fan was not turning.

### Buttons
- **Toggle ThinkLight**: switches the ThinkLight on or off. No Fn key needed.
- **Kick-start fan**: for a sticky fan. It cuts the fan's power and restores it in several patterns
  (about a minute in total), watching the tachometer, and stops as soon as the fan turns. The fan is always
  left at full speed afterwards. This can free a fan that is only sticky; it cannot fix a fully seized bearing.
- **Always on top**: keeps the window above other windows.
- **Start with Windows**: starts the app minimized to the tray when you log in.

### Tray icon
- The icon shows the **CPU temperature as a number**, coloured like the temperature tile. Its background turns
  **red when the fan is stopped**.
- Hover over it for temperature, fan, clock and load. Left-click to open the dashboard. Right-click for
  ThinkLight, Kick-start and **Exit**.
- Closing the window with **X** only hides it to the tray. Use **Exit** in the tray menu to quit.
- You get a pop-up notification when the fan stops or the CPU passes 90 °C.

## Compatibility
The EC register offsets were read from the X230's own firmware tables and match the ones Linux's `thinkpad_acpi`
uses, so most ThinkPads from roughly 2008-2013 with the classic Lenovo EC probably work. Only one model has
actually been tested so far:

| Model | Windows | Status |
|---|---|---|
| X230 | 10 (64-bit) | Works: all readings and controls verified |

Likely candidates (untested): X200/X201/X220, T400/T410/T420/T430, T500/T510/T520/T530, W5xx.
Newer models (roughly 2014 onwards) use a different EC layout and will probably show wrong values.

**Tried it on another model?** Please [open an issue](../../issues) with your model, your Windows version and
whether the temperature, fan RPM and ThinkLight readings look right. This table will be updated.

## Troubleshooting

**"Windows protected your PC" (SmartScreen) when starting the app.**
The `.exe` is not code-signed, which costs money for a small free project. Click **More info → Run anyway**.
You can check the download against the SHA-256 checksum in the release notes, or build it from source yourself.

**My antivirus flags `InstallDriver.exe` or `inpoutx64.sys`.**
InpOut is a well-known open-source driver that lets programs read and write hardware ports. Because malware can
misuse that kind of access, some antivirus products flag it on principle. It is the same driver used by many
hardware tools. If you are not comfortable with it, do not install it: the app cannot read the EC without it.

**The tiles say "n/a" or "EC unavailable", or the status line says the InpOut driver was not found.**
The driver is not installed or not running. Run `InstallDriver.exe` again and accept the admin prompt. Make
sure `inpoutx64.dll` is in the same folder as `ThinkPadMonitor.exe`.

**CPU speed says "counter unavailable".**
Windows performance counters are disabled or damaged. In an admin command prompt, run `lodctr /r`, then restart.

**The readings look wrong (temperature 0 or 255, nonsense RPM).**
Your model probably uses a different EC layout. Stop using the Kick-start and ThinkLight buttons, since those
write to the EC, and please report the model in an issue.

**The fan shows STOPPED but I can hear it running.**
Some fans have no working tachometer signal. Please report it in an issue together with your model.

**Another fan tool (e.g. TPFanControl) is running.**
They can run side by side: both use the shared `Global\Access_EC` lock, so their commands never collide.
But if both try to control the fan, the last one to write wins.

## Uninstall
1. Right-click the tray icon → **Exit**. Untick **Start with Windows** first if you enabled it, or delete the
   `ThinkPadMonitor` value under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`.
2. Delete the app folder.
3. To remove the InpOut driver as well, run in an **admin** command prompt:
   ```
   sc stop inpoutx64
   sc delete inpoutx64
   del %WINDIR%\System32\drivers\inpoutx64.sys
   ```
   Skip this step if another program uses InpOut.

## Build from source
```
build.cmd
```
Uses the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).
No Visual Studio needed. Output: `bin\ThinkPadMonitor.exe`, with the InpOut files copied next to it.

Source layout:
| File | Purpose |
|---|---|
| `src/EmbeddedController.cs` | EC read/write over ports 0x62/0x66, with the shared `Global\Access_EC` lock |
| `src/Sampler.cs` | EC register map and one snapshot of every metric |
| `src/PdhCounter.cs` | CPU clock and load via the Windows PDH API |
| `src/FanKicker.cs` | The fan kick-start sequence |
| `src/MainForm.cs` | Dashboard window, graph, tray icon and alerts |

## Extra scripts
PowerShell tools in `scripts/`. Put them next to `inpoutx64.dll` to use them.
- `ThinkLight.ps1 [toggle|on|off|status]`, plus `ThinkLight.vbs` (silent launcher for a hotkey shortcut).
- `FanFull.ps1`: keeps the fan at full speed, re-applied every 5 s. `-Mode auto` hands the fan back to the
  firmware, `-Mode status` prints the fan register.
- `Install-FanTask.ps1`: registers `FanFull.ps1` as a SYSTEM task at boot, plus a resume-from-sleep trigger
  (run as admin).
- `FanKick.ps1` / `FanKick.vbs`: the kick-start sequence as a standalone script.

## How it works
ThinkPads expose their EC through the standard ACPI EC ports (`0x62` data, `0x66` command/status). The app sends
the standard read (`0x80`) and write (`0x81`) commands through the InpOut driver. Windows' own ACPI driver uses
the same ports, so the status polling is a tight spin loop and every transaction is retried.

The register offsets come from the machine's own ACPI DSDT (`HKLM\HARDWARE\ACPI\DSDT`), whose EC field
definitions name each bit:

| Name in DSDT | EC offset | Meaning |
|---|---|---|
| `TMP0` | `0x78` | CPU temperature, °C |
| `HFSP` | `0x2F` | Fan control: bits 0-2 level, `0x40` full speed, `0x80` automatic |
| `HSPD` | `0x84`-`0x85` | Fan tachometer, RPM (16-bit), `0xFFFF` = not turning |
| `KBLT` | `0x3B` bit 1 | ThinkLight |

CPU clock and load come from Windows performance counters via PDH, which keeps working even on machines where
the .NET counter API is broken.

## Warning
This writes to embedded-controller registers. Wrong values can switch off the fan or other hardware.
The offsets are verified for the X230 only, so use it at your own risk on anything else.
A stopped fan is a hardware fault: software can monitor it and limit the damage, but the real fix is cleaning,
lubricating or replacing the fan.

## Contributing
Reports from other ThinkPad models are the most useful contribution: see [Compatibility](#compatibility).
Pull requests are welcome; please keep the app dependency-free and buildable with `build.cmd`.

## License
MIT, see `LICENSE`. InpOut is (c) Phil Gibbons / logix4u.net under the MIT license, see
`third_party/inpout/license.txt`.
