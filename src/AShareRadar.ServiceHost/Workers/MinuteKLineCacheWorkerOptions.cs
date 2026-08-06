namespace AShareRadar.ServiceHost.Workers;

public sealed class MinuteKLineCacheWorkerOptions
{
    public bool Enabled { get; set; } = true;

    public bool RunOnStartup { get; set; }

    public int ActiveIntervalSeconds { get; set; } = 60;

    public int IdleIntervalMinutes { get; set; } = 30;

    public string ActiveStartTime { get; set; } = "09:25";

    public string ActiveEndTime { get; set; } = "15:10";

    public int CandidateCount { get; set; } = 420;

    public int MinuteBarCount { get; set; } = 360;
}
