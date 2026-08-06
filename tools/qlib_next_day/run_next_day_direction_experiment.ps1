param(
    [string]$Symbols = "",
    [string]$SymbolsFile = "",
    [string]$SignalDate = "auto",
    [int]$Threads = 19,
    [string]$OutputRoot = "C:\Users\Administrator\Documents\Codex\2026-08-01\zhi-x\next_day_direction_outputs"
)

$ErrorActionPreference = "Stop"

$WorkspaceRoot = "C:\Users\Administrator\Documents\Codex\2026-08-01\zhi-x"
$Python = "C:\Users\Administrator\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
$ProgressPath = Join-Path $OutputRoot "progress.json"

if (-not (Test-Path $Python)) {
    throw "Python runtime not found: $Python"
}

if ($Symbols -eq "" -and $SymbolsFile -eq "") {
    throw "Please pass -Symbols or -SymbolsFile."
}

$ArgsList = @(
    (Join-Path $WorkspaceRoot "run_next_day_direction_experiment.py"),
    "--signal-date", $SignalDate,
    "--threads", $Threads,
    "--output-root", $OutputRoot,
    "--progress", $ProgressPath
)

if ($Symbols -ne "") {
    $ArgsList += @("--symbols", $Symbols)
}

if ($SymbolsFile -ne "") {
    $ArgsList += @("--symbols-file", $SymbolsFile)
}

Write-Host "Running Qlib next-day direction experiment..."
Write-Host "Signal date: $SignalDate"
Write-Host "Output root: $OutputRoot"
& $Python @ArgsList

if ($LASTEXITCODE -ne 0) {
    throw "Next-day direction experiment failed. Progress file: $ProgressPath"
}

Write-Host "Done. Progress file:"
Write-Host $ProgressPath
