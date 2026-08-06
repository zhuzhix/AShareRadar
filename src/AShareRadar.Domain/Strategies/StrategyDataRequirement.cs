namespace AShareRadar.Domain.Strategies;

public sealed record StrategyDataRequirement(
    bool RequiresRealtimeQuote,
    bool RequiresDailyKLine,
    bool RequiresMinuteKLine,
    bool RequiresSectorData,
    bool RequiresCapitalFlow,
    int MinDailyBarCount);
