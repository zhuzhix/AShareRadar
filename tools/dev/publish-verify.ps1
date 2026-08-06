param(
    [string]$Configuration = "Release",
    [string]$ServiceHostOutput = "C:\Users\Administrator\Documents\Codex\2026-07-27\z\work\AShareRadar\artifacts\verify-servicehost",
    [string]$DesktopOutput = "C:\Users\Administrator\Documents\Codex\2026-07-27\z\work\AShareRadar\artifacts\verify-desktop",
    [string]$BaseUrl = "http://127.0.0.1:18730",
    [switch]$SkipArchiveMissingEvents,
    [switch]$StartDesktop
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$serviceProject = Join-Path $repoRoot "src\AShareRadar.ServiceHost\AShareRadar.ServiceHost.csproj"
$desktopProject = Join-Path $repoRoot "src\AShareRadar.Desktop\AShareRadar.Desktop.csproj"
$serviceExe = Join-Path $ServiceHostOutput "AShareRadar.ServiceHost.exe"
$desktopExe = Join-Path $DesktopOutput "AShareRadar.Desktop.exe"

function Stop-AppProcess {
    param([string]$Name)

    $processes = Get-Process -Name $Name -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        Write-Host "Stopping $Name pid=$($process.Id)"
        Stop-Process -Id $process.Id -Force
    }
}

function Wait-HttpOk {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            return Invoke-RestMethod -Uri $Url -TimeoutSec 5
        }
        catch {
            Start-Sleep -Seconds 1
        }
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for $Url"
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

Push-Location $repoRoot
try {
    Stop-AppProcess -Name "AShareRadar.Desktop"
    Stop-AppProcess -Name "AShareRadar.ServiceHost"

    New-Item -ItemType Directory -Force -Path $ServiceHostOutput | Out-Null
    New-Item -ItemType Directory -Force -Path $DesktopOutput | Out-Null

    Write-Host "Publishing ServiceHost to $ServiceHostOutput"
    Invoke-Checked "dotnet" @("publish", $serviceProject, "-c", $Configuration, "-o", $ServiceHostOutput, "--no-restore")

    Write-Host "Publishing Desktop to $DesktopOutput"
    Invoke-Checked "dotnet" @("publish", $desktopProject, "-c", $Configuration, "-o", $DesktopOutput, "--no-restore")

    Write-Host "Starting ServiceHost"
    Start-Process -FilePath $serviceExe -WorkingDirectory $ServiceHostOutput -WindowStyle Hidden

    $monitor = Wait-HttpOk -Url "$BaseUrl/api/monitor/status" -TimeoutSeconds 45
    $sentiment = Wait-HttpOk -Url "$BaseUrl/api/market-sentiment/snapshot?refresh=true" -TimeoutSeconds 60
    $opportunities = Wait-HttpOk -Url "$BaseUrl/api/opportunities?view=Current" -TimeoutSeconds 30

    $archiveResult = $null
    if (-not $SkipArchiveMissingEvents) {
        Write-Host "Archiving opportunities without signal event details"
        $archiveResult = Invoke-RestMethod -Uri "$BaseUrl/api/maintenance/opportunities/archive-missing-events" -Method Post -TimeoutSec 30
        $archivePath = Join-Path $repoRoot ("artifacts\archive-missing-events-{0}.json" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
        $archiveResult | ConvertTo-Json -Depth 8 | Set-Content -Path $archivePath -Encoding UTF8
        Write-Host "Archive result saved to $archivePath"
    }

    if ($StartDesktop) {
        Write-Host "Starting Desktop"
        Start-Process -FilePath $desktopExe -WorkingDirectory $DesktopOutput
    }

    [pscustomobject]@{
        ServiceHostOutput = $ServiceHostOutput
        DesktopOutput = $DesktopOutput
        MonitorStatus = $monitor.monitorStatus
        MarketStatus = $monitor.marketStatus
        SentimentScore = $sentiment.temperatureScore
        SentimentLevel = $sentiment.level
        OpportunityCount = @($opportunities).Count
        ArchivedMissingEventCount = if ($archiveResult -eq $null) { $null } else { $archiveResult.archivedCount }
    } | Format-List
}
finally {
    Pop-Location
}
