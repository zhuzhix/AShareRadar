namespace AShareRadar.Contracts.Review;

public sealed record TodayReviewDto(
    DateOnly TradingDate,
    int OpportunityCount,
    int FocusedCount,
    int GivenUpCount,
    int WaitPullbackCount,
    decimal AverageScore,
    IReadOnlyList<StrategyReviewDto> Strategies,
    IReadOnlyList<ReviewOpportunityDto> Opportunities);

public sealed record StrategyReviewDto(
    string StrategyName,
    int HitCount,
    decimal AverageScore);

public sealed record ReviewOpportunityDto(
    string Symbol,
    string Name,
    string Status,
    string? ManualTag,
    decimal CurrentScore,
    int HitCount,
    DateTimeOffset FirstSeenTime,
    DateTimeOffset LastSeenTime);
