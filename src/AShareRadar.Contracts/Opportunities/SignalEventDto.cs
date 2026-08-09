namespace AShareRadar.Contracts.Opportunities;

public sealed record SignalEventDto(
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
    IReadOnlyList<StrategyHitDto> StrategyHits,
    IReadOnlyList<SignalHeatContextDto>? HeatContexts = null);
