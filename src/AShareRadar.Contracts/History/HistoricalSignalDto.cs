namespace AShareRadar.Contracts.History;

public sealed record HistoricalSignalDto(
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
