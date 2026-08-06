namespace AShareRadar.Contracts.History;

public sealed record StrategyPerformanceDto(
    string StrategyCode,
    string StrategyName,
    int HitCount,
    decimal AverageScore,
    decimal MaxScore,
    DateTimeOffset? LastHitTime);
