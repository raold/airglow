# airglow Plan 1b: Releases (MSI installer and GitHub Releases) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship airglow as an unsigned `airglow-<version>-x64.msi` attached to GitHub Releases, built by CI from a version tag, after fixing the one issue parked from plan 1's final review.

**Architecture:** A WiX Toolset 5 project packages self-contained publishes of the service and the app, installs the `Airglow` Windows service with restart-on-failure, a Start menu shortcut and a sign-in Run entry, and upgrades in place. GitHub Actions runs the tests on every push and builds and publishes the MSI on `v*` tags, with an optional signing step that stays off until secrets exist.

**Tech Stack:** .NET 10, WiX Toolset 5.0.2 (`WixToolset.Sdk`, `WixToolset.Util.wixext`), PowerShell 7, GitHub Actions (`windows-latest`, `actions/setup-dotnet`), GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-11-06-airglow-design.md` (plan 1 design) plus the owner's decisions in this plan's Global Constraints.

## Global Constraints

- Everything from plan 1's Global Constraints still applies (C# only, warnings as errors, public repo hygiene, hardware writes need the owner's go-ahead).
- WiX Toolset version `5.0.2` exactly (MS-RL licence; WiX 6+ needs an OSMF EULA acceptance the owner hasn't given).
- MSI is unsigned (owner decision). The release workflow carries a signing step that runs only when the signing secrets exist.
- MSI file name `airglow-<version>-x64.msi`; product name `airglow`; manufacturer `raold`; UpgradeCode `09D9D653-3B61-446F-B4B8-A384707F27DF` (never change it); per-machine; x64.
- Install layout: `[ProgramFiles64Folder]Airglow\Service\` and `[ProgramFiles64Folder]Airglow\App\` — the same paths the plan 1 scripts use.
- Service: name `Airglow`, display name `airglow fan and lighting engine`, LocalSystem, automatic start, failure actions restart after 5 s three times with a 1-day reset, failure actions also on non-crash failures.
- Both publishes are self-contained `win-x64` so the MSI needs no .NET runtime.
- Config in `%ProgramData%\Airglow` is never installed or removed by the MSI.
- Version comes from the tag: `v0.1.0` → `0.1.0`.

## Review Focus

- Upgrading from an older MSI: the service stops, files are replaced, the service restarts, the config survives. Test: Task 3 Step 6 (owner-gated install, then reinstall of a higher version).
- A machine where plan 1's scripts already installed the service: the MSI must not half-install. Expected: documented migration (`uninstall-service.ps1`, then the MSI); the MSI fails cleanly with a readable message if the service already exists. Test: Task 3 Step 6.
- Two tray apps (installer Run entry + an old HKCU Run entry, or a manual launch): only one instance runs; the second brings the first window forward. Test: Task 2 `Second_instance_signals_first_and_exits`.
- PawnIO removed with OpenRGB: the app says why the CPU temperature is missing instead of failing silently. Test: Task 2 `Shows_pawnio_hint_when_cpu_temperature_missing`.
- A release tag on a commit whose tests fail: no MSI is published. Test: Task 4 workflow (`needs: test`).

---

### Task 1: Watchdog survives sleep/resume; first tick watched

Parked from plan 1's final re-review: QueryPerformanceCounter keeps counting through standby, so on resume the watchdog sees the sleep as a hung tick and calls FailFast; and the first tick after start is never watched.

**Files:**
- Modify: `src/Airglow.Service/EngineHost.cs`
- Test: `tests/Airglow.Service.Tests/EngineHostTests.cs` (or the existing watchdog tests' file)

**Interfaces:**
- Consumes: the existing watchdog (injectable hang limit, check interval and fail-fast action).
- Produces: new injectable `FirstTickLimit` (default 60 s).

- [ ] **Step 1: Write the failing tests**
- `Watchdog_rearms_after_its_own_suspension`: ticks complete; then FakeTimeProvider jumps 120 s with no watchdog check in between (simulated sleep); the next check must not fail fast; if no tick then completes for 15 s more, it fails fast.
- `Watchdog_watches_the_first_tick`: no tick completes for 60 s after start → fail-fast called; a first tick completing at 30 s → not called.

- [ ] **Step 2: Run them to make sure they fail**
Run: `dotnet test tests/Airglow.Service.Tests --filter FullyQualifiedName~Watchdog`

- [ ] **Step 3: Implement**
The watchdog remembers when its previous check ran; if the gap since then exceeds 2 × check interval, it re-arms (`Interlocked.CompareExchange` the last-tick timestamp to now) instead of firing. Before the first tick completes, it measures from watchdog start against `FirstTickLimit`.

- [ ] **Step 4: Run the tests and make sure they pass** (then the full suite once)

- [ ] **Step 5: Commit** `Re-arm the watchdog after sleep and watch the first tick`

---

### Task 2: App ready for installed use

**Files:**
- Modify: `src/Airglow.App/App.xaml.cs`, `src/Airglow.App/Autostart.cs`, `src/Airglow.App/Components/Pages/Settings.razor`, `src/Airglow.App/Components/Pages/Overview.razor`
- Create: `src/Airglow.App/SingleInstance.cs`
- Test: `tests/Airglow.App.Tests/SingleInstanceTests.cs`, `tests/Airglow.App.Tests/OverviewTests.cs`, `tests/Airglow.App.Tests/AutostartTests.cs`

**Interfaces:**
- Produces: `sealed class SingleInstance(string name) : IDisposable { bool IsFirst { get; } event Action? ShowRequested; void SignalFirst(); }` (named `Mutex` `Local\airglow-app` plus named `EventWaitHandle` `Local\airglow-app-show`); `Autostart` gains `static bool IsManagedByInstaller { get; }` (true when `HKLM\Software\Microsoft\Windows\CurrentVersion\Run` has value `airglow`), reading through a small injectable registry abstraction for tests.

- [ ] **Step 1: Write the failing tests**
- `Second_instance_signals_first_and_exits`: two `SingleInstance` objects with a unique test name: the first `IsFirst`, the second not; `SignalFirst()` on the second raises `ShowRequested` on the first within 2 s.
- `Shows_pawnio_hint_when_cpu_temperature_missing`: connected state whose temperatures lack `cpu.package` → Overview shows `CPU temperature isn't available. The PawnIO driver may be missing; get it from pawnio.eu.`; with `cpu.package` present, no hint.
- `Autostart_reports_installer_managed`: fake registry with the HKLM value → `IsManagedByInstaller` true; Settings renders the autostart toggle disabled with the text `Starts at sign-in (set by the installer)`.

