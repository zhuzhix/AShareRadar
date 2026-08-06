namespace AShareRadar.Application.Backtesting;

public sealed record BacktestReplayResult(
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> StrategyCodes,
    string StockPool,
    string DataSourceStatus,
    string Message,
    long ElapsedMilliseconds,
    IReadOnlyList<BacktestStrategySummaryItem> StrategySummaries,
    IReadOnlyList<BacktestSignalItem> Signals,
    IReadOnlyList<BacktestSentimentSummaryItem> SentimentSummaries);

public sealed record BacktestStrategySummaryItem(
    string StrategyCode,
    string StrategyName,
    int SignalCount,
    decimal AverageScore,
    decimal? WinRate1Day,
    decimal? WinRate3Day,
    decimal? WinRate5Day,
    decimal? AverageReturn1Day,
    decimal? AverageReturn3Day,
    decimal? AverageReturn5Day,
    decimal? BestReturn5Day,
    decimal? WorstReturn5Day);

public sealed record BacktestSentimentSummaryItem(
    string SentimentLevel,
    int SignalCount,
    decimal AverageScore,
    decimal? WinRate1Day,
    decimal? WinRate3Day,
    decimal? WinRate5Day,
    decimal? AverageReturn1Day,
    decimal? AverageReturn3Day,
    decimal? AverageReturn5Day);

public sealed record BacktestSignalItem(
    DateOnly TradingDate,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    string Action,
    string Confidence,
    decimal Score,
    decimal? Price,
    string Reason,
    string? Risk,
    decimal? Return1Day,
    decimal? Return3Day,
    decimal? Return5Day,
    IReadOnlyDictionary<string, decimal>? Metrics,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<string>? PassedConditions,
    IReadOnlyList<string>? FailedConditions,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice);
