param(
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$RunDir = Join-Path $ProjectRoot ".run"
$PidPath = Join-Path $RunDir "servicehost.pid"
$PortPath = Join-Path $RunDir "servicehost.port"
$ServiceHostMarker = "AShareRadar.ServiceHost"

function Write-Info {
    param([string]$Message)
    if (-not $Quiet) {
        Write-Host $Message
    }
}

function Test-IsServiceHostProcess {
    param(
        [int]$ProcessId,
        [string]$CommandLine
    )

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return $false
    }

    return $CommandLine.Contains($ServiceHostMarker) -and $CommandLine.Contains($ProjectRoot)
}

function Get-ServiceHostProcessIds {
    $ids = New-Object System.Collections.Generic.HashSet[int]

    if (Test-Path $PidPath) {
        $rawPid = (Get-Content -LiteralPath $PidPath -ErrorAction SilentlyContinue | Select-Object -First 1)
        $pidFromFile = 0
        if ([int]::TryParse($rawPid, [ref]$pidFromFile)) {
            $process = Get-Process -Id $pidFromFile -ErrorAction SilentlyContinue
            if ($process) {
                [void]$ids.Add($pidFromFile)
            }
        }
    }

    try {
        $processes = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'"
        foreach ($process in $processes) {
            if (Test-IsServiceHostProcess -ProcessId $process.ProcessId -CommandLine $process.CommandLine) {
                [void]$ids.Add([int]$process.ProcessId)
            }
        }
    }
    catch {
        Write-Info "Cannot read dotnet command line. Falling back to PID file only."
    }

    return @($ids)
}

$targetIds = Get-ServiceHostProcessIds
if ($targetIds.Count -eq 0) {
    Write-Info "AShareRadar.ServiceHost is not running."
    if (Test-Path $PidPath) {
        Remove-Item -LiteralPath $PidPath -Force -ErrorAction SilentlyContinue
    }
    exit 0
}

foreach ($targetId in $targetIds) {
    try {
        Write-Info "Stopping AShareRadar.ServiceHost PID $targetId ..."
        Stop-Process -Id $targetId -Force -ErrorAction Stop
        Wait-Process -Id $targetId -Timeout 8 -ErrorAction SilentlyContinue
    }
    catch {
        Write-Warning "Failed to stop PID $targetId`: $($_.Exception.Message)"
    }
}

if (Test-Path $PidPath) {
    Remove-Item -LiteralPath $PidPath -Force -ErrorAction SilentlyContinue
}

if (Test-Path $PortPath) {
    Remove-Item -LiteralPath $PortPath -Force -ErrorAction SilentlyContinue
}

Write-Info "AShareRadar.ServiceHost stopped."
