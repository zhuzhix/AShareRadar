param(
    [string]$InstallDir = "",
    [string]$Token = "",
    [switch]$OverwriteData,
    [switch]$SkipPythonDeps,
    [switch]$UpgradeOnly,
    [switch]$ConfigureOnly,
    [switch]$NoShortcut
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = if (Test-Path -LiteralPath "E:\") { "E:\AShareRadar" } else { Join-Path $env:LOCALAPPDATA "AShareRadar" }
}
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

function Refresh-ProcessPath() {
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $parts = @($userPath, $machinePath) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
    $env:Path = $parts -join ";"
}

function Ensure-PythonDeps([string]$InstallRoot) {
    if ($SkipPythonDeps) { return }
    Ensure-WingetPackage "Python.Python.3.12" "python" "Python 3.12"
    Refresh-ProcessPath

    $python = Get-Command "python" -ErrorAction SilentlyContinue
    if ($null -eq $python) {
        throw "Python was installed or detected, but python.exe is not available on PATH. Open a new PowerShell window and rerun install.ps1."
    }

    $pythonVersion = (& $python.Source -c "import platform, sys; print(f'{sys.version_info.major}.{sys.version_info.minor};{platform.architecture()[0]}')" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $pythonVersion -notmatch '^3\.12;64bit$') {
        throw "Python 3.12 64-bit is required for the directory package. Detected: $pythonVersion"
    }
    & $python.Source -m pip --version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "The detected Python installation does not provide pip. Install Python 3.12 with pip and rerun install.ps1."
    }

    $requirements = Join-Path $InstallRoot "tools\python\requirements.txt"
    $target = Join-Path $InstallRoot "tools\eastmoney_quant\.python_packages"
    if (Test-Path $requirements) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Invoke-WithRetry {
            & $python.Source -m pip install -r $requirements -t $target --upgrade
            if ($LASTEXITCODE -ne 0) {
                throw "Python dependency installation failed with exit code $LASTEXITCODE."
            }
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

function Set-SectionToken($Settings, [string]$SectionName, [string]$Value) {
    $sectionProperty = $Settings.PSObject.Properties[$SectionName]
    if ($null -eq $sectionProperty) {
        $section = [pscustomobject]@{}
        $Settings | Add-Member -MemberType NoteProperty -Name $SectionName -Value $section
    }
    else {
        $section = $sectionProperty.Value
    }

    if ($section.PSObject.Properties.Name -contains "Token") {
        $section.Token = $Value
    }
    else {
        $section | Add-Member -MemberType NoteProperty -Name Token -Value $Value
    }
}

function Ensure-Token([string]$Value, [string]$InstallRoot) {
    $configDir = Join-Path $InstallRoot "config"
    $configPath = Join-Path $configDir "appsettings.local.json"
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null

    if (Test-Path $configPath) {
        $localSettings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    }
    else {
        $localSettings = [pscustomobject]@{}
    }

    $token = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($token)) {
        $token = $env:EASTMONEY_QUANT_TOKEN
    }
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        foreach ($sectionName in @("EastMoneyQuant", "EastMoneyQuantDotNet", "ExternalSentimentSdkUpdate")) {
            Set-SectionToken $localSettings $sectionName $token
        }
        Write-Host "Token saved to $configPath"
    }
    elseif (-not (Test-Path $configPath)) {
        foreach ($sectionName in @("EastMoneyQuant", "EastMoneyQuantDotNet", "ExternalSentimentSdkUpdate")) {
            Set-SectionToken $localSettings $sectionName ""
        }
        Write-Host "Token is not configured. Set it later in $configPath"
    }

    $localSettings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding UTF8
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
Ensure-Token $Token $InstallDir
if (-not $NoShortcut) {
    Create-DesktopShortcut $InstallDir
}

Write-Host "Install complete."
Write-Host "Run: powershell -ExecutionPolicy Bypass -File `"$InstallDir\start-ashare-radar.ps1`""
