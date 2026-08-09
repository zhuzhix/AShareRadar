param(
    [string]$Version = "0.1.0",
    [string]$HistoricalDuckDbPath = "",
    [string]$PythonRuntimePath = "",
    [string]$PythonPackagesPath = "",
    [string]$PythonVersion = "3.12.10",
    [string]$OutputRoot = "",
    [switch]$SkipCompile
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..\..")
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot "artifacts\releases"
}

$StageRoot = Join-Path $RepoRoot "artifacts\packages\AShareRadar-Setup"
$CacheRoot = Join-Path $RepoRoot "artifacts\packaging-cache"
$RuntimeRoot = Join-Path $StageRoot "runtime\python"
$PackageScript = Join-Path $ScriptDir "build-package.ps1"
$IssScript = Join-Path $ScriptDir "AShareRadar.iss"
$InstallerPath = Join-Path $OutputRoot "AShareRadar-Setup-$Version.exe"
$PythonCacheRoot = Join-Path $CacheRoot "python"

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Copy-TreeFiltered([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) { throw "Missing source directory: $Source" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring((Resolve-Path $Source).Path.Length).TrimStart("\")
        if ([string]::IsNullOrWhiteSpace($relative)) { return }
        if ($relative -match '(^|\\)(__pycache__|\.pytest_cache|\.mypy_cache)(\\|$)') { return }
        if (-not $_.PSIsContainer -and $_.Name -like '*.pyc') { return }
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

function Set-RelativeRuntimeSettings([string]$SettingsPath) {
    $settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
    $python = "..\..\runtime\python\python.exe"
    $tools = "..\..\tools\eastmoney_quant"
    $data = "..\..\data"

    foreach ($section in @($settings.EastMoneyQuant, $settings.HistoricalDataUpdate, $settings.ExternalSentimentSdkUpdate)) {
        if ($null -ne $section -and $section.PSObject.Properties.Name -contains "PythonPath") {
            $section.PythonPath = $python
        }
    }
    if ($null -ne $settings.EastMoneyQuant) {
        $settings.EastMoneyQuant.RealtimeScriptPath = "$tools\realtime_snapshot_gm.py"
        $settings.EastMoneyQuant.KLineScriptPath = "$tools\kline_gm.py"
        $settings.EastMoneyQuant.DuckDbPath = "$data\ashare.duckdb"
    }
    if ($null -ne $settings.HistoricalDataUpdate) {
        $settings.HistoricalDataUpdate.ScriptPath = "$tools\update_history_data_gm.py"
        $settings.HistoricalDataUpdate.DataDir = $data
    }
    if ($null -ne $settings.ExternalSentimentSdkUpdate) {
        $settings.ExternalSentimentSdkUpdate.ScriptPath = "$tools\update_external_sentiment_gm.py"
        $settings.ExternalSentimentSdkUpdate.OutputPath = "$data\market-sentiment-external.csv"
    }
    if ($null -ne $settings.Database) {
        $settings.Database.SqlitePath = "$data\runtime\ashare-radar.sqlite"
        $settings.Database.DuckDbPath = "$data\ashare.duckdb"
    }
    if ($null -ne $settings.QlibNextDayPrediction) {
        $settings.QlibNextDayPrediction.ScriptPath = "..\..\tools\qlib_next_day\run_next_day_direction_experiment.ps1"
        $settings.QlibNextDayPrediction.WorkingDirectory = "..\..\tools\qlib_next_day"
        $settings.QlibNextDayPrediction.OutputRoot = "..\..\data\qlib\next_day_direction_outputs"
        $settings.QlibNextDayPrediction.SymbolsWorkDirectory = "..\..\data\next-day-prediction"
    }
    $settings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $SettingsPath -Encoding UTF8
}

function Ensure-PythonRuntime() {
    if (-not [string]::IsNullOrWhiteSpace($PythonRuntimePath)) {
        if (-not (Test-Path (Join-Path $PythonRuntimePath "python.exe"))) {
            throw "Python runtime does not contain python.exe: $PythonRuntimePath"
        }
        return
    }

    $cachedRuntime = Join-Path $PythonCacheRoot "python-$PythonVersion-embed-amd64"
    if (Test-Path (Join-Path $cachedRuntime "python.exe")) {
        $script:PythonRuntimePath = $cachedRuntime
        return
    }

    New-Item -ItemType Directory -Path $PythonCacheRoot -Force | Out-Null
    $zipPath = Join-Path $PythonCacheRoot "python-$PythonVersion-embed-amd64.zip"
    $url = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
    if (-not (Test-Path $zipPath)) {
        Write-Host "Downloading bundled Python runtime: $url"
        try {
            Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
        }
        catch {
            throw "Unable to download the official Python embeddable runtime. Check network access or pass -PythonRuntimePath to a local Python directory. $($_.Exception.Message)"
        }
    }

    if ((Get-Item $zipPath).Length -lt 1000000) {
        throw "Downloaded Python runtime is incomplete: $zipPath"
    }

    Remove-Item -LiteralPath $cachedRuntime -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -LiteralPath $zipPath -DestinationPath $cachedRuntime -Force
    if (-not (Test-Path (Join-Path $cachedRuntime "python.exe"))) {
        throw "Python runtime archive did not contain python.exe: $zipPath"
    }
    $script:PythonRuntimePath = $cachedRuntime
}

function Enable-EmbeddedPythonSitePackages([string]$RuntimePath) {
    $pth = Get-ChildItem -LiteralPath $RuntimePath -Filter "*._pth" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $pth) { return }

    $lines = @(Get-Content -LiteralPath $pth.FullName)
    if (-not ($lines -contains "Lib/site-packages")) {
        $lines += "Lib/site-packages"
    }
    $lines = $lines | ForEach-Object {
        if ($_ -eq "#import site") { "import site" } else { $_ }
    }
    $lines | Set-Content -LiteralPath $pth.FullName -Encoding ASCII
}

function Ensure-NonSecretConfig() {
    $configRoot = Join-Path $StageRoot "config"
    New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
    $secrets = Join-Path $configRoot "secrets.example.json"
    '{"EastMoneyQuantToken":""}' | Set-Content -LiteralPath $secrets -Encoding UTF8
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null

$packageArgs = @(
    "-PackageKind", "Full",
    "-OutputRoot", (Join-Path $RepoRoot "artifacts\packages"),
    "-NoZip"
)
if (-not [string]::IsNullOrWhiteSpace($HistoricalDuckDbPath)) {
    $packageArgs += @("-HistoricalDuckDbPath", $HistoricalDuckDbPath)
}
Invoke-Checked "powershell" (@("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PackageScript) + $packageArgs)

Ensure-PythonRuntime

Remove-Item -LiteralPath $RuntimeRoot -Recurse -Force -ErrorAction SilentlyContinue
Copy-TreeFiltered $PythonRuntimePath $RuntimeRoot
if (-not [string]::IsNullOrWhiteSpace($PythonPackagesPath)) {
    Copy-TreeFiltered $PythonPackagesPath (Join-Path $RuntimeRoot "Lib\site-packages")
}
else {
    # Merge the dependencies required by both Python task groups.
    $packageRoots = @(
        (Join-Path $RepoRoot "tools\history_update\.python_packages")
        (Join-Path $RepoRoot "tools\eastmoney_quant\.python_packages")
    )
    foreach ($packages in $packageRoots) {
        if (Test-Path $packages) {
            Copy-TreeFiltered $packages (Join-Path $RuntimeRoot "Lib\site-packages")
        }
    }
}
Enable-EmbeddedPythonSitePackages $RuntimeRoot

Set-RelativeRuntimeSettings (Join-Path $StageRoot "app\service\appsettings.json")
Ensure-NonSecretConfig

$manifestPath = Join-Path $StageRoot "package-manifest.json"
$manifest = if (Test-Path $manifestPath) { Get-Content -Raw $manifestPath | ConvertFrom-Json } else { [pscustomobject]@{} }
foreach ($property in @{
    appVersion = $Version
    packageFormat = "inno-setup"
    pythonRuntime = "bundled"
    dataPolicy = "preserve-on-upgrade"
}.GetEnumerator()) {
    if ($manifest.PSObject.Properties.Name -contains $property.Key) {
        $manifest.($property.Key) = $property.Value
    }
    else {
        $manifest | Add-Member -MemberType NoteProperty -Name $property.Key -Value $property.Value
    }
}
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

if (-not $SkipCompile) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -eq $iscc) {
        $known = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($null -eq $known) { throw "Inno Setup compiler not found. Install Inno Setup 6 or pass -SkipCompile." }
        $isccPath = $known
    }
    else {
        $isccPath = $iscc.Source
    }

    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    Invoke-Checked $isccPath @("/DAppVersion=$Version", $IssScript)
    if (-not (Test-Path $InstallerPath)) { throw "Installer was not produced: $InstallerPath" }
    $hash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $InstallerPath)" | Set-Content -LiteralPath "$InstallerPath.sha256" -Encoding ASCII
    Write-Host "Installer: $InstallerPath"
    Write-Host "SHA256: $hash"
}

Write-Host "Staging directory: $StageRoot"
