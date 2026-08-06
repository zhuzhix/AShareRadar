namespace AShareRadar.Contracts.MarketData;

public sealed record MarketSentimentSnapshotDto(
    DateTimeOffset SnapshotTime,
    string ProviderName,
    decimal TemperatureScore,
    string Level,
    string Summary,
    string DataQuality,
    IReadOnlyList<MarketSentimentCategoryDto> Categories,
    IReadOnlyList<MarketSentimentMetricDto> Metrics,
    IReadOnlyList<string> Warnings);

public sealed record MarketSentimentCategoryDto(
    string Code,
    string Name,
    decimal Score,
    string Status,
    string Description);

public sealed record MarketSentimentMetricDto(
    string Code,
    string Name,
    decimal? Value,
    string DisplayValue,
    string Unit,
    string CategoryCode,
    bool IsAvailable,
    string SourceStatus = "Realtime");

public sealed record MarketSentimentDataSourceStatusDto(
    string Code,
    string Status,
    string Message,
    DateTimeOffset CheckedAt);

public sealed record MarketSentimentRegimeDto(
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string StartLevel,
    string EndLevel,
    decimal StartScore,
    decimal EndScore,
    decimal MinScore,
    decimal MaxScore,
    int SnapshotCount,
    string Label);

public sealed record MarketSentimentStrategyRulesDto(
    bool Enabled,
    int MaxSnapshotAgeMinutes,
    bool EnableActionDemotion,
    decimal DemoteAggressiveBelowTemperature,
    decimal OverheatedRiskTemperature,
    SentimentAdjustmentRuleDto Frozen,
    SentimentAdjustmentRuleDto Cold,
    SentimentAdjustmentRuleDto Neutral,
    SentimentAdjustmentRuleDto Hot,
    SentimentAdjustmentRuleDto Overheated);

public sealed record SentimentAdjustmentRuleDto(
    decimal Aggressive,
    decimal Defensive,
    decimal MainlineOrTrend);
