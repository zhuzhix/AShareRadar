param(
    [ValidateSet("Full", "Upgrade")]
    [string]$PackageKind = "Full",
    [string]$Version = "0.1.0",
    [string]$OutputRoot = "",
    [string]$HistoricalDuckDbPath = "",
    [string]$RuntimeDataRoot = "",
    [switch]$SkipPublish,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot "artifacts\packages"
}

$BuildDate = Get-Date -Format "yyyyMMdd"
$PackageName = "AShareRadar-$PackageKind-$BuildDate"
$PackageRoot = Join-Path $OutputRoot "AShareRadar-Setup"
$ZipPath = Join-Path $OutputRoot "$PackageName.zip"

function Reset-Directory([string]$Path) {
    if (Test-Path $Path) {
        $resolved = (Resolve-Path $Path).Path
        $allowed = (Resolve-Path $OutputRoot).Path
        if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refuse to clean path outside output root: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Copy-DirectoryFiltered([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) { return }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $sourceRoot = (Resolve-Path $Source).Path
    $skipDirs = @(".python_packages", "__pycache__", ".pytest_cache", ".mypy_cache")
    Get-ChildItem -Path $sourceRoot -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart("\")
        if ([string]::IsNullOrWhiteSpace($relative)) { return }
        $parts = $relative -split "\\"
        if ($parts | Where-Object { $skipDirs -contains $_ }) { return }
        if (-not $_.PSIsContainer -and ($_.Name -like "*.pyc" -or $_.Name -like "verify_*_result*.json")) { return }

        $target = Join-Path $Destination $relative
        if ($_.PSIsContainer) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
        }
        else {
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
    }
}

function Copy-IfExists([string]$Source, [string]$Destination) {
    if (Test-Path $Source) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    }
}

function Remove-PackageSecrets([string]$SettingsPath) {
    if (-not (Test-Path $SettingsPath)) { return }
    $settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
    foreach ($sectionName in @("EastMoneyQuant", "EastMoneyQuantDotNet")) {
        $section = $settings.PSObject.Properties[$sectionName].Value
        if ($null -ne $section -and $section.PSObject.Properties.Name -contains "Token") {
            $section.Token = ""
        }
    }
    if ($settings.ExternalSentimentSdkUpdate -and $settings.ExternalSentimentSdkUpdate.PSObject.Properties.Name -contains "Token") {
        $settings.ExternalSentimentSdkUpdate.Token = ""
    }
    $settings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $SettingsPath -Encoding UTF8
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Reset-Directory $PackageRoot
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "app\service") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "app\desktop") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "data\runtime") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "tools\python") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "runtimes\installers") -Force | Out-Null

if (-not $SkipPublish) {
    dotnet publish (Join-Path $RepoRoot "src\AShareRadar.ServiceHost\AShareRadar.ServiceHost.csproj") -c Release -r win-x64 --self-contained true -o (Join-Path $PackageRoot "app\service")
    dotnet publish (Join-Path $RepoRoot "src\AShareRadar.Desktop\AShareRadar.Desktop.csproj") -c Release -r win-x64 --self-contained true -o (Join-Path $PackageRoot "app\desktop")
}
else {
    $servicePublish = Join-Path $RepoRoot "artifacts\verify-servicehost"
    $desktopPublish = Join-Path $RepoRoot "artifacts\verify-desktop"
    if (-not (Test-Path $servicePublish)) { throw "Missing service publish directory: $servicePublish" }
    if (-not (Test-Path $desktopPublish)) { throw "Missing desktop publish directory: $desktopPublish" }
    Copy-Item -Path (Join-Path $servicePublish "*") -Destination (Join-Path $PackageRoot "app\service") -Recurse -Force
    Copy-Item -Path (Join-Path $desktopPublish "*") -Destination (Join-Path $PackageRoot "app\desktop") -Recurse -Force
}

# Never ship a developer's EastMoney token inside a distributable package.
Remove-PackageSecrets (Join-Path $PackageRoot "app\service\appsettings.json")

Copy-Item -LiteralPath (Join-Path $ScriptDir "install.ps1") -Destination (Join-Path $PackageRoot "install.ps1") -Force
Copy-Item -LiteralPath (Join-Path $ScriptDir "uninstall.ps1") -Destination (Join-Path $PackageRoot "uninstall.ps1") -Force
Copy-Item -LiteralPath (Join-Path $ScriptDir "upgrade.ps1") -Destination (Join-Path $PackageRoot "upgrade.ps1") -Force
Copy-Item -LiteralPath (Join-Path $ScriptDir "doctor.ps1") -Destination (Join-Path $PackageRoot "doctor.ps1") -Force
Copy-Item -LiteralPath (Join-Path $ScriptDir "start-ashare-radar.ps1") -Destination (Join-Path $PackageRoot "start-ashare-radar.ps1") -Force
Copy-Item -LiteralPath (Join-Path $ScriptDir "stop-ashare-radar.ps1") -Destination (Join-Path $PackageRoot "stop-ashare-radar.ps1") -Force
Copy-Item -LiteralPath (Join-Path $ScriptDir "README.md") -Destination (Join-Path $PackageRoot "README.md") -Force

