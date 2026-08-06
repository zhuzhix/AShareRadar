namespace AShareRadar.Application.History;

public sealed record StrategyPerformanceItem(
    string StrategyCode,
    string StrategyName,
    int HitCount,
    decimal AverageScore,
    decimal MaxScore,
    DateTimeOffset? LastHitTime);
