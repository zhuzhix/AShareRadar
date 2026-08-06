namespace AShareRadar.Domain.Monitoring;

public sealed record MonitorSession(
    Guid Id,
    DateOnly TradingDate,
    DateTimeOffset StartTime,
    MonitorStatus Status,
    int ScanIntervalSeconds);
