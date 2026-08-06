namespace AShareRadar.Domain.Opportunities;

public sealed record SignalEvent(
    Guid Id,
    Guid OpportunityId,
    Guid RunId,
    DateTimeOffset EventTime,
    SignalEventType EventType,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    string Reason,
    string? Risk,
    IReadOnlyList<StrategyHitDetail> StrategyHits);
