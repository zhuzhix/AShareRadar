namespace AShareRadar.Application.Qlib;

public sealed record QlibSignalSeed(
    Guid Id,
    DateOnly SignalDate,
    string Code,
    string Symbol,
    string Exchange,
    string Name,
    decimal PredScore,
    int RankTotal,
    int ModelRank,
    decimal ModelScore100,
    decimal TargetWeight,
    string Action,
    string Confidence,
    string StrategyCode,
    string StrategyName,
    string SourceExperimentId,
    string Reason,
    string? Risk,
    DateTimeOffset ImportedAt);

public sealed record QlibSignalSeedImportResult(
    DateTimeOffset ImportedAt,
    DateOnly SignalDate,
    string StrategyCode,
    string StrategyName,
    string SourceExperimentId,
    int ImportedCount,
    IReadOnlyList<QlibSignalSeed> Seeds);