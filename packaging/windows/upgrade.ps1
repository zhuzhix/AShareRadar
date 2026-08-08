param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AShareRadar"
)

$ErrorActionPreference = "Stop"
$PackageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not (Test-Path $InstallDir)) {
    throw "AShareRadar is not installed at $InstallDir. Run install.ps1 first."
}

Get-Process AShareRadar.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process AShareRadar.ServiceHost -ErrorAction SilentlyContinue | Stop-Process -Force

foreach ($name in @("app", "tools", "runtimes")) {
    $source = Join-Path $PackageRoot $name
    $target = Join-Path $InstallDir $name
    if (Test-Path $source) {
        if (Test-Path $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
    }
}

foreach ($name in @("install.ps1", "uninstall.ps1", "upgrade.ps1", "doctor.ps1", "start-ashare-radar.ps1", "stop-ashare-radar.ps1", "README.md", "package-manifest.json")) {
    $source = Join-Path $PackageRoot $name
    if (Test-Path $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $InstallDir $name) -Force
    }
}

& (Join-Path $InstallDir "install.ps1") -InstallDir $InstallDir -UpgradeOnly -ConfigureOnly
Write-Host "Upgrade complete. Data directory was preserved: $InstallDir\data"