- [ ] **Step 2: Run them to make sure they fail**

- [ ] **Step 3: Implement**
`App.OnStartup`: if not first, `SignalFirst()` and shut down immediately; otherwise `ShowRequested` shows and activates the main window on the UI thread. Settings: when `IsManagedByInstaller`, the toggle is disabled with that text and the HKCU value is left alone.

- [ ] **Step 4: Run the tests and make sure they pass; smoke-launch twice** (second launch exits, first stays; then kill)

- [ ] **Step 5: Commit** `Run one tray app, defer autostart to the installer, explain a missing CPU sensor`

---

### Task 3: WiX installer and local build script

**Files:**
- Create: `installer/Airglow.Installer.wixproj`, `installer/Package.wxs`, `installer/airglow.ico`
- Create: `scripts/build-msi.ps1`
- Modify: `.gitignore` (add `artifacts/`), `Airglow.slnx` (do NOT add the installer project to the solution; it builds through the script)

**Interfaces:**
- Produces: `scripts/build-msi.ps1 -Version <x.y.z>` → `artifacts/airglow-<x.y.z>-x64.msi`; exit non-zero on any failure.

- [ ] **Step 1: Write `build-msi.ps1`**
Validates `-Version` as `\d+\.\d+\.\d+`; `dotnet publish src/Airglow.Service -c Release -r win-x64 --self-contained true -o artifacts/publish/service`; same for `src/Airglow.App` to `artifacts/publish/app`; `dotnet build installer/Airglow.Installer.wixproj -c Release -p:Version=<v> -p:ServiceDir=<abs> -p:AppDir=<abs> -o artifacts/msi`; copies the MSI to `artifacts/airglow-<v>-x64.msi`. Checks `$LASTEXITCODE` after each native command.

