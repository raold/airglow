param(
    [Parameter(Mandatory)]
    [string]$Version
)
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3, got '$Version'." }

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$serviceDir = Join-Path $artifacts 'publish\service'
$appDir = Join-Path $artifacts 'publish\app'
$msiDir = Join-Path $artifacts 'msi'
$msi = Join-Path $artifacts "airglow-$Version-x64.msi"

# Start clean so files left over from an earlier publish never end up in the MSI.
foreach ($dir in $serviceDir, $appDir, $msiDir) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

dotnet publish (Join-Path $root 'src\Airglow.Service') -c Release -r win-x64 --self-contained true -o $serviceDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish of the service failed.' }

dotnet publish (Join-Path $root 'src\Airglow.App') -c Release -r win-x64 --self-contained true -o $appDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish of the app failed.' }

dotnet build (Join-Path $root 'installer\Airglow.Installer.wixproj') -c Release "-p:Version=$Version" "-p:ServiceDir=$serviceDir" "-p:AppDir=$appDir" -o $msiDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet build of the installer failed.' }

Copy-Item -LiteralPath (Join-Path $msiDir 'airglow.msi') -Destination $msi -Force
Write-Host "Built $msi ($((Get-Item -LiteralPath $msi).Length) bytes)"
