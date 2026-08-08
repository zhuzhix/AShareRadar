param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AShareRadar",
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"

Get-Process AShareRadar.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process AShareRadar.ServiceHost -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not (Test-Path $InstallDir)) {
    Write-Host "AShareRadar is not installed at $InstallDir"
    return
}

if ($KeepData) {
    foreach ($name in @("app", "tools", "runtimes", "start-ashare-radar.ps1", "stop-ashare-radar.ps1", "doctor.ps1", "upgrade.ps1", "README.md", "package-manifest.json")) {
        $target = Join-Path $InstallDir $name
        if (Test-Path $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
    Write-Host "AShareRadar application files removed. Data kept at $InstallDir\data"
}
else {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "AShareRadar removed from $InstallDir"
}
