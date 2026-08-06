namespace AShareRadar.Application.Qlib;

public sealed record QlibSignalStatus(
    bool Enabled,
    bool FileExists,
    string SignalRoot,
    string WatchlistPath,
    DateOnly? SignalDate,
    int RecordCount,
    DateTimeOffset? LastWriteTime,
    string? Error);

public sealed record QlibSignalSnapshot(
    string StrategyCode,
    string StrategyName,
    string SourceExperimentId,
    DateOnly SignalDate,
    DateTimeOffset LoadedAt,
    IReadOnlyList<QlibSignalRecord> Records);

public sealed record QlibSignalRecord(
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
    string? Risk);
