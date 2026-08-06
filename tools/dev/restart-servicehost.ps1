param(
    [int]$Port = 18730,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$DisableHistoryUpdate,
    [string]$MarketDataProvider = ""
)

$ErrorActionPreference = "Stop"

$stopScript = Join-Path $PSScriptRoot "stop-servicehost.ps1"
$startScript = Join-Path $PSScriptRoot "start-servicehost.ps1"

& $stopScript

$startArgs = @{
    Port = $Port
    Configuration = $Configuration
}

if ($NoBuild) {
    $startArgs.NoBuild = $true
}

if ($DisableHistoryUpdate) {
    $startArgs.DisableHistoryUpdate = $true
}

if (-not [string]::IsNullOrWhiteSpace($MarketDataProvider)) {
    $startArgs.MarketDataProvider = $MarketDataProvider
}

& $startScript @startArgs
