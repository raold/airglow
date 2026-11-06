#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:ProgramFiles 'Airglow\Service'
$exe = Join-Path $target 'Airglow.Service.exe'

function Wait-ServiceStopped {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $service = Get-Service -Name Airglow -ErrorAction SilentlyContinue
        if (-not $service -or $service.Status -eq 'Stopped') { return }
        Start-Sleep -Milliseconds 250
    }
    throw 'Airglow did not stop within 30 seconds.'
}

$existing = Get-Service -Name Airglow -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    sc.exe stop Airglow | Out-Null
    Wait-ServiceStopped
}

dotnet publish (Join-Path $root 'src\Airglow.Service') -c Release -r win-x64 --self-contained false -o $target
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if (-not $existing) {
    sc.exe create Airglow binPath= "`"$exe`"" start= auto DisplayName= "airglow fan and lighting engine"
    if ($LASTEXITCODE -ne 0) { throw 'sc.exe create failed.' }
}
sc.exe failure Airglow reset= 86400 actions= restart/5000/restart/5000/restart/5000
if ($LASTEXITCODE -ne 0) { throw 'sc.exe failure failed.' }

sc.exe start Airglow
if ($LASTEXITCODE -ne 0) { throw 'sc.exe start failed.' }
