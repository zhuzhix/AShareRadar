namespace AShareRadar.Application.Review;

public sealed record TodayReview(
    DateOnly TradingDate,
    int OpportunityCount,
    int FocusedCount,
    int GivenUpCount,
    int WaitPullbackCount,
    decimal AverageScore,
    IReadOnlyList<StrategyReview> Strategies,
    IReadOnlyList<ReviewOpportunity> Opportunities);

public sealed record StrategyReview(
    string StrategyName,
    int HitCount,
    decimal AverageScore);

public sealed record ReviewOpportunity(
    string Symbol,
    string Name,
    string Status,
    string? ManualTag,
    decimal CurrentScore,
    int HitCount,
    DateTimeOffset FirstSeenTime,
    DateTimeOffset LastSeenTime);
