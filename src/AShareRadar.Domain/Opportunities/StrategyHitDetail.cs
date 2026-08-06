namespace AShareRadar.Domain.Opportunities;

public sealed record StrategyHitDetail(
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
