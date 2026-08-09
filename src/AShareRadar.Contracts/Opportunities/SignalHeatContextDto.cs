namespace AShareRadar.Contracts.Opportunities;

public sealed record SignalHeatContextDto(
    Guid EventId,
    string Symbol,
    DateTimeOffset EventTime,
    string ContextType,
    string Code,
    string Name,
    int HeatRank,
    int StockCount,
    int RisingCount,
    decimal AverageChangePercent,
    decimal RisingRatioPercent,
    decimal TotalAmount,
    decimal HeatScore,
    bool IsLeader,
    string? HeatSnapshotBatchId,
    DateTimeOffset CreatedAt);
