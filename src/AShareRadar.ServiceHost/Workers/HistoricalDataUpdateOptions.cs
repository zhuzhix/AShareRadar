namespace AShareRadar.ServiceHost.Workers;

public sealed class HistoricalDataUpdateOptions
{
    public bool Enabled { get; set; } = true;

    public bool RunOnStartup { get; set; }

    public int StartupDelaySeconds { get; set; } = 5;

    public string RunAfterTime { get; set; } = "15:15";

    public int CheckIntervalMinutes { get; set; } = 5;

    public string PythonPath { get; set; } = "";

    public string ScriptPath { get; set; } = "";

    public string DataDir { get; set; } = "";

    public string AdjustFlag { get; set; } = "2";

    public int Limit { get; set; }

    public bool Rebuild { get; set; }

    public bool IncludeWeekly { get; set; } = true;

    public string StartDate { get; set; } = "2015-01-01";
}
