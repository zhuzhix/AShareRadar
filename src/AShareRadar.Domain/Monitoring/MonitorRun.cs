namespace AShareRadar.Domain.Monitoring;

public sealed record MonitorRun(
    Guid Id,
    Guid SessionId,
    DateTimeOffset ScanTime,
    MarketStatus MarketStatus,
    int HitCount,
    int NewCount,
    int ActiveCount,
    int DisappearedCount,
    long DurationMilliseconds);
