# airglow Plan 1: Engine, Fans and App Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows service that runs fan curves for the 4 Lian Li UNI HUB AL groups and both RTX 4090 fans, plus a tray desktop app to watch and edit them, so FanControl and L-Connect 3 can be switched off.

**Architecture:** `Airglow.Core` holds pure logic (curves, hysteresis, output shaping, config, engine) behind hardware interfaces. `Airglow.Hardware` implements those interfaces with LibreHardwareMonitorLib (temperatures, 4090 fans) and a Windows HID driver for the Lian Li hub. `Airglow.Service` hosts the engine as a LocalSystem Windows service with an HTTP API on a named pipe; `Airglow.App` (WPF + BlazorWebView, tray icon) is the UI and talks only to that API.

**Tech Stack:** .NET 10 (`net10.0-windows`, x64), C# latest, LibreHardwareMonitorLib 0.9.6, ASP.NET Core minimal APIs on Kestrel named pipes, Microsoft.Extensions.Hosting.WindowsServices, WPF + Microsoft.AspNetCore.Components.WebView.Wpf, xUnit, bUnit, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-11-06-airglow-design.md`

## Global Constraints

- Every project targets `net10.0-windows`, `PlatformTarget` x64, `Nullable` enable, `ImplicitUsings` enable, `TreatWarningsAsErrors` true (set once in `Directory.Build.props`).
- C# for everything: no hand-written JavaScript in the UI.
- LibreHardwareMonitorLib version `0.9.6` exactly.
- Lian Li hub: VID `0x0CF2`, PID `0xA101`, usage page `0xFF72`, report ID `0xE0`; feature reports are zero-padded to the device's feature report length (7 on this hub); input reports are read with a buffer of the device's input report length (65).
- Named pipe `airglow-v1`; config file `%ProgramData%\Airglow\config.json`; service name `Airglow`.
- Never write to a device while its old controller runs: `FanControl` pauses GPU fans; `L-Connect 3`, `L-Connect-Service` pause Lian Li fans.
- Failsafe: missing curve input holds the last speed, then commands 100% after 5 s. Shutdown: GPU fans to automatic, Lian Li groups to 60%.
- Defaults copy today's setup (values in Task 4).
- Steps that change real fan speeds or system startup need the owner's go-ahead at that step.
- Commit messages are imperative and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

- A temperature arrives as `NaN` or disappears (GPU driver reset, LHM glitch): treat it as missing, hold, then failsafe; never feed NaN into a curve. Test: Task 2 `Nan_temperature_counts_as_missing`.
- The Lian Li hub vanishes or resets (USB reset, sleep/resume): other fans keep running, the hub reconnects, and the last commands are re-sent. Tests: Task 7 `Backend_fault_is_isolated_and_retried`, `Resends_unchanged_command_every_10s`.
- L-Connect or FanControl starts while the engine runs (e.g. autostart after an update): the affected fans pause within one tick. Test: Task 7 `Paused_when_conflicting_process_running`.
- The UI saves a bad config (unsorted points, unknown curve, mix cycle): the API rejects it with readable errors and the engine keeps the running config. Test: Task 8 `Put_invalid_config_keeps_running_config`.
- The UI opens while the service is stopped: it shows a clear "engine not running" state and reconnects by itself. Test: Task 9 `Shows_engine_down_banner_when_disconnected`.

## File Structure

```
Airglow.slnx, Directory.Build.props, global.json, .gitignore
src/Airglow.Core/
  Curves/FanCurve.cs           curve records, CurvePoint, HysteresisSettings, MixFunction
  Curves/CurveMath.cs          graph interpolation
  Curves/HysteresisFilter.cs   per-curve temperature filter
  Curves/CurveSet.cs           evaluates all curves once per tick
  Curves/CurveEditing.cs       add/move/remove point rules for the editor
  Fans/FanOutputShaper.cs      min/start/stop/offset/step per fan
  Config/AppConfig.cs          config records + CreateDefault()
  Config/ConfigJson.cs         System.Text.Json options
  Config/ConfigValidator.cs
  Config/ConfigStore.cs        load/save with atomic replace
  Hardware/IHardwareBackend.cs IHardwareBackend, IFanChannel, SensorInfo, IBackendFactory, IConflictDetector
  Engine/FanEngine.cs
  Engine/EngineState.cs        telemetry records
  AirglowPaths.cs                pipe name, config path
src/Airglow.Hardware/
  Hid/HidNative.cs, Hid/HidDeviceInfo.cs, Hid/IHidDevice.cs, Hid/HidEnumerator.cs, Hid/WindowsHidDevice.cs
  LianLi/AlFirmware.cs, LianLi/LianLiAlHub.cs, LianLi/LianLiAlHubFactory.cs
  Monitoring/LhmBackend.cs, Monitoring/LhmBackendFactory.cs, Monitoring/LhmSensorMap.cs
  ProcessConflictDetector.cs
src/Airglow.Probe/Program.cs     read-only hardware report
src/Airglow.Service/Program.cs, EngineHost.cs, StateCache.cs, PipeAcl.cs
src/Airglow.App/                 WPF host, tray, ServiceClient, LiveState, Razor components, wwwroot
tests/Airglow.Core.Tests/, tests/Airglow.Hardware.Tests/, tests/Airglow.Service.Tests/, tests/Airglow.App.Tests/
scripts/install-service.ps1, uninstall-service.ps1, cutover.ps1, rollback.ps1
```

---

### Task 1: Toolchain, solution and curve math

**Files:**
- Create: `global.json`, `Directory.Build.props`, `.gitignore`, `Airglow.slnx`
- Create: `src/Airglow.Core/Airglow.Core.csproj`, `src/Airglow.Core/Curves/FanCurve.cs`, `src/Airglow.Core/Curves/CurveMath.cs`
- Test: `tests/Airglow.Core.Tests/CurveMathTests.cs`

**Interfaces:**
- Produces (namespace `Airglow.Core.Curves`):
  - `readonly record struct CurvePoint(double Celsius, double Percent)`
  - `sealed record HysteresisSettings(double UpCelsius = 2, double DownCelsius = 2, double ResponseUpSeconds = 1, double ResponseDownSeconds = 1, bool IgnoreAtLimits = true)`
  - `enum MixFunction { Max, Min, Average }`
  - `abstract record FanCurve(string Id, string Name)` with `GraphCurve(string Id, string Name, string SensorId, IReadOnlyList<CurvePoint> Points, HysteresisSettings Hysteresis)`, `FlatCurve(string Id, string Name, int Percent)`, `MixCurve(string Id, string Name, MixFunction Function, IReadOnlyList<string> CurveIds)`
  - `static class CurveMath { static double EvaluateGraph(IReadOnlyList<CurvePoint> points, double celsius); }`

