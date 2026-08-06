namespace AShareRadar.Application.Monitoring;

public sealed class MarketSentimentStrategyOptions
{
    public bool Enabled { get; set; } = true;

    public int MaxSnapshotAgeMinutes { get; set; } = 5;

    public bool EnableActionDemotion { get; set; } = true;

    public decimal DemoteAggressiveBelowTemperature { get; set; } = 35m;

    public decimal OverheatedRiskTemperature { get; set; } = 80m;

    public SentimentAdjustmentRule Frozen { get; set; } = new(-18m, -8m, 0m);

    public SentimentAdjustmentRule Cold { get; set; } = new(-10m, -4m, 0m);

    public SentimentAdjustmentRule Neutral { get; set; } = new(0m, 0m, 0m);

    public SentimentAdjustmentRule Hot { get; set; } = new(4m, 4m, 8m);

    public SentimentAdjustmentRule Overheated { get; set; } = new(-12m, -6m, 0m);
}

public sealed record SentimentAdjustmentRule(
    decimal Aggressive,
    decimal Defensive,
    decimal MainlineOrTrend);
