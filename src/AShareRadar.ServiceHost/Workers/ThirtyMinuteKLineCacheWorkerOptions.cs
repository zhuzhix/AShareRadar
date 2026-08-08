namespace AShareRadar.ServiceHost.Workers;

public sealed class ThirtyMinuteKLineCacheWorkerOptions
{
    public bool Enabled { get; set; } = true;

    public bool RunOnStartup { get; set; }

    public int ActiveIntervalSeconds { get; set; } = 300;

    public int IdleIntervalMinutes { get; set; } = 30;

    public string ActiveStartTime { get; set; } = "09:25";

    public string ActiveEndTime { get; set; } = "15:10";

    public int CandidateCount { get; set; } = 800;

    public int BarCount { get; set; } = 240;
}