$sourceData = Join-Path $RepoRoot "artifacts\verify-servicehost\data"
if (-not (Test-Path $sourceData)) {
    $sourceData = Join-Path $RepoRoot "src\AShareRadar.ServiceHost\data"
}

foreach ($name in @("sector-mapping.csv", "concept-mapping.csv", "market-sentiment-external.csv", "trading-calendar-cn.json")) {
    Copy-IfExists (Join-Path $sourceData $name) (Join-Path $PackageRoot "data\$name")
}

if ($PackageKind -eq "Full") {
    if (-not [string]::IsNullOrWhiteSpace($HistoricalDuckDbPath)) {
        Copy-IfExists $HistoricalDuckDbPath (Join-Path $PackageRoot "data\ashare.duckdb")
    }
    elseif (Test-Path (Join-Path $sourceData "ashare.duckdb")) {
        Copy-IfExists (Join-Path $sourceData "ashare.duckdb") (Join-Path $PackageRoot "data\ashare.duckdb")
    }
    else {
        Copy-IfExists (Join-Path $sourceData "runtime\ashare-radar.duckdb") (Join-Path $PackageRoot "data\ashare.duckdb")
    }

    Copy-IfExists (Join-Path $sourceData "runtime\ashare-radar.duckdb") (Join-Path $PackageRoot "data\runtime\ashare-radar.duckdb")
    Copy-IfExists (Join-Path $sourceData "runtime\ashare-radar.sqlite") (Join-Path $PackageRoot "data\runtime\ashare-radar.sqlite")
    Copy-IfExists (Join-Path $sourceData "runtime\ashare-radar.sqlite-wal") (Join-Path $PackageRoot "data\runtime\ashare-radar.sqlite-wal")
    Copy-IfExists (Join-Path $sourceData "runtime\ashare-radar.sqlite-shm") (Join-Path $PackageRoot "data\runtime\ashare-radar.sqlite-shm")
    Copy-DirectoryFiltered (Join-Path $sourceData "qlib") (Join-Path $PackageRoot "data\qlib")
    Copy-DirectoryFiltered (Join-Path $sourceData "next-day-prediction") (Join-Path $PackageRoot "data\next-day-prediction")

    if (-not [string]::IsNullOrWhiteSpace($RuntimeDataRoot)) {
        if (-not (Test-Path $RuntimeDataRoot)) { throw "RuntimeDataRoot not found: $RuntimeDataRoot" }
        Copy-IfExists (Join-Path $RuntimeDataRoot "ashare.duckdb") (Join-Path $PackageRoot "data\ashare.duckdb")
        Copy-DirectoryFiltered (Join-Path $RuntimeDataRoot "runtime") (Join-Path $PackageRoot "data\runtime")
        Copy-DirectoryFiltered (Join-Path $RuntimeDataRoot "shared_data") (Join-Path $PackageRoot "data\shared_data")
        Copy-DirectoryFiltered (Join-Path $RuntimeDataRoot "qlib") (Join-Path $PackageRoot "data\qlib")
        Copy-DirectoryFiltered (Join-Path $RuntimeDataRoot "next-day-prediction") (Join-Path $PackageRoot "data\next-day-prediction")
    }
}

Copy-DirectoryFiltered (Join-Path $RepoRoot "tools\eastmoney_quant") (Join-Path $PackageRoot "tools\eastmoney_quant")
Copy-DirectoryFiltered (Join-Path $RepoRoot "tools\qlib_next_day") (Join-Path $PackageRoot "tools\qlib_next_day")

@"
duckdb
pandas
numpy
akshare
pyarrow
"@ | Set-Content -Path (Join-Path $PackageRoot "tools\python\requirements.txt") -Encoding UTF8

$manifest = [ordered]@{
    appVersion = $Version
    packageKind = $PackageKind
    buildTime = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    servicePort = 18730
    dataVersion = $BuildDate
    duckdbPath = "data/ashare.duckdb"
    sqlitePath = "data/runtime/ashare-radar.sqlite"
    requiresToken = $true
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $PackageRoot "package-manifest.json") -Encoding UTF8

if (-not $NoZip) {
    if (Test-Path $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Compress-Archive -Path (Join-Path $PackageRoot "*") -DestinationPath $ZipPath -Force
    Write-Host "Package zip: $ZipPath"
}

Write-Host "Package directory: $PackageRoot"
