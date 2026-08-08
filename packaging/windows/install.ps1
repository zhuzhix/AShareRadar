param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AShareRadar",
    [string]$Token = "",
    [switch]$OverwriteData,
    [switch]$SkipPythonDeps,
    [switch]$UpgradeOnly,
    [switch]$ConfigureOnly,
    [switch]$NoShortcut
)

$ErrorActionPreference = "Stop"
$PackageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Test-Command($Name) {
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Invoke-WithRetry([scriptblock]$Action, [string]$Name, [int]$MaxAttempts = 3) {
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Write-Host "[$Name] attempt $attempt/$MaxAttempts"
            & $Action
            return
        }
        catch {
            if ($attempt -eq $MaxAttempts) { throw }
            Start-Sleep -Seconds (3 * $attempt)
        }
    }
}

function Ensure-WingetPackage([string]$Id, [string]$CheckCommand, [string]$Name) {
    if (Test-Command $CheckCommand) {
        Write-Host "$Name already installed."
        return
    }

    if (-not (Test-Command "winget")) {
        throw "$Name is missing and winget is unavailable. Please install $Name manually, then rerun install.ps1."
    }

    Invoke-WithRetry {
        winget install -e --id $Id --silent --accept-package-agreements --accept-source-agreements
    } $Name
}

function Ensure-PythonDeps([string]$InstallRoot) {
    if ($SkipPythonDeps) { return }
    Ensure-WingetPackage "Python.Python.3.12" "python" "Python 3.12"

    $requirements = Join-Path $InstallRoot "tools\python\requirements.txt"
    $target = Join-Path $InstallRoot "tools\python\.python_packages"
    if (Test-Path $requirements) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Invoke-WithRetry {
            python -m pip install -r $requirements -t $target --upgrade
        } "Python dependencies"
    }
}

