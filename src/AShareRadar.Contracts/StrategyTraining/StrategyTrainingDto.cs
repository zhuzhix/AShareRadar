namespace AShareRadar.Contracts.StrategyTraining;

public sealed record StrategyTrainingDatasetRequest(
    DateOnly StartDate,
    DateOnly EndDate,
    string? StrategyCode,
    decimal SuccessHighReturnThreshold = 2m,
    bool RequirePositiveClose = true,
    bool ForceRebuild = false,
    decimal[]? ScoreThresholds = null,
    decimal[]? AmountThresholds = null,
    decimal[]? RelativeStrengthThresholds = null,
    decimal[]? HeatThresholds = null,
    int[]? OutputLimits = null);

public sealed record StrategyTrainingRunRequest(
    DateOnly StartDate,
    DateOnly EndDate,
    string? StrategyCode,
    decimal SuccessHighReturnThreshold = 2m,
    bool RequirePositiveClose = true,
    bool ForceRebuild = false,
    decimal[]? ScoreThresholds = null,
    decimal[]? AmountThresholds = null,
    decimal[]? RelativeStrengthThresholds = null,
    decimal[]? HeatThresholds = null,
    int[]? OutputLimits = null);

public sealed record StrategyTrainingDatasetDto(
    DateOnly StartDate,
    DateOnly EndDate,
    string? StrategyCode,
    int SourceSignalCount,
    int SampleCount,
    int SuccessCount,
    decimal? SuccessRate,
    string Message,
    IReadOnlyList<StrategyTrainingSampleDto> Samples);

public sealed record StrategyTrainingSampleDto(
    Guid Id,
    DateOnly SignalDate,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    decimal? AmountYi,
    decimal? ChangePercent,
    decimal? VolumeRatio,
    decimal? RelativeStrengthPercent,
    decimal? SectorHeatScore,
    decimal? ConceptHeatScore,
    decimal? SentimentTemperature,
    decimal? NextOpenReturn,
    decimal? NextHighReturn,
    decimal? NextCloseReturn,
    bool IsSuccess,
    string Reason,
    IReadOnlyDictionary<string, decimal>? Metrics = null);

public sealed record StrategyTrainingRunDto(
    Guid RunId,
    DateOnly StartDate,
    DateOnly EndDate,
    string? StrategyCode,
    int SourceSignalCount,
    int SampleCount,
    int ResultCount,
    DateTimeOffset CreatedAt,
    string Message,
    IReadOnlyList<StrategyTrainingResultDto> Results);

public sealed record StrategyTrainingResultDto(
    int Rank,
    decimal MinScore,
    decimal MinAmountYi,
    decimal MinRelativeStrengthPercent,
    decimal MinHeatScore,
    int MaxOutputPerDay,
    int HitCount,
    int SuccessCount,
    decimal? SuccessRate,
    decimal? AverageNextOpenReturn,
    decimal? AverageNextHighReturn,
    decimal? AverageNextCloseReturn,
    decimal? WorstNextCloseReturn,
    string Summary);

public sealed record StrategyParameterProfileDto(
    Guid Id,
    string StrategyCode,
    string ProfileName,
    Guid? SourceTrainingRunId,
    IReadOnlyDictionary<string, string> Parameters,
    int SampleCount,
    decimal? SuccessRate,
    decimal? AverageNextHighReturn,
    decimal? AverageNextCloseReturn,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt);

public sealed record SaveStrategyParameterProfileRequest(
    string StrategyCode,
    string ProfileName,
    Guid? SourceTrainingRunId,
    decimal MinScore,
    decimal MinAmountYi,
    decimal MinRelativeStrengthPercent,
    decimal MinHeatScore,
    int MaxOutputPerDay,
    int SampleCount,
    decimal? SuccessRate,
    decimal? AverageNextHighReturn,
    decimal? AverageNextCloseReturn);
