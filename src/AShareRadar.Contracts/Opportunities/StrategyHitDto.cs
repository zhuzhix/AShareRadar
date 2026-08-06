namespace AShareRadar.Contracts.Opportunities;

public sealed record StrategyHitDto(
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    string Reason,
    string? Risk,
    IReadOnlyDictionary<string, decimal>? Metrics,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<string>? PassedConditions,
    IReadOnlyList<string>? FailedConditions,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice);
