namespace AShareRadar.Contracts.Qlib;

public sealed record QlibSignalStatusDto(
    bool Enabled,
    bool FileExists,
    string SignalRoot,
    string WatchlistPath,
    DateOnly? SignalDate,
    int RecordCount,
    DateTimeOffset? LastWriteTime,
    string? Error);

public sealed record QlibSignalSnapshotDto(
    string StrategyCode,
    string StrategyName,
    string SourceExperimentId,
    DateOnly SignalDate,
    DateTimeOffset LoadedAt,
    IReadOnlyList<QlibSignalRecordDto> Records);

public sealed record QlibSignalRecordDto(
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
public sealed record QlibSignalSeedDto(
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

public sealed record QlibSignalSeedImportResultDto(
    DateTimeOffset ImportedAt,
    DateOnly SignalDate,
    string StrategyCode,
    string StrategyName,
    string SourceExperimentId,
    int ImportedCount,
    IReadOnlyList<QlibSignalSeedDto> Seeds);
