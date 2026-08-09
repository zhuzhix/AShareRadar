param(
    [ValidateSet("Full", "Upgrade")]
    [string]$PackageKind = "Full",
    [string]$Version = "0.1.0",
    [string]$OutputRoot = "",
    [string]$BuildRoot = "",
    [string]$HistoricalDuckDbPath = "",
    [string]$RuntimeDataRoot = "",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
$WorkspaceRoot = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "AShareRadar"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $WorkspaceRoot "packages"
}
if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    $BuildRoot = Join-Path $WorkspaceRoot "build"
}

$BuildDate = Get-Date -Format "yyyyMMdd"
$PackageName = "AShareRadar-$PackageKind-$BuildDate"
$PackageRoot = Join-Path $OutputRoot "AShareRadar-Setup"
$ZipPath = Join-Path $OutputRoot "$PackageName.zip"

function Reset-Directory([string]$Path, [string]$AllowedRoot) {
    if (Test-Path $Path) {
        $resolved = (Resolve-Path $Path).Path
        $allowed = (Resolve-Path $AllowedRoot).Path
        if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refuse to clean path outside allowed root: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Copy-DirectoryFiltered([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) { return }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $sourceRoot = (Resolve-Path $Source).Path
    $skipDirs = @(".python_packages", "__pycache__", ".pytest_cache", ".mypy_cache", ".git", "Logs", "mlruns", "artifacts")
    Get-ChildItem -Path $sourceRoot -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart("\")
        if ([string]::IsNullOrWhiteSpace($relative)) { return }
        $parts = $relative -split "\\"
        if ($parts | Where-Object { $skipDirs -contains $_ }) { return }
        if (-not $_.PSIsContainer -and ($_.Name -like "*.pyc" -or $_.Name -like "*.log" -or $_.Name -like "*.tmp" -or $_.Name -like "*.bak" -or $_.Name -like "*.wal" -or $_.Name -like "*.shm" -or $_.Name -like "verify_*_result*.json")) { return }

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

function Copy-RequiredFile([string]$Source, [string]$Destination, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required package file is missing: $Name ($Source)"
    }
    if ((Get-Item -LiteralPath $Source).Length -le 0) {
        throw "Required package file is empty: $Name ($Source)"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Assert-RadarStopped() {
    $running = Get-Process -Name "AShareRadar.ServiceHost", "AShareRadar.Desktop" -ErrorAction SilentlyContinue
    if ($null -ne $running) {
        $names = ($running | Select-Object -ExpandProperty ProcessName -Unique) -join ", "
        throw "AShareRadar is running ($names). Stop the application before packaging to keep SQLite/DuckDB data consistent."
    }
}

function Remove-GeneratedRuntimeFiles([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    foreach ($directoryName in @("Logs", "mlruns", "artifacts")) {
        Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $directoryName } |
            Sort-Object FullName -Descending |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '\.(log|tmp|bak|wal|shm)$' } |
        Remove-Item -Force -ErrorAction SilentlyContinue
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

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
Assert-RadarStopped
$PublishRoot = Join-Path $BuildRoot "publish"
Reset-Directory $PackageRoot $OutputRoot
Reset-Directory $PublishRoot $BuildRoot
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "app\service") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "app\desktop") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "data\runtime") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "tools\python") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PackageRoot "runtimes\installers") -Force | Out-Null

Invoke-Checked "dotnet" @(
    "publish",
    (Join-Path $RepoRoot "src\AShareRadar.ServiceHost\AShareRadar.ServiceHost.csproj"),
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-o", (Join-Path $PublishRoot "service")
)
Invoke-Checked "dotnet" @(
    "publish",
    (Join-Path $RepoRoot "src\AShareRadar.Desktop\AShareRadar.Desktop.csproj"),
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-o", (Join-Path $PublishRoot "desktop")
)

Copy-DirectoryFiltered (Join-Path $PublishRoot "service") (Join-Path $PackageRoot "app\service")
Copy-DirectoryFiltered (Join-Path $PublishRoot "desktop") (Join-Path $PackageRoot "app\desktop")
Remove-GeneratedRuntimeFiles (Join-Path $PackageRoot "app")

if (-not (Test-Path (Join-Path $PackageRoot "app\service\AShareRadar.ServiceHost.exe"))) {
    throw "Service executable was not produced: $PackageRoot\app\service\AShareRadar.ServiceHost.exe"
}
if (-not (Test-Path (Join-Path $PackageRoot "app\desktop\AShareRadar.Desktop.exe"))) {
    throw "Desktop executable was not produced: $PackageRoot\app\desktop\AShareRadar.Desktop.exe"
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

$sourceData = Join-Path $RepoRoot "src\AShareRadar.ServiceHost\data"
if (-not (Test-Path $sourceData)) {
    throw "Source data directory is missing: $sourceData"
}

foreach ($name in @("sector-mapping.csv", "concept-mapping.csv", "market-sentiment-external.csv", "trading-calendar-cn.json")) {
    $source = Join-Path $sourceData $name
    if ($PackageKind -eq "Full" -and $name -in @("sector-mapping.csv", "concept-mapping.csv", "trading-calendar-cn.json")) {
        Copy-RequiredFile $source (Join-Path $PackageRoot "data\$name") $name
    }
    else {
        Copy-IfExists $source (Join-Path $PackageRoot "data\$name")
    }
}

if ($PackageKind -eq "Full") {
    $selectedDuckDbSource = $null
    if (-not [string]::IsNullOrWhiteSpace($HistoricalDuckDbPath)) {
        $selectedDuckDbSource = $HistoricalDuckDbPath
    }
    elseif (-not [string]::IsNullOrWhiteSpace($RuntimeDataRoot) -and (Test-Path (Join-Path $RuntimeDataRoot "ashare.duckdb") -PathType Leaf)) {
        $selectedDuckDbSource = Join-Path $RuntimeDataRoot "ashare.duckdb"
    }
    elseif (Test-Path (Join-Path $sourceData "ashare.duckdb")) {
        $selectedDuckDbSource = Join-Path $sourceData "ashare.duckdb"
    }
    elseif (Test-Path (Join-Path $sourceData "runtime\ashare-radar.duckdb") -PathType Leaf) {
        $selectedDuckDbSource = Join-Path $sourceData "runtime\ashare-radar.duckdb"
    }
    if ($null -eq $selectedDuckDbSource) {
        throw "Full package requires a historical DuckDB file. Pass -HistoricalDuckDbPath or provide one under the source data directory."
    }
    Copy-RequiredFile $selectedDuckDbSource (Join-Path $PackageRoot "data\ashare.duckdb") "ashare.duckdb"

    Copy-IfExists (Join-Path $sourceData "runtime\ashare-radar.duckdb") (Join-Path $PackageRoot "data\runtime\ashare-radar.duckdb")
    Copy-IfExists (Join-Path $sourceData "runtime\ashare-radar.sqlite") (Join-Path $PackageRoot "data\runtime\ashare-radar.sqlite")
    Copy-DirectoryFiltered (Join-Path $sourceData "qlib") (Join-Path $PackageRoot "data\qlib")
    Copy-DirectoryFiltered (Join-Path $sourceData "next-day-prediction") (Join-Path $PackageRoot "data\next-day-prediction")

    if (-not [string]::IsNullOrWhiteSpace($RuntimeDataRoot)) {
        if (-not (Test-Path $RuntimeDataRoot)) { throw "RuntimeDataRoot not found: $RuntimeDataRoot" }
        # A complete runtime package includes every data file under the explicitly
        # supplied data root (parquet, SQLite state, qlib, shared_data, etc.).
        # Generated logs, caches, and SQLite lock files are still filtered by
        # Copy-DirectoryFiltered. The selected DuckDB is copied again below so
        # HistoricalDuckDbPath remains authoritative.
        Copy-DirectoryFiltered $RuntimeDataRoot (Join-Path $PackageRoot "data")
    }

    # The selected database remains authoritative if RuntimeDataRoot was also supplied.
    Copy-RequiredFile $selectedDuckDbSource (Join-Path $PackageRoot "data\ashare.duckdb") "ashare.duckdb"
}

Copy-DirectoryFiltered (Join-Path $RepoRoot "tools\eastmoney_quant") (Join-Path $PackageRoot "tools\eastmoney_quant")
Copy-DirectoryFiltered (Join-Path $RepoRoot "tools\qlib_next_day") (Join-Path $PackageRoot "tools\qlib_next_day")

New-Item -ItemType Directory -Path (Join-Path $PackageRoot "tools\python") -Force | Out-Null
@"
duckdb
pandas
numpy
akshare
baostock
requests
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
    dataPolicy = "preserve-on-upgrade"
    sourceCommit = "unknown"
}
try {
    $commit = (& git -C $RepoRoot rev-parse HEAD 2>$null | Select-Object -First 1)
    if (-not [string]::IsNullOrWhiteSpace($commit)) {
        $manifest.sourceCommit = $commit.Trim()
    }
}
catch {
    # Git metadata is useful but not required for a source archive build.
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $PackageRoot "package-manifest.json") -Encoding UTF8

if (-not $NoZip) {
    if (Test-Path $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Compress-Archive -Path (Join-Path $PackageRoot "*") -DestinationPath $ZipPath -Force
Write-Host "Package zip: $ZipPath"
}

Write-Host "Build directory: $PublishRoot"
Write-Host "Package directory: $PackageRoot"
