namespace AShareRadar.Application.Review;

public sealed class QlibNextDayPredictionOptions
{
    public bool Enabled { get; set; } = true;

    public string PowerShellPath { get; set; } = "powershell";

    public string ScriptPath { get; set; } =
        @"C:\Users\Administrator\Documents\Codex\2026-08-01\zhi-x\run_next_day_direction_experiment.ps1";

    public string WorkingDirectory { get; set; } =
        @"C:\Users\Administrator\Documents\Codex\2026-08-01\zhi-x";

    public string OutputRoot { get; set; } =
        @"C:\Users\Administrator\Documents\Codex\2026-08-01\zhi-x\next_day_direction_outputs";

    public string SymbolsWorkDirectory { get; set; } = "data/next-day-prediction";

    public int TimeoutMinutes { get; set; } = 120;

    public int Threads { get; set; } = 19;

    public bool DeleteSymbolsFileAfterRun { get; set; } = true;
}