function Copy-Tree([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) { return }
    if ((Resolve-Path $Source).Path -eq (Resolve-Path (Split-Path -Parent $Destination)).Path + "\" + (Split-Path -Leaf $Destination)) {
        return
    }
    if (Test-Path $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function Copy-DataTree([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) { return }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -Path $Source -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring((Resolve-Path $Source).Path.Length).TrimStart("\")
        if ([string]::IsNullOrWhiteSpace($relative)) { return }
        $target = Join-Path $Destination $relative
        if ($_.PSIsContainer) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            return
        }

        if ((Test-Path $target) -and -not $OverwriteData) {
            return
        }

        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

function Set-IfExists($Object, [string]$Property, $Value) {
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Property) {
        $Object.$Property = $Value
    }
}

function Rewrite-AppSettings([string]$SettingsPath, [string]$InstallRoot) {
    if (-not (Test-Path $SettingsPath)) { throw "Missing appsettings.json: $SettingsPath" }
    $settings = Get-Content -Raw $SettingsPath | ConvertFrom-Json

    Set-IfExists $settings.Database "SqlitePath" (Join-Path $InstallRoot "data\runtime\ashare-radar.sqlite")
    Set-IfExists $settings.Database "DuckDbPath" (Join-Path $InstallRoot "data\ashare.duckdb")
    Set-IfExists $settings.MarketData "SectorMappingPath" (Join-Path $InstallRoot "data\sector-mapping.csv")
    Set-IfExists $settings.MarketData "ConceptMappingPath" (Join-Path $InstallRoot "data\concept-mapping.csv")

    Set-IfExists $settings.EastMoneyQuant "PythonPath" "python"
    Set-IfExists $settings.EastMoneyQuant "RealtimeScriptPath" (Join-Path $InstallRoot "tools\eastmoney_quant\realtime_snapshot_gm.py")
    Set-IfExists $settings.EastMoneyQuant "KLineScriptPath" (Join-Path $InstallRoot "tools\eastmoney_quant\kline_gm.py")
    Set-IfExists $settings.EastMoneyQuant "DuckDbPath" (Join-Path $InstallRoot "data\ashare.duckdb")

    Set-IfExists $settings.HistoricalDataUpdate "PythonPath" "python"
    Set-IfExists $settings.HistoricalDataUpdate "ScriptPath" (Join-Path $InstallRoot "tools\eastmoney_quant\update_history_data_gm.py")
    Set-IfExists $settings.HistoricalDataUpdate "DataDir" (Join-Path $InstallRoot "data")

    Set-IfExists $settings.MarketMappingUpdate "PythonPath" "python"
    Set-IfExists $settings.MarketMappingUpdate "ScriptPath" (Join-Path $InstallRoot "tools\eastmoney_quant\update_sector_concept_mapping_gm.py")
    Set-IfExists $settings.MarketMappingUpdate "OutputDataDir" (Join-Path $InstallRoot "data")

    Set-IfExists $settings.ExternalSentimentSdkUpdate "PythonPath" "python"
    Set-IfExists $settings.ExternalSentimentSdkUpdate "ScriptPath" (Join-Path $InstallRoot "tools\eastmoney_quant\update_external_sentiment_gm.py")
    Set-IfExists $settings.ExternalSentimentSdkUpdate "OutputPath" (Join-Path $InstallRoot "data\market-sentiment-external.csv")

    Set-IfExists $settings.QlibNextDayPrediction "ScriptPath" (Join-Path $InstallRoot "tools\qlib_next_day\run_next_day_direction_experiment.ps1")
    Set-IfExists $settings.QlibNextDayPrediction "WorkingDirectory" (Join-Path $InstallRoot "tools\qlib_next_day")
    Set-IfExists $settings.QlibNextDayPrediction "OutputRoot" (Join-Path $InstallRoot "data\qlib\next_day_direction_outputs")
    Set-IfExists $settings.QlibNextDayPrediction "SymbolsWorkDirectory" (Join-Path $InstallRoot "data\next-day-prediction")
    Set-IfExists $settings.TradingCalendar "CalendarPath" (Join-Path $InstallRoot "data\trading-calendar-cn.json")

    $settings | ConvertTo-Json -Depth 50 | Set-Content -Path $SettingsPath -Encoding UTF8
}

function Ensure-Token([string]$Value) {
    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        [Environment]::SetEnvironmentVariable("EASTMONEY_QUANT_TOKEN", $Value, "User")
        $env:EASTMONEY_QUANT_TOKEN = $Value
        return
    }

    if ([string]::IsNullOrWhiteSpace($env:EASTMONEY_QUANT_TOKEN)) {
        Write-Host "EASTMONEY_QUANT_TOKEN is not set. Realtime SDK calls may fail until it is configured."
        Write-Host "You can set it later with:"
        Write-Host "[Environment]::SetEnvironmentVariable('EASTMONEY_QUANT_TOKEN', '<token>', 'User')"
    }
}

function Create-DesktopShortcut([string]$InstallRoot) {
    $desktop = [Environment]::GetFolderPath("Desktop")
    if ([string]::IsNullOrWhiteSpace($desktop)) { return }
    $shortcutPath = Join-Path $desktop "AShareRadar.lnk"
    $target = Join-Path $InstallRoot "start-ashare-radar.ps1"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = "powershell.exe"
    $shortcut.Arguments = "-ExecutionPolicy Bypass -File `"$target`""
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.Save()
}

Write-Host "Installing AShareRadar to $InstallDir"
Get-Process AShareRadar.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process AShareRadar.ServiceHost -ErrorAction SilentlyContinue | Stop-Process -Force

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $InstallDir "data\runtime") -Force | Out-Null

if (-not $ConfigureOnly) {
    Copy-Tree (Join-Path $PackageRoot "app") (Join-Path $InstallDir "app")
    Copy-Tree (Join-Path $PackageRoot "tools") (Join-Path $InstallDir "tools")
    Copy-Tree (Join-Path $PackageRoot "runtimes") (Join-Path $InstallDir "runtimes")
    if (-not $UpgradeOnly) {
        Copy-DataTree (Join-Path $PackageRoot "data") (Join-Path $InstallDir "data")
    }

    foreach ($name in @("install.ps1", "uninstall.ps1", "upgrade.ps1", "doctor.ps1", "start-ashare-radar.ps1", "stop-ashare-radar.ps1", "README.md", "package-manifest.json")) {
        $source = Join-Path $PackageRoot $name
        if (Test-Path $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $InstallDir $name) -Force
        }
    }
}

Rewrite-AppSettings (Join-Path $InstallDir "app\service\appsettings.json") $InstallDir
Ensure-PythonDeps $InstallDir
Ensure-Token $Token
if (-not $NoShortcut) {
    Create-DesktopShortcut $InstallDir
}

Write-Host "Install complete."
Write-Host "Run: powershell -ExecutionPolicy Bypass -File `"$InstallDir\start-ashare-radar.ps1`""
