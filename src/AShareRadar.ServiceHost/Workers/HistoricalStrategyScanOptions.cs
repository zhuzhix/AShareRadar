namespace AShareRadar.ServiceHost.Workers;

public sealed class HistoricalStrategyScanOptions
{
    public bool Enabled { get; set; } = true;

    public bool RunOnStartup { get; set; } = true;

    public int StartupDelaySeconds { get; set; } = 60;

    public string RunAfterTime { get; set; } = "18:10";

    public int CheckIntervalMinutes { get; set; } = 30;

    public int RepeatIntervalMinutes { get; set; } = 240;

    public string StockPool { get; set; } = "AShare";

    public int MaxSymbols { get; set; } = 1200;

    public int DailyBarCount { get; set; } = 100;

    public int LoadConcurrency { get; set; } = 8;
}
