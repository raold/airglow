#Requires -RunAsAdministrator
param([switch]$Go)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'AirglowPipe.psm1') -Force

$appDir = Join-Path $env:ProgramFiles 'Airglow\App'
$appExe = Join-Path $appDir 'Airglow.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$serviceProject = Join-Path $root 'src\Airglow.Service'
$appProject = Join-Path $root 'src\Airglow.App'

$script:CurrentStep = 'preflight'

function Invoke-Step {
    param([string]$Description, [scriptblock]$Action)
    if (-not $Go) { Write-Host "WOULD $Description"; return }
    $script:CurrentStep = $Description
    Write-Host "DOING $Description"
    & $Action
}

function Stop-ProcessByName {
    param([string]$Name)
    foreach ($p in @(Get-Process -Name $Name -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit(15000) | Out-Null
        if (-not $p.HasExited) { throw "$Name (pid $($p.Id)) did not exit." }
    }
}

function Stop-AirglowApp {
    $procs = @(Get-Process -Name Airglow -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -like "$appDir\*" })
    foreach ($p in $procs) {
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit(15000) | Out-Null
        if (-not $p.HasExited) { throw "Airglow.exe (pid $($p.Id)) did not exit." }
    }
}

function Split-TaskPath {
    param([string]$Path)
    $folder = Split-Path $Path -Parent
    if (-not $folder.EndsWith('\')) { $folder += '\' }
    [pscustomobject]@{ Folder = $folder; Name = Split-Path $Path -Leaf }
}

function Disable-Task {
    param([string]$Path)
    $t = Split-TaskPath $Path
    Disable-ScheduledTask -TaskPath $t.Folder -TaskName $t.Name | Out-Null
}

function Test-Preflight {
    function Report([bool]$ok, [string]$what) {
        if ($ok) { Write-Host "OK      $what" } else { Write-Host "MISSING $what"; $script:missing++ }
    }
    $script:missing = 0
    Report ([bool](Get-Command dotnet -ErrorAction SilentlyContinue)) 'dotnet on PATH'
    Report (Test-Path -LiteralPath $serviceProject) 'src\Airglow.Service'
    Report (Test-Path -LiteralPath $appProject) 'src\Airglow.App'
    foreach ($path in '\FanControl', '\LianLi\L-Connect 3') {
        $t = Split-TaskPath $path
        Report ([bool](Get-ScheduledTask -TaskPath $t.Folder -TaskName $t.Name -ErrorAction SilentlyContinue)) "scheduled task $path"
    }
    foreach ($name in 'LConnectService', 'LConnectServiceWatcher') {
        Report ([bool](Get-Service -Name $name -ErrorAction SilentlyContinue)) "service $name"
    }
    $openRgb = Get-ScheduledTask -TaskPath '\' -TaskName 'OpenRGB' -ErrorAction SilentlyContinue
    if (-not $openRgb) { Write-Host 'NOTE    task \OpenRGB not found' }
    elseif ($openRgb.State -eq 'Disabled') { Write-Host 'OK      task \OpenRGB is disabled' }
    else { Write-Host "WARN    task \OpenRGB is $($openRgb.State), not Disabled (left unchanged)" }
    $script:missing -eq 0
}

if (-not (Test-Preflight)) {
    Write-Host 'Preflight failed; nothing was changed.'
    exit 1
}

try {
    # Build first: nothing has been changed yet if this fails.
    Invoke-Step 'dotnet build -c Release src\Airglow.Service' {
        dotnet build $serviceProject -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
    }
    Invoke-Step 'dotnet build -c Release src\Airglow.App' {
        dotnet build $appProject -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
    }

    # Deploy airglow first; its engine pauses every fan while the old apps run.
    Invoke-Step 'run scripts\install-service.ps1 (stop, republish, start service Airglow)' {
        & (Join-Path $PSScriptRoot 'install-service.ps1')
    }
    Invoke-Step 'ensure service Airglow starts automatically (sc.exe config Airglow start= auto)' {
        sc.exe config Airglow start= auto
        if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed (exit $LASTEXITCODE)." }
    }
    Invoke-Step "stop any running Airglow.exe from $appDir" { Stop-AirglowApp }
    Invoke-Step "publish src\Airglow.App to $appDir" {
        dotnet publish $appProject -c Release -r win-x64 --self-contained false -o $appDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    }
    Invoke-Step "set HKCU Run value airglow = `"$appExe`" --tray" {
        New-ItemProperty -Path $runKey -Name 'airglow' -Value "`"$appExe`" --tray" -PropertyType String -Force | Out-Null
    }

    # Now retire the old controllers.
    Invoke-Step 'stop process FanControl (if running)' { Stop-ProcessByName 'FanControl' }
    Invoke-Step 'disable scheduled task \FanControl' { Disable-Task '\FanControl' }

    Invoke-Step 'stop process "L-Connect 3" (if running)' { Stop-ProcessByName 'L-Connect 3' }
    Invoke-Step 'disable scheduled task \LianLi\L-Connect 3' { Disable-Task '\LianLi\L-Connect 3' }
    Invoke-Step 'stop service LConnectServiceWatcher' { Stop-Service -Name LConnectServiceWatcher -Force }
    Invoke-Step 'stop service LConnectService' { Stop-Service -Name LConnectService -Force }
    Invoke-Step 'set LConnectServiceWatcher startup type Disabled' { Set-Service -Name LConnectServiceWatcher -StartupType Disabled }
    Invoke-Step 'set LConnectService startup type Disabled' { Set-Service -Name LConnectService -StartupType Disabled }
    Invoke-Step 'stop leftover L-Connect-Service-Watcher and L-Connect-Service processes (if running)' {
        Stop-ProcessByName 'L-Connect-Service-Watcher'
        Stop-ProcessByName 'L-Connect-Service'
    }
    Invoke-Step 'stop process "L-Connect 3" again (the watcher may have relaunched it)' { Stop-ProcessByName 'L-Connect 3' }

    if (-not $Go) {
        Write-Host 'WOULD poll GET /api/state over pipe airglow-v1 for up to 20 s and print each fan id and mode, plus faults'
        Write-Host 'Dry run only. Re-run with -Go to apply.'
        return
    }

    $script:CurrentStep = 'poll /api/state'
    Write-Host 'DOING poll GET /api/state for up to 20 s'
    $deadline = (Get-Date).AddSeconds(20)
    $state = $null
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $state = Get-AirglowApi -Path '/api/state'
            $lastError = $null
            $fans = @($state.fans)
            if ($fans.Count -gt 0 -and -not ($fans | Where-Object { $_.mode -notin 'curve', 'auto' })) { break }
        }
        catch { $lastError = $_.Exception.Message }
        Start-Sleep -Seconds 1
    }
    if (-not $state) { throw "could not read /api/state from the airglow pipe: $lastError" }

    $bad = 0
    foreach ($fan in @($state.fans)) {
        Write-Host ("  {0}: {1}" -f $fan.id, $fan.mode)
        if ($fan.mode -notin 'curve', 'auto') { $bad++; Write-Warning "fan $($fan.id) is in mode '$($fan.mode)': $($fan.note)" }
    }
    $faults = @($state.faults)
    if ($faults.Count -gt 0) { Write-Warning ('faults: ' + ($faults | ConvertTo-Json -Compress -Depth 5)) }
    else { Write-Host '  no faults' }
    if ($bad -gt 0) { Write-Warning "$bad fan(s) not in curve or auto mode." }

    Write-Host "Start airglow now from `"$appExe`" (it also starts at every sign-in)."
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    Write-Host "Cutover failed at: $script:CurrentStep. Run scripts\rollback.ps1 -Go to restore FanControl and L-Connect."
    exit 1
}
