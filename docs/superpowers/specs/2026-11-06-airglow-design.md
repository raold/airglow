# airglow design

## Intent

One bespoke Windows app replaces FanControl, L-Connect 3 and OpenRGB on this PC so all three can be
uninstalled. The owner wants fan control and lighting for their own hardware with a nice UI, and
nothing else running.

## Hardware in scope

| Device | Interface | Use |
|---|---|---|
| Lian Li UNI HUB AL (`0CF2:A101`, fw v1.7), 4 groups, 10 AL120 fans | HID, usage page `0xFF72`, report ID `0xE0` | fan speed + RPM, lighting |
| MSI RTX 4090 Gaming X Trio (`10DE:2684`, sub `1462:5103`), 2 fan controllers | NVAPI (via LibreHardwareMonitorLib) | fan speed + RPM, GPU temps |
| Intel CPU on Z790 AORUS MASTER X | LibreHardwareMonitorLib (PawnIO) | temperatures |
| 4x G.Skill Trident Z5 RGB, ENE controllers at SMBus `0x70`-`0x73` (i801) | SMBus via PawnIO | lighting |
| MSI GPU lighting, NVAPI I2C on GPU 0 address `0x68` | NVAPI I2C | lighting |
| 2x ITE `048D:5702` RGB Fusion controllers (IT5701 fw 7.0.15.1, IT5702-Z490XE fw 1.11.1.0) | HID feature report `0xCC` | lighting |
| Keychron Q6 Max (`3434:0860`, VIA v12), DOIO (`D010:1601`, VIA v11) | VIA raw HID `0xFF60/0x61` | lighting |

Out of scope: motherboard fan headers (not controlled today), Stream Deck, Yeti X, G603, Windows
Dynamic Lighting.

## Goals

- Fan control for the 4 Lian Li groups and both 4090 fans: graph, flat and mix (max/min/average)
  curves on any temperature sensor; hysteresis up/down in °C with response times and
  "ignore at limits"; per-fan minimum, start (kick from stop), stop-below, offset, step up/down
  per tick and manual override.
- GPU fans below their stop threshold hand back to the card's own automatic mode (its zero-RPM).
- Failsafe: lost sensor holds the last speed, then goes to 100% after 5 s; service stop hands the
  GPU back to automatic and sets the Lian Li groups to a fixed shutdown speed; Windows restarts a
  crashed service.
- Lighting (plan 2): colour picker, static, breathing, heartbeat, rainbow, colour cycle, wave,
  effects synced across all devices, temperature-reactive colour.
- Starts at boot with no UAC prompt; the UI is optional and closing it never stops fan control.
- UI in the owner's house style (see Visual style), following Windows light/dark.

## Visual style

Taken from the owner's existing dashboards.

| Token | Light | Dark |
|---|---|---|
| `--bg` | `#edf0f3` | `#0e141a` |
| `--surface` | `#ffffff` | `#151d25` |
| `--surface-2` | `#f5f7f9` | `#1b252f` |
| `--ink` / `--ink-2` / `--ink-3` | `#14202b` / `#455463` / `#758291` | `#e2e8ee` / `#a6b2be` / `#74818e` |
| `--line` | `#d8dee5` | `#26323e` |
| `--accent` / `--accent-ink` | `#0d6470` / `#ffffff` | `#4fb2bd` / `#062a2f` |
| `--ok` / `--warn` / `--bad` | `#2c7a4d` / `#b06a0c` / `#c2352a` | `#5dbe84` / `#e2a744` / `#ef6a5a` |
| `--muted` / `--info` / `--live` | `#7a8197` / `#2a68a8` / `#1f7a52` | `#9aa1b6` / `#6ba6e6` / `#57c08c` |

- Type: display Archivo (brand 750 weight, 19px, width 112%), body Hanken Grotesk 15px/1.5,
  numbers and ids JetBrains Mono with tabular figures. Radius 10px.
- Header: sticky, `--surface` with a 1px `--line` bottom border; brand mark left, live status strip
  right (stat label 10.5px uppercase, letter-spacing 0.08em, `--ink-3`; value 14px semibold).
- Shell: max width 1360px, 16px side gutter; left rail 188px wide (nav items 9px 12px padding,
  8px radius, 14px semibold; current item on `--surface` with a 3px inset `--accent` bar);
  content in `--surface` panels on the `--bg` ground; rail becomes a tab row on narrow windows.
- Status dot 8px with a 3px halo at 22% of its colour; meters 64x6px pills filled with `--accent`.

## Non-goals

- Supporting hardware other than the table above.
- Remote access; the UI talks to the engine only on this machine.

## Anti-goals

- Fans left slow or stopped because the app hung, crashed or lost a sensor.
- Two controllers fighting over one fan: the engine pauses a device while its old controller app
  is running.

## Decisions (owner)

- C# for everything.
- Bespoke replacement; owner uninstalls FanControl, L-Connect 3 and OpenRGB afterwards.

## Decisions (builder's calls)

- .NET 10. Windows service (LocalSystem) owns all hardware; WPF + BlazorWebView desktop app with a
  tray icon is the UI; they talk over a local named pipe (`airglow-v1`) with an HTTP API.
- LibreHardwareMonitorLib 0.9.6 for temperatures and 4090 fan control; own drivers for everything
  else, using lian-li-linux (MIT) and OpenRGB as protocol references.
- Defaults copied from today's setup: 4090 curve `(32.1,0) (73.2,100) (80.9,100)` on GPU core,
  hysteresis 2/2 °C, response 1/1 s, step 8%; Lian Li group curves from L-Connect, assumed to
  follow CPU package temperature.
- Delivered as two plans: (1) engine, fans and app shell; (2) lighting.
