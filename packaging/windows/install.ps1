param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AShareRadar"
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

    if (-not (Test-Command $CheckCommand)) {
        throw "$Name install finished but command '$CheckCommand' is still unavailable. Reopen PowerShell and rerun install.ps1."
    }
}

function Ensure-DotNetRuntime() {
    $desktopOk = $false
    $aspnetOk = $false
    if (Test-Command "dotnet") {
        $runtimes = dotnet --list-runtimes
        $desktopOk = $runtimes -match "Microsoft.WindowsDesktop.App 8\."
        $aspnetOk = $runtimes -match "Microsoft.AspNetCore.App 8\."
    }

    if (-not $desktopOk) {
        Ensure-WingetPackage "Microsoft.DotNet.DesktopRuntime.8" "dotnet" ".NET 8 Desktop Runtime"
    }

    if (-not $aspnetOk) {
        Ensure-WingetPackage "Microsoft.DotNet.AspNetCore.8" "dotnet" ".NET 8 ASP.NET Core Runtime"
    }
}

function Ensure-PythonDeps([string]$ToolDir) {
    Ensure-WingetPackage "Python.Python.3.12" "python" "Python 3.12"

    $requirements = Join-Path $ToolDir "requirements.txt"
    $target = Join-Path $ToolDir ".python_packages"
    if (Test-Path $requirements) {
        if (-not (Test-Path $target)) {
            New-Item -ItemType Directory -Path $target | Out-Null
        }

        Invoke-WithRetry {
            python -m pip install -r $requirements -t $target --upgrade
        } "Python dependencies"
    }
}

Write-Host "Installing AShareRadar to $InstallDir"
Ensure-DotNetRuntime

if (Test-Path $InstallDir) {
    Get-ChildItem -Path $InstallDir -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}

Copy-Item -Path (Join-Path $PackageRoot "app") -Destination $InstallDir -Recurse -Force

$serviceDir = Join-Path $InstallDir "app\service"
$toolDir = Join-Path $serviceDir "tools\history_update"
Ensure-PythonDeps $toolDir

$settingsPath = Join-Path $serviceDir "appsettings.json"
$settings = Get-Content -Raw $settingsPath | ConvertFrom-Json
$settings.Database.SqlitePath = "data/runtime/ashare-radar.sqlite"
$settings.Database.DuckDbPath = "data/ashare.duckdb"
$settings.MarketData.SectorMappingPath = "data/sector-mapping.csv"
$settings.MarketData.ConceptMappingPath = "data/concept-mapping.csv"
$settings.HistoricalDataUpdate.PythonPath = "python"
$settings.HistoricalDataUpdate.ScriptPath = "tools/history_update/update_history_data.py"
$settings.HistoricalDataUpdate.DataDir = "data"
$settings | ConvertTo-Json -Depth 20 | Set-Content -Path $settingsPath -Encoding UTF8

Copy-Item -Path (Join-Path $PackageRoot "start-ashare-radar.ps1") -Destination $InstallDir -Force
Copy-Item -Path (Join-Path $PackageRoot "stop-ashare-radar.ps1") -Destination $InstallDir -Force
Copy-Item -Path (Join-Path $PackageRoot "README.md") -Destination $InstallDir -Force

Write-Host "Install complete."
Write-Host "Run: powershell -ExecutionPolicy Bypass -File `"$InstallDir\start-ashare-radar.ps1`""
