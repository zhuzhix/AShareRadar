namespace AShareRadar.Contracts.Opportunities;

public sealed record OpportunityDto(
    Guid Id,
    string Symbol,
    string Name,
    string Status,
    decimal CurrentScore,
    decimal BestScore,
    int HitCount,
    DateTimeOffset FirstSeenTime,
    DateTimeOffset LastSeenTime,
    string? ManualTag,
    string? Note,
    string StrategySummary,
    string StrategyExplanation);
