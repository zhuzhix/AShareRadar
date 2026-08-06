namespace AShareRadar.Domain.Monitoring;

public enum MonitorStatus
{
    NotStarted = 0,
    Running = 1,
    Paused = 2,
    Scanning = 3,
    DataSourceError = 4,
    Stopped = 5
}