- [ ] **Step 2: Write the WiX project**
`WixToolset.Sdk/5.0.2`, `WixToolset.Util.wixext` 5.0.2, `OutputName` airglow, `Platform` x64, `DefineConstants` passing `ServiceDir`, `AppDir`, `Version`. `Package.wxs`: the Global Constraints values; `MajorUpgrade` (`DowngradeErrorMessage="A newer version of airglow is already installed."`); `MediaTemplate EmbedCab="yes"`; ARP icon `airglow.ico`; service component whose key path is `Airglow.Service.exe` with `ServiceInstall` (LocalSystem, auto, error control normal), `ServiceConfig FailureActionsWhen="failedToStopOrReturnedError" OnInstall="yes" OnReinstall="yes"`, `ServiceConfigFailureActions ResetPeriod="86400"` with three `Failure Action="restartService" Delay="5000"`, `ServiceControl Start="install" Stop="both" Remove="uninstall" Wait="yes"`; the rest of each publish folder through WiX 5 `<Files Include="$(ServiceDir)\**" />` / `$(AppDir)\**` (excluding the exe already authored); Start menu shortcut `airglow` → `App\Airglow.exe`; HKLM Run value `airglow` = `"[AppFolder]Airglow.exe" --tray`. `airglow.ico`: generate once from the tray glyph drawing (16/32/48/256) and commit it.

- [ ] **Step 3: Build**
Run: `pwsh scripts/build-msi.ps1 -Version 0.0.1`
Expected: exit 0, `artifacts/airglow-0.0.1-x64.msi` exists, size reported; ICE validation passes (or the specific ICE warnings are listed in the report).

- [ ] **Step 4: Inspect the MSI read-only**
Use the `WindowsInstaller.Installer` COM object to query the built MSI's `Property` (ProductName, ProductVersion, UpgradeCode), `ServiceInstall`, `ServiceControl`, `MsiServiceConfigFailureActions` and `Registry` tables and print them. Expected: the Global Constraints values.

- [ ] **Step 5: Commit** `Add WiX installer and MSI build script`

- [ ] **Step 6: Install on the owner's machine (controller; owner go-ahead)**
`gsudo pwsh scripts/uninstall-service.ps1` (removes the script-installed service; the cutover-published files are replaced by the MSI), then `gsudo msiexec /i artifacts\airglow-0.0.1-x64.msi /qn /l*v artifacts\install.log`. Check: service `Airglow` running and auto, failure actions set, `GET /api/state` fans in curve/auto, Start menu shortcut present, HKLM Run value present, config unchanged. Then build `-Version 0.0.2` and install it over the top: service restarts, config unchanged, one ARP entry. Rollback if anything fails: `msiexec /x`, then `scripts/install-service.ps1`.

---

### Task 4: CI and release workflows

**Files:**
- Create: `.github/workflows/ci.yml`, `.github/workflows/release.yml`

- [ ] **Step 1: Write `ci.yml`**
On `push` (all branches) and `pull_request`: `windows-latest`, `actions/checkout@v4`, `actions/setup-dotnet@v4` with `dotnet-version: 10.0.x`, `dotnet test Airglow.slnx -c Release`.

- [ ] **Step 2: Write `release.yml`**
On `push` tags `v*`, `permissions: contents: write`. Job `test` (same as CI). Job `msi` (`needs: test`): checkout, setup-dotnet, `pwsh scripts/build-msi.ps1 -Version ${{ <tag without v> }}`, optional signing step `if: ${{ secrets.AZURE_TRUSTED_SIGNING_ENDPOINT != '' }}` (expose the secret through `env` and test the env var, since `secrets` can't be used directly in `if`) using `azure/trusted-signing-action` with endpoint, account, profile and client credentials from secrets, then `gh release create <tag> artifacts/airglow-<v>-x64.msi --title "airglow <v>" --generate-notes` with `GH_TOKEN: ${{ github.token }}`.

- [ ] **Step 3: Validate locally**
Parse both files with Python `yaml.safe_load`; if `actionlint` is available (`winget`/`go` not required), run it; report results.

- [ ] **Step 4: Commit** `Add CI and release workflows`

---

### Task 5: README for installed use

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update README**
Status (fans live; lighting next); "Install" (download the MSI from Releases; unsigned, so SmartScreen → More info → Run anyway; needs the PawnIO driver for CPU temperature; FanControl and L-Connect must not run at the same time — airglow pauses their fans while they do); "Moving from the scripts" (`scripts\uninstall-service.ps1`, then the MSI); "Uninstall" (Apps & features; config stays in `%ProgramData%\Airglow`); "Build" (`scripts/build-msi.ps1`). No personal paths or hostnames.

- [ ] **Step 2: Commit** `Document installing airglow from Releases`
