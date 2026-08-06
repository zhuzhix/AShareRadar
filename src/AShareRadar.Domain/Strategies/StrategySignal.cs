namespace AShareRadar.Domain.Strategies;

public sealed record StrategySignal(
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    StrategyType StrategyType,
    decimal Score,
    decimal? Price,
    string Reason,
    string? Risk,
    StrategySignalAction Action = StrategySignalAction.Candidate,
    StrategySignalConfidence Confidence = StrategySignalConfidence.Medium,
    StrategyStage Stage = StrategyStage.TriggerConfirmation,
    IReadOnlyDictionary<string, decimal>? Metrics = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? PassedConditions = null,
    IReadOnlyList<string>? FailedConditions = null,
    decimal? StopLossPrice = null,
    decimal? TakeProfitPrice = null);
