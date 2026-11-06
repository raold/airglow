# airglow

Fan control and lighting for one Windows PC, in one C# app. It replaces three tools that each
did part of the job: FanControl (GPU fans), Lian Li L-Connect 3 (case fans and their lighting) and
OpenRGB (everything else that lights up).

> **Status:** early. The design and the first plan (engine, fans and app) are written; the code is
> being built task by task. Lighting comes in a second plan.

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

## Building

Needs Windows 11 and the .NET 10 SDK.

```bash
dotnet build Airglow.slnx
dotnet test Airglow.slnx
```

Reading CPU temperatures, installing the service and switching over from the old apps need an
admin shell. The `scripts/` folder will hold the install, cut-over and rollback scripts.

`scripts/white.py` is a stopgap from before airglow: it sets every light to static white through
OpenRGB (`uv run scripts/white.py`).

## Credits

Protocol knowledge comes from these projects; airglow's code is its own.

- [lian-li-linux](https://github.com/sgtaziz/lian-li-linux) (MIT): Lian Li UNI HUB protocol.
- [uni-sync](https://github.com/EightB1ts/uni-sync): Lian Li fan commands.
- [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (GPL-2.0): lighting protocols for the
  DIMMs, GPU and motherboard.
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0):
  used as a library for sensors and GPU fans.
