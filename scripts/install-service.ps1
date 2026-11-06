#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:ProgramFiles 'Airglow\Service'
$staging = "$target.new"
$previous = "$target.old"
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

# Publish to a staging folder first: a failed build leaves the installed service running untouched.
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
dotnet publish (Join-Path $root 'src\Airglow.Service') -c Release -r win-x64 --self-contained false -o $staging
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed; the installed service was left alone.' }

$existing = Get-Service -Name Airglow -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    $servicePid = (Get-CimInstance Win32_Service -Filter "Name='Airglow'").ProcessId
    sc.exe stop Airglow | Out-Null
    Wait-ServiceStopped
    if ($servicePid) { Wait-Process -Id $servicePid -Timeout 30 -ErrorAction SilentlyContinue }
}

# Swap folders: Service -> Service.old, Service.new -> Service.
if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
if (Test-Path -LiteralPath $target) { Rename-Item -LiteralPath $target -NewName (Split-Path $previous -Leaf) }
try {
    Rename-Item -LiteralPath $staging -NewName (Split-Path $target -Leaf)
}
catch {
    if (Test-Path -LiteralPath $previous) { Rename-Item -LiteralPath $previous -NewName (Split-Path $target -Leaf) }
    throw
}
if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }

if (-not $existing) {
    sc.exe create Airglow binPath= "`"$exe`"" start= auto DisplayName= "airglow fan and lighting engine"
    if ($LASTEXITCODE -ne 0) { throw 'sc.exe create failed.' }
}
sc.exe failure Airglow reset= 86400 actions= restart/5000/restart/5000/restart/5000
if ($LASTEXITCODE -ne 0) { throw 'sc.exe failure failed.' }
sc.exe failureflag Airglow 1
if ($LASTEXITCODE -ne 0) { throw 'sc.exe failureflag failed.' }

sc.exe start Airglow
if ($LASTEXITCODE -ne 0) { throw 'sc.exe start failed.' }