- [ ] **Step 1: Install the .NET 10 SDK (owner approves the UAC prompt)**

Run: `winget install --id Microsoft.DotNet.SDK.10 -e --source winget`
Then: `dotnet --list-sdks`
Expected: a `10.0.x` line.

- [ ] **Step 2: Create the repo and solution**

Run in the repo root: `dotnet new globaljson --sdk-version <installed 10.0.x> --roll-forward latestFeature`, `dotnet new sln -n Airglow`, `dotnet new classlib -o src/Airglow.Core`, `dotnet new xunit -o tests/Airglow.Core.Tests`, add both to the solution, reference Core from the test project, delete template `Class1.cs`/`UnitTest1.cs`. Write `Directory.Build.props` with the Global Constraints properties. Keep `scripts/white.py`, `docs/`, `README.md`, `CLAUDE.md` and `.gitignore` as they are (the repo already exists).

- [ ] **Step 3: Write the failing test**

```csharp
public class CurveMathTests
{
    static readonly CurvePoint[] Gpu = [new(32.1, 0), new(73.2, 100), new(80.9, 100)];

    [Theory]
    [InlineData(20, 0)]
    [InlineData(32.1, 0)]
    [InlineData(52.65, 50)]
    [InlineData(73.2, 100)]
    [InlineData(90, 100)]
    public void EvaluateGraph_interpolates_and_clamps(double celsius, double expected)
        => Assert.Equal(expected, CurveMath.EvaluateGraph(Gpu, celsius), 3);

    [Fact]
    public void EvaluateGraph_single_point_is_constant()
        => Assert.Equal(40, CurveMath.EvaluateGraph([new(50, 40)], 10), 3);
}
```

- [ ] **Step 4: Run it to make sure it fails**

Run: `dotnet test tests/Airglow.Core.Tests --filter FullyQualifiedName~CurveMathTests`
Expected: build error, `CurveMath` not defined.

- [ ] **Step 5: Implement the curve records and `CurveMath.EvaluateGraph`**

Linear interpolation between the two points that bracket `celsius`; below the first point return its percent, above the last return its percent. Points are assumed sorted (the validator enforces it).

- [ ] **Step 6: Run the tests and make sure they pass**

Run: `dotnet test tests/Airglow.Core.Tests --filter FullyQualifiedName~CurveMathTests`
Expected: PASS (6 tests).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Scaffold solution and add curve math" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Hysteresis filter and curve set

**Files:**
- Create: `src/Airglow.Core/Curves/HysteresisFilter.cs`, `src/Airglow.Core/Curves/CurveSet.cs`
- Test: `tests/Airglow.Core.Tests/HysteresisFilterTests.cs`, `tests/Airglow.Core.Tests/CurveSetTests.cs`

**Interfaces:**
- Consumes: Task 1 curve records, `CurveMath`.
- Produces:
  - `sealed class HysteresisFilter(HysteresisSettings settings, TimeProvider time) { double? Current { get; } double? Apply(double? rawCelsius, double minPointCelsius, double maxPointCelsius); }`
  - `sealed record CurveResult(double? Percent, bool InputMissing)`
  - `sealed class CurveSet(IReadOnlyList<FanCurve> curves, TimeProvider time) { IReadOnlyDictionary<string, CurveResult> Evaluate(IReadOnlyDictionary<string, double> temperatures); }`

- [ ] **Step 1: Write the failing tests**

Add package `Microsoft.Extensions.TimeProvider.Testing` to the test project. Use `FakeTimeProvider` and `HysteresisSettings()` defaults (2 °C, 1 s, ignore at limits), min point 30, max point 80.

- `First_sample_applies_immediately`: `Apply(50, 30, 80)` returns 50.
- `Rise_below_band_never_applies`: after 50, `Apply(51.9, …)` at +0 s and +5 s returns 50.
- `Rise_applies_after_response_time`: after 50, `Apply(52, …)` returns 50 at +0 s and +0.5 s, and 52 at +1.0 s.
- `Pending_rise_resets_when_back_in_band`: after 50, `Apply(52)` at +0.5 s, `Apply(51)` at +0.7 s, `Apply(52)` at +1.2 s returns 50; at +2.2 s returns 52.
- `Drop_applies_after_response_time`: after 50, `Apply(47.5)` at +1.0 s returns 47.5.
- `Ignores_hysteresis_at_limits`: after 79, `Apply(80.5, 30, 80)` returns 80.5 immediately; after 31, `Apply(29.5, 30, 80)` returns 29.5 immediately.
- `Null_input_holds_current`: after 50, `Apply(null, …)` returns 50.

