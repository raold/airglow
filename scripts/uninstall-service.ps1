#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:ProgramFiles 'Airglow\Service'

$existing = Get-Service -Name Airglow -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        sc.exe stop Airglow | Out-Null
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Service -Name Airglow).Status -ne 'Stopped') {
            if ((Get-Date) -gt $deadline) { throw 'Airglow did not stop within 30 seconds.' }
            Start-Sleep -Milliseconds 250
        }
    }
    sc.exe delete Airglow
    if ($LASTEXITCODE -ne 0) { throw 'sc.exe delete failed.' }
}

if (Test-Path $target) { Remove-Item -Recurse -Force $target }
