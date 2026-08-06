namespace AShareRadar.ServiceHost.Workers;

public sealed class MarketSentimentWorkerOptions
{
    public bool Enabled { get; set; } = true;

    public bool RunOnStartup { get; set; } = true;

    public int ActiveIntervalSeconds { get; set; } = 60;

    public int IdleIntervalMinutes { get; set; } = 30;

    public string ActiveStartTime { get; set; } = "09:25";

    public string ActiveEndTime { get; set; } = "15:10";
}
