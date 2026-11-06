# airglow

Fan control and lighting for one Windows PC, in one C# app. It replaces three tools that each
did part of the job: FanControl (GPU fans), Lian Li L-Connect 3 (case fans and their lighting) and
OpenRGB (everything else that lights up).

> **Status:** fan control is live: the Lian Li UNI HUB AL groups and both RTX 4090 fans. Lighting
> is the second plan and not done yet; until then L-Connect 3 or OpenRGB still handle lighting.

## What it controls

| Hardware | Fans | Lighting |
|---|---|---|
| Lian Li UNI HUB AL with 10 AL120 fans in 4 groups | curves, RPM | planned |
| MSI RTX 4090 Gaming X Trio | both fan controllers, curves, RPM | planned |
| Intel CPU (temperatures only) | sensor source | |
| 4x G.Skill Trident Z5 RGB DDR5 | | planned |
| Gigabyte Z790 AORUS MASTER X RGB Fusion (2 controllers) | | planned |
| Keychron Q6 Max and DOIO keyboards (VIA) | | planned |

It is built for this exact machine. Other hardware is out of scope.

## Fan control

- Graph, flat and mix (max, min, average) curves on any temperature sensor.
- Hysteresis in °C with separate up and down response times, ignored at the curve's limits.
- Per fan: minimum, start (kick from a stop), stop-below, offset, step limits per update, manual
  override.
- The 4090 hands back to its own automatic mode (fans stopped) below its stop threshold.
- Failsafe: a lost sensor holds the last speed, then goes to 100% after 5 seconds. Stopping the
  service returns the GPU to automatic and sets the case fans to 60%; Windows restarts a crashed
  service.
- While FanControl or L-Connect is still running, airglow leaves the fans they own alone.

## How it works

- **`Airglow.Service`** is a Windows service that starts at boot and owns all the hardware.
- **`Airglow.App`** is a tray app (WPF + Blazor) for watching and editing; closing it never stops
  fan control. The two talk over a local named pipe.
- **`Airglow.Core`** holds the curve, hysteresis and engine logic; **`Airglow.Hardware`** holds the
  device drivers.

Design: [`docs/superpowers/specs/2026-11-06-airglow-design.md`](docs/superpowers/specs/2026-11-06-airglow-design.md)
Plan 1: [`docs/superpowers/plans/2026-11-06-airglow-fans.md`](docs/superpowers/plans/2026-11-06-airglow-fans.md)

## Install

1. Download `airglow-<version>-x64.msi` from the
   [Releases](https://github.com/raold/airglow/releases) page.
2. Run it. The installer is unsigned, so SmartScreen shows "Windows protected your PC": click
   "More info", then "Run anyway".

It installs to `C:\Program Files\Airglow`, adds the `Airglow` service (starts at boot, restarts
itself on failure) and a Start menu shortcut, and starts the tray app at sign-in. The .NET runtime
is bundled.

CPU temperatures need the PawnIO driver. OpenRGB and FanControl often install it; keep it if you
uninstall those, otherwise get it from [pawnio.eu](https://pawnio.eu). Without it the app shows a
hint, and curves on CPU sensors go to the failsafe speed.

FanControl and L-Connect 3 must not run alongside airglow. While either runs, airglow leaves the
fans that app owns alone (shown as Paused).

### Moving from the scripts

If you installed with the plan 1 scripts:

1. Run `scripts\uninstall-service.ps1` as admin.
2. Delete `C:\Program Files\Airglow\App`.
3. Remove the `airglow` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
4. Install the MSI.

`scripts\rollback.ps1` still restores FanControl and L-Connect 3.

### Uninstall

Settings, Apps, airglow. Your config stays in `%ProgramData%\Airglow`.

## Building

Needs Windows 11 and the .NET 10 SDK.

```bash
dotnet build Airglow.slnx
dotnet test Airglow.slnx
pwsh scripts/build-msi.ps1 -Version 0.1.0   # artifacts/airglow-0.1.0-x64.msi
```

Reading CPU temperatures and installing the service need an admin shell.

To release, push a tag `vX.Y.Z`. The release workflow runs the tests, builds the MSI and attaches it
to a GitHub Release. An optional signing step stays off until signing secrets are configured.

## Lighting

Until lighting lands, `scripts/white.py` sets every light to static white through OpenRGB
(`uv run scripts/white.py`).

## Credits

Protocol knowledge comes from these projects; airglow's code is its own.

- [lian-li-linux](https://github.com/sgtaziz/lian-li-linux) (MIT): Lian Li UNI HUB protocol.
- [uni-sync](https://github.com/EightB1ts/uni-sync): Lian Li fan commands.
- [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (GPL-2.0): lighting protocols for the
  DIMMs, GPU and motherboard.
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0):
  used as a library for sensors and GPU fans.

[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) lists every third-party component the installer
ships (libraries, the .NET runtime, WebView2 and fonts) with its licence; it is also installed next
to both programs.