CurveSet, with `GraphCurve("gpu", …, "gpu.core", [(32.1,0),(73.2,100),(80.9,100)], new())`, `GraphCurve("cpu", …, "cpu.package", [(30,20),(80,100)], new())`, `MixCurve("mix", …, Max, ["gpu","cpu"])`, `FlatCurve("flat", …, 35)`:
- `Evaluates_graph_flat_and_mix`: temps `{gpu.core: 52.65, cpu.package: 80}` gives gpu 50, cpu 100, mix 100, flat 35, all `InputMissing == false`.
- `Missing_sensor_holds_last_value_and_flags_missing`: evaluate with gpu.core 52.65, then with `{}`: gpu `Percent == 50`, `InputMissing == true`; mix `InputMissing == true`.
- `Nan_temperature_counts_as_missing`: second evaluate with `{gpu.core: double.NaN}` behaves the same as missing.
- `Never_seen_sensor_gives_null_percent`: first evaluate with `{}` gives gpu `Percent == null`, `InputMissing == true`.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Airglow.Core.Tests --filter "FullyQualifiedName~HysteresisFilterTests|FullyQualifiedName~CurveSetTests"`
Expected: build error, types not defined.

- [ ] **Step 3: Implement `HysteresisFilter`**

```
if raw is null: return Current
if Current is null: Current = raw; return
if IgnoreAtLimits and (raw >= maxPoint or raw <= minPoint) and raw != Current: Current = raw; clear pending; return
delta = raw - Current
direction = delta >= UpCelsius ? Up : delta <= -DownCelsius ? Down : None
if direction == None: clear pending; return Current
if pending direction != direction: pending = (direction, now)
if now - pendingSince >= (Up ? ResponseUpSeconds : ResponseDownSeconds): Current = raw; clear pending
return Current
```

- [ ] **Step 4: Implement `CurveSet`**

One `HysteresisFilter` per graph curve, created in the constructor, called with the curve's first and last point temperatures as its limits. `Evaluate` computes each curve exactly once per call (memoised recursion for mix; the validator guarantees no cycles). NaN or absent sensor → filter gets `null`, result flags `InputMissing`. Mix ignores members with `Percent == null`, returns `null` if all are null, and is missing if any member is missing.

- [ ] **Step 5: Run the tests and make sure they pass**

Run: same as Step 2. Expected: PASS (11 tests).

- [ ] **Step 6: Commit**

`git commit -m "Add hysteresis filter and curve set"` (plus trailer).

---

### Task 3: Fan output shaper

**Files:**
- Create: `src/Airglow.Core/Fans/FanOutputShaper.cs`
- Create: `src/Airglow.Core/Config/AppConfig.cs` (only the `FanTarget` record now; Task 4 adds the rest)
- Test: `tests/Airglow.Core.Tests/FanOutputShaperTests.cs`

**Interfaces:**
- Produces:
  - `sealed record FanTarget(string FanId, string Name, string? CurveId, int MinPercent = 0, int StartPercent = 0, double? StopBelowPercent = null, int OffsetPercent = 0, int StepUpPercent = 100, int StepDownPercent = 100, int? ManualPercent = null)` (namespace `Airglow.Core.Config`)
  - `sealed class FanOutputShaper(FanTarget target) { int? Last { get; } int Next(double curvePercent); void Reset(int percent); }` (namespace `Airglow.Core.Fans`)

- [ ] **Step 1: Write the failing tests**

`gpu = new FanTarget("gpu.fan1", "GPU fan 1", "gpu", StepUpPercent: 8, StepDownPercent: 8, StopBelowPercent: 1)`; `lianli = new FanTarget("lianli.g1", "Group 1", "lianli-g1", MinPercent: 20, StartPercent: 40)`.
- `First_tick_is_unrestricted`: gpu `Next(55) == 55`.
- `Step_up_is_limited`: gpu `Reset(40)`, `Next(100) == 48`.
- `Step_down_is_limited`: gpu `Reset(48)`, `Next(0) == 40`.
- `Stops_below_threshold`: gpu `Reset(8)`, `Next(0.5) == 0`.
- `Respects_minimum`: lianli `Next(10) == 20`.
- `Kicks_from_stop_with_start_percent`: lianli `Reset(0)`, `Next(25) == 40`.
- `Applies_offset_and_clamps`: `new FanTarget(…, OffsetPercent: 5)` gives `Next(50) == 55` and `Next(99) == 100`.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Airglow.Core.Tests --filter FullyQualifiedName~FanOutputShaperTests`
Expected: build error.

- [ ] **Step 3: Implement `FanOutputShaper.Next`**

```
p = clamp(round(curvePercent) + OffsetPercent, 0, 100)
target = (StopBelowPercent is s and p < s) ? 0 : max(p, MinPercent)
out = Last is null ? target
    : target > Last ? min(target, Last + StepUpPercent)
    : max(target, Last - StepDownPercent)
if Last == 0 and out > 0: out = max(out, StartPercent)
Last = out; return out
```
`Reset(p)` sets `Last = p` (used for manual and failsafe commands).

- [ ] **Step 4: Run the tests and make sure they pass**

Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

`git commit -m "Add fan output shaper"`.

---

### Task 4: Config model, defaults, validation and store

**Files:**
- Modify: `src/Airglow.Core/Config/AppConfig.cs`
- Create: `src/Airglow.Core/Config/ConfigJson.cs`, `src/Airglow.Core/Config/ConfigValidator.cs`, `src/Airglow.Core/Config/ConfigStore.cs`, `src/Airglow.Core/AirglowPaths.cs`
- Test: `tests/Airglow.Core.Tests/ConfigTests.cs`

**Interfaces:**
- Produces (namespace `Airglow.Core.Config` unless noted):
  - `sealed record EngineSettings(int TickMilliseconds = 1000, int FailsafePercent = 100, double FailsafeAfterSeconds = 5, int ShutdownPercent = 60)`
  - `sealed record LianLiGroup(int Index, string Name, int FanCount)` (Index 0-3)
  - `sealed record AppConfig(int Version, EngineSettings Engine, IReadOnlyList<FanCurve> Curves, IReadOnlyList<FanTarget> Fans, IReadOnlyList<LianLiGroup> LianLiGroups) { static AppConfig CreateDefault(); }`
  - `static class ConfigJson { static JsonSerializerOptions Options { get; } }` (camelCase, indented, enums as strings, curves polymorphic with discriminator `"type"`: `graph`, `flat`, `mix`)
  - `static class ConfigValidator { static IReadOnlyList<string> Validate(AppConfig config); }`
  - `sealed record LoadResult(AppConfig Config, string? Problem)`; `sealed class ConfigStore(string path) { LoadResult Load(); void Save(AppConfig config); }` (`Save` throws `ArgumentException` listing validation errors)
  - `static class AirglowPaths { const string PipeName = "airglow-v1"; static string ConfigFile { get; } }` (namespace `Airglow.Core`)

