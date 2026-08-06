param(
    [int]$Port = 18730,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$DisableHistoryUpdate,
    [string]$MarketDataProvider = "",
    [int]$StartupTimeoutSeconds = 15,
    [switch]$NoWait
)

$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ServiceHostProjectDir = Join-Path $ProjectRoot "src\AShareRadar.ServiceHost"
$ServiceHostProject = Join-Path $ServiceHostProjectDir "AShareRadar.ServiceHost.csproj"
$ServiceHostDll = Join-Path $ServiceHostProjectDir "bin\$Configuration\net8.0\AShareRadar.ServiceHost.dll"
$RunDir = Join-Path $ProjectRoot ".run"
$PidPath = Join-Path $RunDir "servicehost.pid"
$PortPath = Join-Path $RunDir "servicehost.port"
$StdoutPath = Join-Path $RunDir "servicehost.out.log"
$StderrPath = Join-Path $RunDir "servicehost.err.log"

function Quote-Argument {
    param([string]$Value)
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Test-ServiceReady {
    param([int]$TargetPort)

    try {
        $response = Invoke-WebRequest `
            -Uri "http://127.0.0.1:$TargetPort/api/monitor/status" `
            -UseBasicParsing `
            -TimeoutSec 2 `
            -ErrorAction Stop
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 500
    }
    catch {
        return $false
    }
}

function Write-LogTail {
    param(
        [string]$Path,
        [string]$Title
    )

    if (-not (Test-Path $Path)) {
        return
    }

    Write-Host $Title
    Get-Content -LiteralPath $Path -Tail 20 -ErrorAction SilentlyContinue
}

if (-not (Test-Path $RunDir)) {
    New-Item -ItemType Directory -Path $RunDir | Out-Null
}

if (-not $NoBuild) {
    dotnet build $ServiceHostProject -c $Configuration -p:UseAppHost=false --no-restore
}

if (-not (Test-Path $ServiceHostDll)) {
    throw "ServiceHost DLL not found: $ServiceHostDll. Build the project first."
}

if (Test-Path $PidPath) {
    $rawPid = (Get-Content -LiteralPath $PidPath -ErrorAction SilentlyContinue | Select-Object -First 1)
    $runningPid = 0
    if ([int]::TryParse($rawPid, [ref]$runningPid) -and (Get-Process -Id $runningPid -ErrorAction SilentlyContinue)) {
        Write-Host "AShareRadar.ServiceHost is already running. PID $runningPid."
        if (Test-Path $PortPath) {
            $runningPort = Get-Content -LiteralPath $PortPath -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($runningPort)) {
                Write-Host "URL: http://127.0.0.1:$runningPort"
            }
        }
        Write-Host "Run tools\dev\restart-servicehost.ps1 to restart it."
        exit 0
    }
}

$processStartInfo = New-Object System.Diagnostics.ProcessStartInfo
$processStartInfo.FileName = "dotnet"
$processStartInfo.Arguments = "$(Quote-Argument $ServiceHostDll) --urls $(Quote-Argument "http://127.0.0.1:$Port")"
$processStartInfo.WorkingDirectory = $ServiceHostProjectDir
$processStartInfo.UseShellExecute = $false
$processStartInfo.CreateNoWindow = $true

$previousHistoryUpdate = $env:HistoricalDataUpdate__Enabled
$previousMarketDataProvider = $env:MarketData__Provider
try {
    if ($DisableHistoryUpdate) {
        $env:HistoricalDataUpdate__Enabled = "false"
    }

    if (-not [string]::IsNullOrWhiteSpace($MarketDataProvider)) {
        $env:MarketData__Provider = $MarketDataProvider
    }

    $process = [System.Diagnostics.Process]::Start($processStartInfo)
}
finally {
    $env:HistoricalDataUpdate__Enabled = $previousHistoryUpdate
    $env:MarketData__Provider = $previousMarketDataProvider
}

Set-Content -LiteralPath $PidPath -Value $process.Id
Set-Content -LiteralPath $PortPath -Value $Port

Write-Host "AShareRadar.ServiceHost started."
Write-Host "PID: $($process.Id)"
Write-Host "URL: http://127.0.0.1:$Port"
Write-Host "Startup timeout: $StartupTimeoutSeconds seconds"

if ($NoWait) {
    exit 0
}

$deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $process.Refresh()
    if ($process.HasExited) {
        Write-Warning "AShareRadar.ServiceHost exited during startup. ExitCode: $($process.ExitCode)"
        Write-LogTail -Path $StdoutPath -Title "Last stdout lines:"
        Write-LogTail -Path $StderrPath -Title "Last stderr lines:"
        Remove-Item -LiteralPath $PidPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $PortPath -Force -ErrorAction SilentlyContinue
        exit 1
    }

    if (Test-ServiceReady -TargetPort $Port) {
        Write-Host "AShareRadar.ServiceHost is ready."
        exit 0
    }

    Start-Sleep -Milliseconds 500
}

Write-Warning "AShareRadar.ServiceHost did not become ready within $StartupTimeoutSeconds seconds. Stopping it to avoid a stale background process."
Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PidPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PortPath -Force -ErrorAction SilentlyContinue
Write-LogTail -Path $StdoutPath -Title "Last stdout lines:"
Write-LogTail -Path $StderrPath -Title "Last stderr lines:"
exit 2
