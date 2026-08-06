namespace AShareRadar.Contracts.Strategies;

public sealed record StrategyDefinitionDto(
    string Code,
    string Name,
    string Type,
    string Stage,
    string DefaultAction,
    StrategyDataRequirementDto DataRequirement,
    IReadOnlyDictionary<string, string> Parameters,
    string Description);

public sealed record StrategyDataRequirementDto(
    bool RequiresRealtimeQuote,
    bool RequiresDailyKLine,
    bool RequiresMinuteKLine,
    bool RequiresSectorData,
    bool RequiresCapitalFlow,
    int MinDailyBarCount);
