namespace AShareRadar.Application.Opportunities.Storage;

public sealed record OpportunityState(
    IReadOnlyList<OpportunityStateItem> Opportunities,
    IReadOnlyList<SignalEventStateItem> Events);

public sealed record OpportunityStateItem(
    Guid Id,
    DateOnly TradingDate,
    string Symbol,
    string Name,
    DateTimeOffset FirstSeenTime,
    DateTimeOffset LastSeenTime,
    string Status,
    int HitCount,
    decimal CurrentScore,
    decimal BestScore,
    string? ManualTag,
    string? Note);

public sealed record SignalEventStateItem(
    Guid Id,
    Guid OpportunityId,
    Guid RunId,
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
    IReadOnlyList<StrategyHitStateItem> StrategyHits);

public sealed record StrategyHitStateItem(
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    string Reason,
    string? Risk,
    IReadOnlyDictionary<string, decimal>? Metrics = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? PassedConditions = null,
    IReadOnlyList<string>? FailedConditions = null,
    decimal? StopLossPrice = null,
    decimal? TakeProfitPrice = null);
