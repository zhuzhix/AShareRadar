namespace AShareRadar.Application.History;

public sealed record HistoricalSignalItem(
    Guid Id,
    Guid OpportunityId,
    DateTimeOffset EventTime,
    string EventType,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    string Reason,
    string? Risk,
    int StrategyHitCount);
