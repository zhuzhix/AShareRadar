namespace AShareRadar.Contracts.Review;

public sealed record SignalReturnHorizonDto(
    string Code,
    string Name,
    int TradingDays,
    string Group);

public sealed record SignalReturnRecalculateRequestDto(
    DateOnly? FromDate,
    DateOnly? ToDate,
    string? Symbol,
    string? StrategyCode,
    string? StrategyGroup,
    string? StrategyVersion,
    string? HorizonGroup,
    string? HorizonCode,
    string? Status,
    int Count);

public sealed record SignalReturnRecalculateResultDto(
    DateTimeOffset CalculatedAt,
    int SourceSignalCount,
    int ProcessedSignalCount,
    int SkippedSignalCount,
    int FailedSignalCount,
    int RecordCount);

public sealed record SignalReturnQueryResultDto(
    int TotalCount,
    IReadOnlyList<SignalReturnRecordDto> Items);

public sealed record SignalReturnRecordDto(
    Guid EventId,
    Guid OpportunityId,
    DateTimeOffset EventTime,
    DateOnly SignalDate,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    string StrategyGroup,
    string? StrategyVersionId,
    string? StrategyVersion,
    decimal Score,
    decimal? SignalPrice,
    decimal EntryPrice,
    string HorizonCode,
    string HorizonName,
    int TradingDays,
    string HorizonGroup,
    DateOnly? TargetDate,
    decimal? TargetClose,
    decimal? ReturnPercent,
    decimal? MaxReturnPercent,
    decimal? MinReturnPercent,
    string Status,
    DateTimeOffset CalculatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SignalReturnStrategySummaryDto(
    string StrategyCode,
    string StrategyName,
    string StrategyGroup,
    string? StrategyVersion,
    string HorizonCode,
    string HorizonName,
    string HorizonGroup,
    int SignalCount,
    int CompletedCount,
    int PendingCount,
    int WinCount,
    decimal? WinRatePercent,
    decimal? AverageReturnPercent,
    decimal? AverageMaxReturnPercent,
    decimal? AverageMinReturnPercent,
    decimal? BestReturnPercent,
    decimal? WorstReturnPercent,
    DateTimeOffset? LastSignalTime);
