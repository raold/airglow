#Requires -RunAsAdministrator
param([switch]$Go)
$ErrorActionPreference = 'Stop'

$appDir = Join-Path $env:ProgramFiles 'Airglow\App'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Invoke-Step {
    param([string]$Description, [scriptblock]$Action)
    if (-not $Go) { Write-Host "WOULD $Description"; return }
    Write-Host "DOING $Description"
    try { & $Action }
    catch { throw "FAILED: $Description - $($_.Exception.Message)" }
}

function Enable-Task {
    param([string]$Path)
    $folder = Split-Path $Path -Parent
    if (-not $folder.EndsWith('\')) { $folder += '\' }
    Enable-ScheduledTask -TaskPath $folder -TaskName (Split-Path $Path -Leaf) | Out-Null
}

Invoke-Step 'remove HKCU Run value airglow' {
    Remove-ItemProperty -Path $runKey -Name 'airglow' -ErrorAction SilentlyContinue
}
Invoke-Step "stop any running Airglow.exe from $appDir" {
    $procs = @(Get-Process -Name Airglow -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -like "$appDir\*" })
    foreach ($p in $procs) {
        Stop-Process -Id $p.Id -Force
        $p.WaitForExit(15000) | Out-Null
        if (-not $p.HasExited) { throw "Airglow.exe (pid $($p.Id)) did not exit." }
    }
}
Invoke-Step 'stop service Airglow (stays installed; it pauses itself while the old apps run)' {
    $service = Get-Service -Name Airglow -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') { Stop-Service -Name Airglow -Force }
}
Invoke-Step 'set LConnectService startup type Automatic' { Set-Service -Name LConnectService -StartupType Automatic }
Invoke-Step 'set LConnectServiceWatcher startup type Automatic' { Set-Service -Name LConnectServiceWatcher -StartupType Automatic }
Invoke-Step 'start service LConnectService' { Start-Service -Name LConnectService }
Invoke-Step 'start service LConnectServiceWatcher' { Start-Service -Name LConnectServiceWatcher }
Invoke-Step 'enable and start scheduled task \LianLi\L-Connect 3' {
    Enable-Task '\LianLi\L-Connect 3'
    Start-ScheduledTask -TaskPath '\LianLi\' -TaskName 'L-Connect 3'
}
Invoke-Step 'enable and start scheduled task \FanControl' {
    Enable-Task '\FanControl'
    Start-ScheduledTask -TaskPath '\' -TaskName 'FanControl'
}

if (-not $Go) { Write-Host 'Dry run only. Re-run with -Go to apply.' }
