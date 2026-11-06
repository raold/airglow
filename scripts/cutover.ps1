#Requires -RunAsAdministrator
param([switch]$Go)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'AirglowPipe.psm1') -Force

$appDir = Join-Path $env:ProgramFiles 'Airglow\App'
$appExe = Join-Path $appDir 'Airglow.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Invoke-Step {
    param([string]$Description, [scriptblock]$Action)
    if (-not $Go) { Write-Host "WOULD $Description"; return }
    Write-Host "DOING $Description"
    try { & $Action }
    catch { throw "FAILED: $Description - $($_.Exception.Message)" }
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

function Disable-Task {
    param([string]$Path)
    $folder = Split-Path $Path -Parent
    if (-not $folder.EndsWith('\')) { $folder += '\' }
    Disable-ScheduledTask -TaskPath $folder -TaskName (Split-Path $Path -Leaf) | Out-Null
}

# FanControl
Invoke-Step 'stop process FanControl (if running)' { Stop-ProcessByName 'FanControl' }
Invoke-Step 'disable scheduled task \FanControl' { Disable-Task '\FanControl' }

# L-Connect 3
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

# OpenRGB
$openRgb = Get-ScheduledTask -TaskPath '\' -TaskName 'OpenRGB' -ErrorAction SilentlyContinue
if (-not $openRgb) { Write-Host 'NOTE task \OpenRGB not found; nothing to check' }
elseif ($openRgb.State -eq 'Disabled') { Write-Host 'OK task \OpenRGB is already disabled' }
else { Invoke-Step 'disable scheduled task \OpenRGB' { Disable-Task '\OpenRGB' } }

# airglow service
Invoke-Step 'run scripts\install-service.ps1 (stop, republish, start service Airglow)' {
    & (Join-Path $PSScriptRoot 'install-service.ps1')
}
Invoke-Step 'ensure service Airglow starts automatically (sc.exe config Airglow start= auto)' {
    sc.exe config Airglow start= auto
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed (exit $LASTEXITCODE)." }
}

# airglow app
Invoke-Step "stop any running Airglow.exe from $appDir" { Stop-AirglowApp }
Invoke-Step "publish src\Airglow.App to $appDir" {
    dotnet publish (Join-Path $root 'src\Airglow.App') -c Release -r win-x64 --self-contained false -o $appDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
}
Invoke-Step "set HKCU Run value airglow = `"$appExe`" --tray" {
    New-ItemProperty -Path $runKey -Name 'airglow' -Value "`"$appExe`" --tray" -PropertyType String -Force | Out-Null
}
Invoke-Step 'start Airglow.exe --tray' { Start-Process -FilePath $appExe -ArgumentList '--tray' }

# Verify
if (-not $Go) {
    Write-Host 'WOULD poll GET /api/state over pipe airglow-v1 for up to 20 s and print each fan id and mode, plus faults'
    Write-Host 'Dry run only. Re-run with -Go to apply.'
    return
}

Write-Host 'DOING poll GET /api/state for up to 20 s'
$deadline = (Get-Date).AddSeconds(20)
$state = $null
$lastError = $null
while ((Get-Date) -lt $deadline) {
    try {
        $state = Get-AirglowApi -Path '/api/state'
        $lastError = $null
        $fans = @($state.fans)
        if ($fans.Count -gt 0 -and -not ($fans | Where-Object { $_.mode -eq 'paused' })) { break }
    }
    catch { $lastError = $_.Exception.Message }
    Start-Sleep -Seconds 1
}
if (-not $state) { throw "FAILED: could not read /api/state from the airglow pipe: $lastError" }

$paused = 0
foreach ($fan in @($state.fans)) {
    Write-Host ("  {0}: {1}" -f $fan.id, $fan.mode)
    if ($fan.mode -eq 'paused') { $paused++; Write-Warning "fan $($fan.id) is still paused: $($fan.note)" }
}
$faults = @($state.faults)
if ($faults.Count -gt 0) { Write-Warning ('faults: ' + ($faults | ConvertTo-Json -Compress -Depth 5)) }
else { Write-Host '  no faults' }
if ($paused -gt 0) { Write-Warning "$paused fan(s) still paused." }
