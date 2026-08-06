param(
    [string]$InstallDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
$serviceExe = Join-Path $InstallDir "app\service\AShareRadar.ServiceHost.exe"
$desktopExe = Join-Path $InstallDir "app\desktop\AShareRadar.Desktop.exe"

if (-not (Test-Path $serviceExe)) { throw "Service executable not found: $serviceExe" }
if (-not (Test-Path $desktopExe)) { throw "Desktop executable not found: $desktopExe" }

Get-Process AShareRadar.ServiceHost -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process AShareRadar.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force

Start-Process -FilePath $serviceExe -WorkingDirectory (Split-Path -Parent $serviceExe) -WindowStyle Hidden
Start-Sleep -Seconds 2
Start-Process -FilePath $desktopExe -WorkingDirectory (Split-Path -Parent $desktopExe)

Write-Host "AShareRadar started."