**Default values (from today's FanControl and L-Connect config):**
- Curve `gpu` "4090": graph on `gpu.core`, points `(32.1,0) (73.2,100) (80.9,100)`, hysteresis defaults.
- Curves `lianli-g1`…`lianli-g4` "Group N", graph on `cpu.package`, hysteresis defaults, points:
  - g1 `(0,10) (25,45) (34,52) (48,57) (61,74) (71,98) (100,100)`
  - g2 `(0,10) (25,45) (35,55) (49,68) (61,77) (78,93) (100,100)`
  - g3 `(0,10) (25,45) (35,59) (49,59) (61,59) (78,93) (100,100)`
  - g4 `(0,10) (25,45) (35,55) (49,68) (61,77) (78,93) (100,100)`
- Fans `gpu.fan1` "GPU fan 1", `gpu.fan2` "GPU fan 2": curve `gpu`, `StepUpPercent: 8`, `StepDownPercent: 8`, `StopBelowPercent: 1`.
- Fans `lianli.g1`…`lianli.g4` "Group N": curve `lianli-gN`, other fields default.
- `LianLiGroups`: indexes 0-3, names "Group 1"…"Group 4", `FanCount: 3` each (owner corrects in Settings).
- `Version: 1`, `EngineSettings()` defaults.

**Validation messages (exact):**
- `Duplicate curve id '{id}'`
- `Curve '{id}': needs at least 2 points`
- `Curve '{id}': temperatures must increase from point to point`
- `Curve '{id}': points must be within 0-120 °C and 0-100 %`
- `Curve '{id}': unknown curve '{member}' in mix`
- `Curve '{id}': mix cycle`
- `Curve '{id}': percent must be 0-100` (flat)
- `Fan '{fanId}': unknown curve '{curveId}'`
- `Fan '{fanId}': {Field} must be 0-100` (Min, Start, StepUp, StepDown, Manual; StepUp/StepDown must be 1-100: `{Field} must be 1-100`)
- `Engine: TickMilliseconds must be 250-5000`
- `Lian Li group {index}: FanCount must be 0-4`

- [ ] **Step 1: Write the failing tests**

- `Default_config_is_valid`: `ConfigValidator.Validate(AppConfig.CreateDefault())` is empty.
- `Default_config_copies_current_setup`: default has curve `gpu` with the 3 points above, `lianli-g3` point 4 is `(61,59)`, fan `gpu.fan2` has `StepUpPercent == 8` and `StopBelowPercent == 1`, 4 Lian Li groups with `FanCount == 3`.
- `Rejects_unsorted_points`, `Rejects_unknown_fan_curve`, `Rejects_mix_cycle` (mix `a`→`b`, `b`→`a`), `Rejects_duplicate_curve_id`: each asserts `Contains` of the exact message.
- `Json_round_trips`: serialise default with `ConfigJson.Options`, deserialise, serialise again; the two JSON strings are equal and contain `"type": "graph"`.
- `Load_missing_file_writes_default`: temp dir path; `Load()` returns default with `Problem == null` and the file now exists.
- `Load_corrupt_file_falls_back_and_keeps_copy`: write `"{ not json"`; `Load()` returns default, `Problem` starts with `"config.json was unreadable"`, and a `config.invalid-*.json` file exists beside it.
- `Save_rejects_invalid_config`: `Assert.Throws<ArgumentException>`.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Airglow.Core.Tests --filter FullyQualifiedName~ConfigTests`
Expected: build error.

- [ ] **Step 3: Implement config records, defaults, `ConfigJson`, `ConfigValidator`, `ConfigStore`, `AirglowPaths`**

`ConfigStore.Save` validates, writes `config.json.tmp`, then `File.Move(tmp, path, overwrite: true)`. `Load` also treats validation failure as corrupt (same fallback, `Problem` text `config.json was invalid: …`). `AirglowPaths.ConfigFile` = `%ProgramData%\Airglow\config.json`.

- [ ] **Step 4: Run the tests and make sure they pass**

Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

`git commit -m "Add config model, defaults, validation and store"`.

---

### Task 5: HID layer and Lian Li AL hub driver

**Files:**
- Create: `src/Airglow.Core/Hardware/IHardwareBackend.cs`
- Create: `src/Airglow.Hardware/Airglow.Hardware.csproj` (references Core; `AllowUnsafeBlocks` true for `LibraryImport`)
- Create: `src/Airglow.Hardware/Hid/HidNative.cs`, `HidDeviceInfo.cs`, `IHidDevice.cs`, `HidEnumerator.cs`, `WindowsHidDevice.cs`
- Create: `src/Airglow.Hardware/LianLi/AlFirmware.cs`, `LianLiAlHub.cs`, `LianLiAlHubFactory.cs`
- Test: `tests/Airglow.Hardware.Tests/LianLiAlHubTests.cs`, `tests/Airglow.Hardware.Tests/FakeHidDevice.cs`

**Interfaces:**
- Produces (namespace `Airglow.Core.Hardware`):
  - `sealed record SensorInfo(string Id, string Name, string Group)`
  - `interface IFanChannel { string Id { get; } string Name { get; } bool SupportsAuto { get; } int? Rpm { get; } void SetPercent(int percent); void SetAuto(); }`
  - `interface IHardwareBackend : IDisposable { string Name { get; } IReadOnlyList<IFanChannel> Fans { get; } IReadOnlyList<SensorInfo> Sensors { get; } IReadOnlyDictionary<string, double> Temperatures { get; } void Update(); }`
  - `interface IBackendFactory { string Name { get; } IReadOnlyList<string> ConflictingProcesses { get; } IHardwareBackend? TryCreate(AppConfig config); }`
  - `interface IConflictDetector { IReadOnlySet<string> Running(IEnumerable<string> processNames); }`
- Produces (namespace `Airglow.Hardware.Hid`):
  - `sealed record HidDeviceInfo(string Path, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage, int InputReportLength, int OutputReportLength, int FeatureReportLength, string? Product, string? Serial)`
  - `interface IHidDevice : IDisposable { HidDeviceInfo Info { get; } void SetFeature(ReadOnlySpan<byte> report); int GetInputReport(Span<byte> buffer); }` (`SetFeature` pads to `FeatureReportLength`; throws `IOException` on failure)
  - `static class HidEnumerator { static IReadOnlyList<HidDeviceInfo> Enumerate(); }`
  - `sealed class WindowsHidDevice : IHidDevice { static WindowsHidDevice Open(HidDeviceInfo info); }`
- Produces (namespace `Airglow.Hardware.LianLi`):
  - `sealed record AlFirmware(byte CustomerId, byte ProjectId, byte MajorId, byte MinorId, byte FineTune) { bool IsValid { get; } string Version { get; } }`
  - `sealed class LianLiAlHub : IHardwareBackend { LianLiAlHub(IHidDevice device, IReadOnlyList<LianLiGroup> groups); AlFirmware? Firmware { get; } IReadOnlyList<int> ReadRpms(); void SetGroupPercent(int group, int percent); }`; fan channels `lianli.g1`…`lianli.g4` named from `LianLiGroup.Name`, `SupportsAuto == false`; `Update()` reads RPMs; `Temperatures` and `Sensors` empty.
  - `sealed class LianLiAlHubFactory : IBackendFactory` (Name `"Lian Li UNI HUB AL"`, `ConflictingProcesses` `["L-Connect 3", "L-Connect-Service"]`)

**Protocol (lian-li-linux `ene6k77`, confirmed on this hub 2026-11-06: firmware `E0 50 80 C3 15`, RPM read OK):**
- Firmware: feature `[E0 50 01]`, wait 20 ms, input report (buffer[0] = E0) bytes 1-5 = customer, project, major, minor, fineTune. Valid when customer `E0`, project `50`, major `80`, minor `C3`. AL version: fineTune < 8 → `1.0`; else `n = (fineTune >> 4) * 10 + (fineTune & 0x0F) + 2` → `"{n/10}.{n%10}"` (`0x15` → `1.7`).
- RPM: feature `[E0 50 00]`, wait 20 ms, input bytes 1-8 = 4 big-endian `u16`.
- Speed: feature `[E0, 0x20 | group, 0x00, percent]`, percent clamped 0-100, 20 ms after each.
- Constructor: firmware read only (3 attempts, 2 s apart; continue on failure). Reads never change hub state, so a paused engine can still show RPMs.
- Before the first speed command of each hub instance: motherboard sync off per group `[E0 10 42, 1 << (group + 4), 00 00]`, then fan quantity per group `[E0 10 40, group + 1, FanCount, 00]`. A reconnect creates a new instance, so this re-runs after USB resets and resume.

- [ ] **Step 1: Write the failing tests**

`FakeHidDevice` records every `SetFeature` (as padded byte arrays, length 7) and returns queued input reports.
- `Constructor_only_queries_firmware`: queue `E0 E0 50 80 C3 15`; construct; the only report sent is `E0 50 01 00 00 00 00`.
- `First_speed_command_configures_hub_first`: construct with groups FanCount `[3,3,2,2]`, call `SetGroupPercent(0, 50)` twice; sent reports after the firmware query equal, in order: `E0 10 42 10 00 00 00`, `E0 10 42 20 00 00 00`, `E0 10 42 40 00 00 00`, `E0 10 42 80 00 00 00`, `E0 10 40 01 03 00 00`, `E0 10 40 02 03 00 00`, `E0 10 40 03 02 00 00`, `E0 10 40 04 02 00 00`, `E0 20 00 32 00 00 00`, `E0 20 00 32 00 00 00`.
- `Parses_firmware_version`: `Firmware.IsValid` true, `Firmware.Version == "1.7"`.
- `Reads_rpms_big_endian`: queue `E0 03 FC 04 B0 04 74 04 92`; `ReadRpms()` is `[1020, 1200, 1140, 1170]` and the last report sent is `E0 50 00 00 00 00 00`.
- `Sets_group_percent`: `SetGroupPercent(2, 55)` sends `E0 22 00 37 00 00 00`; `SetGroupPercent(0, 120)` sends `… 64 …` (100); `SetGroupPercent(1, -5)` sends `… 00 …`.
- `Channel_set_percent_targets_its_group`: `Fans[3].SetPercent(70)` sends `E0 23 00 46 00 00 00`; `Fans[3].Id == "lianli.g4"`.
- `Update_fills_channel_rpms`.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Airglow.Hardware.Tests --filter FullyQualifiedName~LianLiAlHubTests`
Expected: build error.

- [ ] **Step 3: Implement the HID layer**

`HidNative` uses `LibraryImport` for `hid.dll` (`HidD_GetHidGuid`, `HidD_GetAttributes`, `HidD_GetPreparsedData`, `HidD_FreePreparsedData`, `HidP_GetCaps`, `HidD_GetProductString`, `HidD_GetSerialNumberString`, `HidD_SetFeature`, `HidD_GetInputReport`), `cfgmgr32.dll` (`CM_Get_Device_Interface_List_Size`, `CM_Get_Device_Interface_List`) and `kernel32.dll` (`CreateFile` with read/write access, share read/write). `Enumerate` skips interfaces it cannot open.

- [ ] **Step 4: Implement `AlFirmware`, `LianLiAlHub`, `LianLiAlHubFactory`**

Factory: find the first `HidDeviceInfo` with VID `0x0CF2`, PID `0xA101`, usage page `0xFF72`; `null` if absent. Hub methods serialise device access with a private lock.

- [ ] **Step 5: Run the tests and make sure they pass**

Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

`git commit -m "Add HID layer and Lian Li AL hub driver"`.

---

### Task 6: LibreHardwareMonitor backend and hardware probe

**Files:**
- Modify: `src/Airglow.Hardware/Airglow.Hardware.csproj` (add `LibreHardwareMonitorLib` 0.9.6)
- Create: `src/Airglow.Hardware/Monitoring/LhmSensorMap.cs`, `LhmBackend.cs`, `LhmBackendFactory.cs`, `src/Airglow.Hardware/ProcessConflictDetector.cs`
- Create: `src/Airglow.Probe/Airglow.Probe.csproj`, `src/Airglow.Probe/Program.cs`, `src/Airglow.Probe/app.manifest` (`requireAdministrator`)
- Test: `tests/Airglow.Hardware.Tests/LhmSensorMapTests.cs`

**Interfaces:**
- Consumes: Task 5 core interfaces, `HidEnumerator`, `LianLiAlHubFactory`.
- Produces:
  - `static class LhmSensorMap { static string? WellKnownId(HardwareType hardware, SensorType type, string name); static string IdFor(ISensor sensor); static int? GpuFanNumber(string name); }`. Well-known ids: (Cpu, Temperature, "CPU Package") → `cpu.package`; (GpuNvidia, Temperature, "GPU Core") → `gpu.core`; (GpuNvidia, Temperature, "GPU Hot Spot") → `gpu.hotspot`; (GpuNvidia, Temperature, "GPU Memory Junction") → `gpu.memory`. Anything else: `"lhm" + sensor.Identifier`. `GpuFanNumber("GPU Fan 2") == 2`, `GpuFanNumber("GPU Core") == null`.
  - `sealed class LhmBackend : IHardwareBackend` (`Computer` with CPU, GPU and motherboard enabled; temperatures for every temperature sensor with a non-null, non-NaN value; GPU fan channels `gpu.fan{n}` from NVIDIA `SensorType.Control` sensors whose name has a `GpuFanNumber`, `SupportsAuto == true`, `SetPercent` → `Control.SetSoftware`, `SetAuto` → `Control.SetDefault`, RPM from the NVIDIA `SensorType.Fan` sensor with the same number).
  - `sealed class LhmBackendFactory : IBackendFactory` (Name `"Sensors and GPU"`, `ConflictingProcesses` `["FanControl"]`)
  - `sealed class ProcessConflictDetector : IConflictDetector` (`Process.GetProcessesByName` per name)

- [ ] **Step 1: Write the failing test**

`LhmSensorMapTests`: the 4 well-known mappings, `(Cpu, Temperature, "Core Max")` → null, `GpuFanNumber` cases above.

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/Airglow.Hardware.Tests --filter FullyQualifiedName~LhmSensorMapTests`
Expected: build error.

- [ ] **Step 3: Implement `LhmSensorMap`, `LhmBackend`, `LhmBackendFactory`, `ProcessConflictDetector`**

`LhmBackend.Update` updates each hardware and its sub-hardware, then rebuilds `Temperatures` and channel RPMs. `Dispose` closes the `Computer`.

- [ ] **Step 4: Run the test and make sure it passes**

Expected: PASS.

- [ ] **Step 5: Implement the probe**

Prints, read-only: every LHM hardware and sensor (identifier, mapped id, name, type, value; controls with `MinSoftwareValue`/`MaxSoftwareValue`), the GPU fan channels found, Lian Li firmware and RPMs (queries only; it never calls `SetGroupPercent`), and all HID devices with usage page ≥ `0xFF00`. Exit code 2 when no GPU fan channels are found.

- [ ] **Step 6: Run the probe elevated and check the hardware**

Run (admin terminal): `dotnet run --project src/Airglow.Probe -c Release`
Expected: `cpu.package` and `gpu.core` with plausible values; `gpu.fan1` and `gpu.fan2` with RPMs; Lian Li `v1.7` and 4 RPMs near 1000-1200.
If CPU temperatures are missing and LHM reports an outdated PawnIO, ask the owner before upgrading PawnIO (installed: 2.0.1).
If no GPU fan channels appear, stop and add a direct NVAPI fan-cooler backend (`NvAPI_GPU_ClientFanCoolersGetInfo/GetStatus/GetControl/SetControl`, interface IDs taken from LibreHardwareMonitor's `NvApi.cs`) behind the same `IFanChannel` shape before continuing.

- [ ] **Step 7: Commit**

`git commit -m "Add LibreHardwareMonitor backend and hardware probe"`.

---

### Task 7: Fan engine

**Files:**
- Create: `src/Airglow.Core/Engine/EngineState.cs`, `src/Airglow.Core/Engine/FanEngine.cs`
- Test: `tests/Airglow.Core.Tests/FanEngineTests.cs`, `tests/Shared/Fakes.cs` (linked into `Airglow.Core.Tests` with `<Compile Include="..\Shared\Fakes.cs" />`; Task 8 links it too)

**Interfaces:**
- Consumes: `CurveSet`, `FanOutputShaper`, `AppConfig`, Task 5 interfaces.
- Produces (namespace `Airglow.Core.Engine`):
  - `enum FanMode { Curve, Manual, Failsafe, Auto, Paused, Unavailable }`
  - `sealed record TempReading(string Id, string Name, double? Celsius)`
  - `sealed record FanReading(string Id, string Name, string? CurveId, double? CurvePercent, int? CommandedPercent, int? Rpm, FanMode Mode, string? Note)`
  - `sealed record EngineState(DateTimeOffset At, IReadOnlyList<TempReading> Temperatures, IReadOnlyList<FanReading> Fans, IReadOnlyList<string> Faults)`
  - `sealed class FanEngine(IReadOnlyList<IBackendFactory> factories, IConflictDetector conflicts, TimeProvider time, ILogger<FanEngine> log) : IDisposable { void ApplyConfig(AppConfig config); EngineState Tick(); void SetManual(string fanId, int? percent); void Shutdown(); IReadOnlyList<SensorInfo> Sensors { get; } }`
  - Constants: `ResendSeconds = 10`, `RetrySeconds = 5`.

- [ ] **Step 1: Write the failing tests**

Fakes: `FakeBackend` (settable temperatures, fan channels, `Update` can be told to throw), `FakeFanChannel` (records `SetPercent`/`SetAuto` calls), `FakeFactory` (counts `TryCreate`), `FakeConflicts` (settable running set). Config = `AppConfig.CreateDefault()`. `FakeTimeProvider`.
- `Tick_commands_curve_output`: gpu.core 52.65 → `gpu.fan1` last call `SetPercent(50)`, reading `Mode == Curve`, `CommandedPercent == 50`.
- `Hands_gpu_back_to_auto_at_zero`: gpu.core 30 → `SetAuto()` called, `Mode == Auto`.
- `Missing_sensor_holds_then_failsafe_after_5s`: tick at 52.65, then ticks without gpu.core at +1…+4 s keep 50; at +5 s `SetPercent(100)` and `Mode == Failsafe`; sensor back at 52.65 → `Mode == Curve`.
- `Paused_when_conflicting_process_running`: running `{"FanControl"}` → no calls on GPU channels, `Mode == Paused`, `Note == "FanControl is running"`; Lian Li channels still commanded.
- `Backend_fault_is_isolated_and_retried`: Lian Li `Update` throws `IOException("gone")` → `Faults` contains `Lian Li UNI HUB AL: gone`, Lian Li fans `Mode == Unavailable`, GPU fans still commanded; `TryCreate` not called again before +5 s, called at +5 s.
- `Resends_unchanged_command_every_10s`: steady temperature: one `SetPercent` at t0, none at +5 s, another at +10 s.
- `Manual_override_bypasses_curve_and_step`: `SetManual("gpu.fan1", 70)` → next tick `SetPercent(70)`, `Mode == Manual`; `SetManual("gpu.fan1", null)` → back to `Curve`.
- `Shutdown_sets_auto_for_gpu_and_shutdown_percent_for_lianli`: GPU channels `SetAuto`, Lian Li channels `SetPercent(60)`, backends disposed.
- `Apply_config_keeps_last_commands`: tick, `ApplyConfig` with the same config, tick → step limit continues from the last command (no jump).

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Airglow.Core.Tests --filter FullyQualifiedName~FanEngineTests`
Expected: build error.

- [ ] **Step 3: Implement `FanEngine`**

One lock guards all public members. `Tick`:
1. Create missing backends (`TryCreate`), at most once per `RetrySeconds` per factory.
2. `Update()` each backend; an exception records `"{factory.Name}: {message}"`, disposes the backend and schedules a retry.
3. Merge temperatures; `CurveSet.Evaluate`.
4. For each configured `FanTarget` with a live channel: paused if any of its factory's `ConflictingProcesses` is running; else manual → `Reset(manual)`; else if input has been missing for ≥ `FailsafeAfterSeconds` or `Percent` is null → `Reset(FailsafePercent)`, `Failsafe`; else `Next(percent)`.
5. Command 0 on a `SupportsAuto` channel → `SetAuto()`, otherwise `SetPercent(cmd)`; only when the command changed or `ResendSeconds` passed. A write exception faults that backend as in 2.
6. Return the snapshot. Shapers survive `ApplyConfig` when the fan's `FanId` is unchanged.

- [ ] **Step 4: Run the tests and make sure they pass**

Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

`git commit -m "Add fan engine with failsafe, pause and reconnect"`.

---

### Task 8: Service host, API and install scripts

**Files:**
- Create: `src/Airglow.Service/Airglow.Service.csproj` (`Microsoft.NET.Sdk.Web`; package `Microsoft.Extensions.Hosting.WindowsServices`; references Core and Hardware)
- Create: `src/Airglow.Service/Program.cs`, `EngineHost.cs`, `StateCache.cs`, `PipeAcl.cs`
- Create: `scripts/install-service.ps1`, `scripts/uninstall-service.ps1`
- Test: `tests/Airglow.Service.Tests/ApiTests.cs` (package `Microsoft.AspNetCore.Mvc.Testing`)

**Interfaces:**
- Consumes: `FanEngine`, `ConfigStore`, `AirglowPaths`, factories from Tasks 5-6.
- Produces (HTTP, JSON with `ConfigJson.Options`):
  - `GET /api/state` → `EngineState`
  - `GET /api/config` → `AppConfig`
  - `PUT /api/config` (body `AppConfig`) → `204`, or `400` with `{ "errors": [string] }`
  - `PUT /api/fans/{fanId}/manual` (body `{ "percent": int? }`) → `204`, `404` for an unknown fan
  - `GET /api/sensors` → `SensorInfo[]`
  - `public partial class Program` for tests.

- [ ] **Step 1: Write the failing tests**

`WebApplicationFactory<Program>` in environment `Testing`: replaces the factories with `FakeFactory` instances (fake GPU and Lian Li backends from Task 7, linked from `tests/Shared/Fakes.cs`), the config path with a temp file, and the tick with 50 ms.
- `Get_state_lists_six_fans`
- `Put_invalid_config_keeps_running_config`: PUT default with `gpu` points reversed → `400`, body errors contain `Curve 'gpu': temperatures must increase from point to point`; `GET /api/config` still returns the original points.
- `Put_valid_config_is_saved_and_applied`: change `gpu.fan1` `StepUpPercent` to 20 → `204`; `GET /api/config` shows 20; file on disk shows 20.
- `Manual_override_shows_in_state`: PUT `{ "percent": 70 }` on `gpu.fan1`; within 1 s `GET /api/state` shows `Mode == Manual`, `CommandedPercent == 70`.
- `Manual_override_unknown_fan_is_404`.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Airglow.Service.Tests`
Expected: build error.

- [ ] **Step 3: Implement the host**

`Program.cs`: `WebApplication.CreateBuilder`, `UseWindowsService(o => o.ServiceName = "Airglow")`; outside `Testing`, Kestrel `ListenNamedPipe(AirglowPaths.PipeName)` with `NamedPipeTransportOptions.PipeSecurity = PipeAcl.Create()` (SYSTEM and Administrators full control, Authenticated Users read/write). `EngineHost : BackgroundService` loads config (a `LoadResult.Problem` goes into `Faults`), applies it, ticks on a `PeriodicTimer` of `TickMilliseconds` into `StateCache`, and calls `Shutdown()` on stop. PUT config: validate → `ConfigStore.Save` → `engine.ApplyConfig`.

- [ ] **Step 4: Run the tests and make sure they pass**

Expected: PASS (5 tests).

- [ ] **Step 5: Write the install and uninstall scripts**

`install-service.ps1` (requires admin; stops with a message otherwise): `dotnet publish src/Airglow.Service -c Release -r win-x64 --self-contained false -o "$env:ProgramFiles\Airglow\Service"`; `sc.exe create Airglow binPath= "<exe>" start= auto DisplayName= "airglow fan and lighting engine"`; `sc.exe failure Airglow reset= 86400 actions= restart/5000/restart/5000/restart/5000`; `sc.exe start Airglow`. Re-running it stops the service, republishes and starts it again. `uninstall-service.ps1` stops and deletes the service.

- [ ] **Step 6: Install and check (owner go-ahead; safe while FanControl and L-Connect run, because the engine pauses those fans)**

Run (admin): `scripts/install-service.ps1`, then `sc.exe query Airglow`.
Expected: `RUNNING`. A test client call to `GET /api/state` over the pipe shows all 6 fans with `Mode == Paused`.

- [ ] **Step 7: Commit**

`git commit -m "Add service host, API and install scripts"`.

---

### Task 9: Desktop app shell and overview

**Files:**
- Create: `src/Airglow.App/Airglow.App.csproj` (`Microsoft.NET.Sdk.Razor`, `OutputType` WinExe, `UseWPF` and `UseWindowsForms` true, package `Microsoft.AspNetCore.Components.WebView.Wpf`; references Core)
- Create: `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `TrayIcon.cs`, `ServiceClient.cs`, `LiveState.cs`, `Components/Routes.razor`, `Components/Layout/MainLayout.razor`, `Components/Pages/Overview.razor`, `wwwroot/index.html`, `wwwroot/css/app.css`, `wwwroot/fonts/{archivo,hanken-grotesk,jetbrains-mono}.woff2`
- Test: `tests/Airglow.App.Tests/OverviewTests.cs` (package `bunit`)

**Interfaces:**
- Consumes: `EngineState`, `AppConfig`, `SensorInfo`, `AirglowPaths.PipeName`, the Task 8 API.
- Produces:
  - `interface IServiceClient { Task<EngineState> GetStateAsync(CancellationToken ct); Task<AppConfig> GetConfigAsync(CancellationToken ct); Task<IReadOnlyList<string>> SaveConfigAsync(AppConfig config, CancellationToken ct); Task SetManualAsync(string fanId, int? percent, CancellationToken ct); Task<IReadOnlyList<SensorInfo>> GetSensorsAsync(CancellationToken ct); }` (`SaveConfigAsync` returns validation errors, empty on success)
  - `sealed class ServiceClient : IServiceClient` (`HttpClient` with a `SocketsHttpHandler.ConnectCallback` that opens `NamedPipeClientStream(".", AirglowPaths.PipeName)`)
  - `sealed class LiveState(IServiceClient client, TimeProvider time) : IDisposable { EngineState? State { get; } bool Connected { get; } event Action? Changed; }` (polls every 1 s; `Connected` false after a failed call, keeps retrying)

- [ ] **Step 1: Write the failing tests**

bUnit with a fake `LiveState` source (`LiveState` takes a fake `IServiceClient`):
- `Renders_a_card_per_fan`: state with the 6 default fans (RPM 1020, commanded 45) → 6 `.fan-card` elements; the first shows `1020` and `45%`.
- `Shows_mode_tags`: a fan with `Mode == Paused` and `Note == "FanControl is running"` shows a tag reading `Paused` and that note.
- `Shows_engine_down_banner_when_disconnected`: client throws → banner text `The airglow engine isn't running.` and no fan cards.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Airglow.App.Tests`
Expected: build error.

- [ ] **Step 3: Implement the client, live state and window**

`MainWindow` hosts a `BlazorWebView` (`HostPage` `wwwroot/index.html`, root component `Routes`). Closing the window hides it; the tray icon (WinForms `NotifyIcon`, tooltip `CPU {x} °C · GPU {y} °C`) has Open and Quit. Started with `--tray`, the window stays hidden.

- [ ] **Step 4: Implement the layout, styles and overview**

`app.css` implements the spec's Visual style section: its light and dark tokens on `:root` (dark under `prefers-color-scheme` and `[data-theme="dark"]`), its type and layout values. Fonts are the Google Fonts woff2 files for Archivo, Hanken Grotesk and JetBrains Mono (all OFL), saved under `wwwroot/fonts`. Header: brand `airglow`, status strip with CPU package, GPU core and engine state (`.dot`, `.dot.bad` when disconnected). Left rail: Overview, Fans, Settings. Overview: a temperatures panel (well-known sensors first) and a grid of `.fan-card`s (name, RPM, commanded % with `.meter`, curve name, mode tag, note).

- [ ] **Step 5: Run the tests and make sure they pass**

Expected: PASS (3 tests).

- [ ] **Step 6: Look at it**

Run: `dotnet run --project src/Airglow.App`
Expected: the window opens in the house style, light and dark both readable, live numbers update each second, fans show `Paused` while FanControl and L-Connect run. Screenshot to the owner.

- [ ] **Step 7: Commit**

`git commit -m "Add desktop app shell and overview"`.

---

### Task 10: Fans and settings views

**Files:**
- Create: `src/Airglow.Core/Curves/CurveEditing.cs`
- Create: `src/Airglow.App/Components/CurveEditor.razor`, `src/Airglow.App/Components/Pages/Fans.razor`, `src/Airglow.App/Components/Pages/Settings.razor`, `src/Airglow.App/Autostart.cs`
- Test: `tests/Airglow.Core.Tests/CurveEditingTests.cs`, `tests/Airglow.App.Tests/FansPageTests.cs`

**Interfaces:**
- Consumes: `IServiceClient`, `LiveState`, `AppConfig` records.
- Produces:
  - `static class CurveEditing { const double MinGapCelsius = 0.5; static IReadOnlyList<CurvePoint> Add(IReadOnlyList<CurvePoint> points, CurvePoint p); static IReadOnlyList<CurvePoint> Move(IReadOnlyList<CurvePoint> points, int index, CurvePoint p); static IReadOnlyList<CurvePoint> Remove(IReadOnlyList<CurvePoint> points, int index); }`
  - `static class Autostart { static bool IsEnabled { get; } static void Set(bool enabled); }` (HKCU `Software\Microsoft\Windows\CurrentVersion\Run`, value `airglow` = `"<exe>" --tray`)

- [ ] **Step 1: Write the failing tests**

`CurveEditingTests`:
- `Add_inserts_in_order`: add `(50,60)` to `[(30,20),(80,100)]` → `[(30,20),(50,60),(80,100)]`.
- `Add_rejects_point_too_close`: adding `(30.3,40)` returns the list unchanged.
- `Move_clamps_between_neighbours`: moving index 1 of `[(30,20),(50,60),(80,100)]` to `(90,120)` → `(79.5,100)`.
- `Move_clamps_to_bounds`: moving index 0 to `(-10,-5)` → `(0,0)`.
- `Remove_keeps_two_points`: removing from a 2-point list returns it unchanged.

`FansPageTests` (bUnit, fake client):
- `Save_sends_edited_config`: change `gpu.fan1` step up to 12, click Save → client received a config with `StepUpPercent == 12`.
- `Shows_server_validation_errors`: client returns `["Curve 'gpu': needs at least 2 points"]` → that text is shown and the form stays dirty.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Airglow.Core.Tests --filter FullyQualifiedName~CurveEditingTests` and `dotnet test tests/Airglow.App.Tests --filter FullyQualifiedName~FansPageTests`
Expected: build errors.

- [ ] **Step 3: Implement `CurveEditing`**

Temperatures stay strictly increasing with at least `MinGapCelsius` between neighbours; bounds 0-120 °C and 0-100 %; values snap to 0.5 °C and 1 %.

- [ ] **Step 4: Implement `CurveEditor.razor`**

SVG chart, 0-110 °C by 0-100 %, gridlines every 10. Drag a point with Blazor pointer events on the SVG (`@onpointerdown` on a point, `@onpointermove`/`@onpointerup` on the SVG), double-click empty space to add, select then Delete key or the remove button to delete. Shows a vertical marker at the sensor's live temperature and the curve's live output. Parameters: `Points`, `PointsChanged`, `LiveCelsius`, `LivePercent`.

- [ ] **Step 5: Implement `Fans.razor` and `Settings.razor`**

Fans: fan list on the left; for the selected fan, curve select, minimum, start, stop-below, offset, step up and down, and a manual override slider with an Auto option (calls `SetManualAsync` immediately). The selected curve's panel shows type, sensor select (from `GetSensorsAsync`), hysteresis fields and the `CurveEditor` (graph), percent (flat) or members and function (mix); plus new/duplicate/delete curve. Save and Revert buttons; Save shows returned errors. Settings: engine settings, Lian Li group names and fan counts, and an autostart toggle using `Autostart`.

- [ ] **Step 6: Run the tests and make sure they pass**

Expected: PASS (7 tests).

- [ ] **Step 7: Check it by hand**

Run the app; drag a 4090 curve point, Save, confirm `GET /api/config` changed; set a manual override to 60 and back to Auto. (Fans stay `Paused` until cutover, so nothing physical changes yet.)

- [ ] **Step 8: Commit**

`git commit -m "Add fans and settings views with curve editor"`.

---

### Task 11: Cutover from FanControl and L-Connect

**Files:**
- Create: `scripts/cutover.ps1`, `scripts/rollback.ps1`

**Interfaces:**
- Consumes: service `Airglow` (Task 8), app `--tray` and `Autostart` (Tasks 9-10).
- Produces: a machine where only airglow controls fans at startup.

- [ ] **Step 1: Write `cutover.ps1`**

Requires admin; dry run by default, `-Go` to act; prints each action. Actions: stop `FanControl` and disable scheduled task `\FanControl`; stop `L-Connect 3`, disable task `\LianLi\L-Connect 3`; stop `LConnectServiceWatcher` then `LConnectService` and set both to `Disabled`; confirm task `\OpenRGB` stays disabled; ensure service `Airglow` is running; set the app autostart value. Ends by printing `GET /api/state` fan modes.

- [ ] **Step 2: Write `rollback.ps1`**

Reverses each action: re-enable the two tasks, set both L-Connect services to `Automatic` and start them, stop service `Airglow` (`uninstall-service.ps1` stays separate), remove the app autostart value.

- [ ] **Step 3: Dry run**

Run (admin): `scripts/cutover.ps1`
Expected: the action list above, nothing changed.

- [ ] **Step 4: Cut over (owner go-ahead)**

Run (admin): `scripts/cutover.ps1 -Go`
Expected: all 6 fans `Mode == Curve` or `Auto`, no `Paused`, no `Faults`.

- [ ] **Step 5: Verify behaviour on the real machine**

- Idle 2 minutes: Lian Li RPMs track the group curves; 4090 in `Auto` (fans stopped) below 32 °C.
- GPU load (a game or benchmark) 3 minutes: `gpu.fan1/2` ramp in 8% steps and follow the curve; CPU load raises the Lian Li groups.
- `sc.exe stop Airglow`: GPU back to automatic, Lian Li groups at about 60%; `sc.exe start Airglow` resumes curves.
- Reboot: the service is running and fans follow curves at login; the app is in the tray.

- [ ] **Step 6: Commit**

`git commit -m "Add cutover and rollback scripts"`. Tell the owner FanControl can be uninstalled, and that L-Connect 3 and OpenRGB stay installed until plan 2 (lighting) is done; the Lian Li hub keeps its last lighting effect meanwhile.
