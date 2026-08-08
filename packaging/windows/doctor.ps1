param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AShareRadar"
)

$ErrorActionPreference = "Continue"

function Test-Command($Name) {
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Write-Check([string]$Name, [bool]$Ok, [string]$Detail = "") {
    $status = if ($Ok) { "OK" } else { "FAIL" }
    Write-Host ("[{0}] {1} {2}" -f $status, $Name, $Detail)
}

$serviceExe = Join-Path $InstallDir "app\service\AShareRadar.ServiceHost.exe"
$desktopExe = Join-Path $InstallDir "app\desktop\AShareRadar.Desktop.exe"
$settingsPath = Join-Path $InstallDir "app\service\appsettings.json"
$sqlitePath = Join-Path $InstallDir "data\runtime\ashare-radar.sqlite"
$duckdbPath = Join-Path $InstallDir "data\ashare.duckdb"

Write-Host "AShareRadar doctor: $InstallDir"
Write-Check "Install directory" (Test-Path $InstallDir) $InstallDir
Write-Check "Service executable" (Test-Path $serviceExe) $serviceExe
Write-Check "Desktop executable" (Test-Path $desktopExe) $desktopExe
Write-Check "appsettings.json" (Test-Path $settingsPath) $settingsPath
Write-Check "SQLite runtime data" (Test-Path $sqlitePath) $sqlitePath
Write-Check "DuckDB market data" (Test-Path $duckdbPath) $duckdbPath
Write-Check "Python command" (Test-Command "python")
Write-Check "PowerShell command" (Test-Command "powershell")
Write-Check "EASTMONEY_QUANT_TOKEN" (-not [string]::IsNullOrWhiteSpace($env:EASTMONEY_QUANT_TOKEN))

foreach ($path in @(
    "tools\eastmoney_quant\update_history_data_gm.py",
    "tools\eastmoney_quant\realtime_snapshot_gm.py",
    "tools\eastmoney_quant\kline_gm.py",
    "tools\qlib_next_day\run_next_day_direction_experiment.ps1"
)) {
    $fullPath = Join-Path $InstallDir $path
    Write-Check $path (Test-Path $fullPath) $fullPath
}

try {
    $response = Invoke-WebRequest -Uri "http://127.0.0.1:18730/api/monitor/status" -TimeoutSec 5 -UseBasicParsing
    Write-Check "Service health" ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) "HTTP $($response.StatusCode)"
}
catch {
    Write-Check "Service health" $false $_.Exception.Message
}
