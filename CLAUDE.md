# airglow

Windows service plus tray app that replaces FanControl, L-Connect 3 and OpenRGB on the owner's PC.
Read the design and the current plan before changing anything:

- Design: `docs/superpowers/specs/2026-11-06-airglow-design.md` (hardware table, goals, visual style)
- Plan 1 (engine, fans, app): `docs/superpowers/plans/2026-11-06-airglow-fans.md`
- Plans are executed with superpowers:subagent-driven-development, one task at a time, ticking the
  checkboxes as steps finish.

## Stack and conventions

- .NET 10, `net10.0-windows`, x64, C# only: no hand-written JavaScript, even in the UI.
- Shared build settings live in `Directory.Build.props` (nullable, implicit usings, warnings as
  errors). Don't override them per project.
- Namespaces `Airglow.Core`, `Airglow.Hardware`, `Airglow.Service`, `Airglow.App`.
- Time comes from `TimeProvider`; tests use `FakeTimeProvider`. Tests are xUnit (and bUnit for
  Razor components); hardware drivers are tested against fake HID devices with exact byte arrays.
- Names: service `Airglow`, pipe `airglow-v1`, config `%ProgramData%\Airglow\config.json`.

## Commands

```bash
dotnet build Airglow.slnx
dotnet test Airglow.slnx
gsudo dotnet run --project src/Airglow.Probe -c Release   # read-only hardware report, needs admin
pwsh scripts/build-msi.ps1 -Version 0.1.0   # artifacts/airglow-0.1.0-x64.msi
# tag vX.Y.Z to release (the release workflow builds and attaches the MSI)
```

Use `gsudo <command>` for anything that needs admin (probe, service install, cut-over).

## Hardware safety

This code drives real fans and lighting chips on the owner's only PC.

- Anything that changes real fan speeds, lighting, Windows services or startup entries needs the
  owner's go-ahead at that moment. Reads and the probe are fine without asking.
- Never write to a device while its old controller runs: FanControl owns the GPU fans,
  L-Connect 3 / L-Connect-Service own the Lian Li hub. The engine pauses those devices; keep it so.
- Stopping the engine must hand the GPU back to automatic and leave the Lian Li fans at the
  shutdown speed. Keep `scripts/rollback.ps1` working.
- SMBus (plan 2): only ever address the ENE DRAM controllers at `0x70`-`0x73`. Never write to any
  other SMBus address; the DDR5 SPD hubs live there too.
- Protocol references: lian-li-linux `crates/lianli-devices/src/ene6k77` (Lian Li AL hub) and
  OpenRGB's controller sources. Confirmed on this hub: firmware reply `E0 50 80 C3 15` (v1.7) and
  big-endian RPM reads for all 4 groups.

## Public repo

This repository is public. Never commit hostnames, IP addresses, device serial numbers, API keys,
personal paths or email addresses. Commits use the repo-local identity
`raold <raold@users.noreply.github.com>`, are small, use imperative mood, and end with the
Co-Authored-By trailer.
