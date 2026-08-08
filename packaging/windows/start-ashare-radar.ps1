param(
    [string]$InstallDir = "$PSScriptRoot",
    [int]$HealthTimeoutSeconds = 20
)

$ErrorActionPreference = "Stop"
$serviceExe = Join-Path $InstallDir "app\service\AShareRadar.ServiceHost.exe"
$desktopExe = Join-Path $InstallDir "app\desktop\AShareRadar.Desktop.exe"

if (-not (Test-Path $serviceExe)) { throw "Service executable not found: $serviceExe" }
if (-not (Test-Path $desktopExe)) { throw "Desktop executable not found: $desktopExe" }

Get-Process AShareRadar.ServiceHost -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process AShareRadar.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force

Start-Process -FilePath $serviceExe -WorkingDirectory (Split-Path -Parent $serviceExe) -WindowStyle Hidden

$deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
do {
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:18730/api/monitor/status" -TimeoutSec 2 -UseBasicParsing
        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) { break }
    }
    catch {
        Start-Sleep -Milliseconds 800
    }
} while ((Get-Date) -lt $deadline)

Start-Process -FilePath $desktopExe -WorkingDirectory (Split-Path -Parent $desktopExe)
Write-Host "AShareRadar started."
