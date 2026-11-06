#Requires -RunAsAdministrator
param([switch]$Go)
$ErrorActionPreference = 'Stop'

$appDir = Join-Path $env:ProgramFiles 'Airglow\App'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:failed = 0

# Best effort: every step runs even if an earlier one failed.
function Invoke-Step {
    param([string]$Description, [scriptblock]$Action)
    if (-not $Go) { Write-Host "WOULD $Description"; return }
    Write-Host "DOING $Description"
    try { & $Action }
    catch { $script:failed++; Write-Host "WARN: $Description - $($_.Exception.Message)" }
}

function Enable-Task {
    param([string]$Folder, [string]$Name)
    if (-not (Get-ScheduledTask -TaskPath $Folder -TaskName $Name -ErrorAction SilentlyContinue)) {
        Write-Host "  skipped: task $Folder$Name not found"
        return
    }
    Enable-ScheduledTask -TaskPath $Folder -TaskName $Name | Out-Null
    Start-ScheduledTask -TaskPath $Folder -TaskName $Name
}

function Restore-Service {
    param([string]$Name)
    if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
        Write-Host "  skipped: service $Name not found"
        return
    }
    Set-Service -Name $Name -StartupType Automatic
    Start-Service -Name $Name
}

Invoke-Step 'remove HKCU Run value airglow' {
    if (Get-ItemProperty -Path $runKey -Name 'airglow' -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name 'airglow'
    }
    else { Write-Host '  skipped: Run value airglow not present' }
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
    if (-not $service) { Write-Host '  skipped: service Airglow not found'; return }
    if ($service.Status -ne 'Stopped') { Stop-Service -Name Airglow -Force }
}
Invoke-Step 'set service Airglow to manual start (sc.exe config Airglow start= demand) so a reboot keeps it stopped' {
    if (-not (Get-Service -Name Airglow -ErrorAction SilentlyContinue)) { Write-Host '  skipped: service Airglow not found'; return }
    sc.exe config Airglow start= demand
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed (exit $LASTEXITCODE)." }
}
Invoke-Step 'set LConnectService to Automatic and start it' { Restore-Service 'LConnectService' }
Invoke-Step 'set LConnectServiceWatcher to Automatic and start it' { Restore-Service 'LConnectServiceWatcher' }
Invoke-Step 'enable and start scheduled task \LianLi\L-Connect 3' { Enable-Task '\LianLi\' 'L-Connect 3' }
Invoke-Step 'enable and start scheduled task \FanControl' { Enable-Task '\' 'FanControl' }

if (-not $Go) { Write-Host 'Dry run only. Re-run with -Go to apply.'; return }
if ($script:failed -gt 0) { Write-Host "Rollback finished with $script:failed failed step(s); see WARN lines."; exit 1 }
Write-Host 'Rollback complete.'
