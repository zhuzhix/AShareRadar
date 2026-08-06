namespace AShareRadar.Domain.Strategies;

public sealed record StrategyDefinition(
    string Code,
    string Name,
    StrategyType Type,
    StrategyStage Stage,
    StrategySignalAction DefaultAction,
    StrategyDataRequirement DataRequirement,
    IReadOnlyDictionary<string, string> Parameters,
    string Description);
