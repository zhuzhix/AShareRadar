using AShareRadar.Application.MarketData;

namespace AShareRadar.Application.Review;

public sealed class SignalReturnStatsService
{
    private static readonly IReadOnlyList<SignalReturnHorizon> Horizons =
    [
        new("D1", "1日", 1, "short"),
        new("D3", "3日", 3, "short"),
        new("D5", "5日", 5, "short"),
        new("W1", "1周", 5, "long"),
        new("M1", "1月", 20, "long"),
        new("M3", "3月", 60, "long")
    ];

    private static readonly HashSet<string> MainlineStrategyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "main-sector-resonance",
        "main-sector-gap-recovery"
    };

    private readonly ISignalReturnStatsStore _store;
    private readonly IKLineDataProvider _kLineDataProvider;

    public SignalReturnStatsService(
        ISignalReturnStatsStore store,
        IKLineDataProvider kLineDataProvider)
    {
        _store = store;
        _kLineDataProvider = kLineDataProvider;
    }

    public IReadOnlyList<SignalReturnHorizon> GetHorizons() => Horizons;

    public async Task<SignalReturnRecalculateResult> RecalculateAsync(
        SignalReturnRecalculateRequest request,
        CancellationToken cancellationToken)
    {
        var sources = _store.QuerySignalSources(request.Query);
        var records = new List<SignalReturnRecord>();
        var processedSignals = 0;
        var skippedSignals = 0;
        var failedSignals = 0;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var signalRecords = await CalculateSignalAsync(source, cancellationToken);
                if (signalRecords.Count == 0)
                {
                    skippedSignals++;
                    continue;
                }

                records.AddRange(signalRecords);
                processedSignals++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failedSignals++;
            }
        }

        if (records.Count > 0)
        {
            _store.UpsertRecords(records);
        }

        return new SignalReturnRecalculateResult(
            DateTimeOffset.Now,
            sources.Count,
            processedSignals,
            skippedSignals,
            failedSignals,
            records.Count);
    }

    public SignalReturnQueryResult QueryRecords(SignalReturnQuery query)
    {
        return _store.QueryRecords(query);
    }

    public IReadOnlyList<SignalReturnStrategySummary> QueryStrategySummaries(SignalReturnSummaryQuery query)
    {
        return _store.QueryStrategySummaries(query);
    }

    private async Task<IReadOnlyList<SignalReturnRecord>> CalculateSignalAsync(
        SignalReturnSource source,
        CancellationToken cancellationToken)
    {
        var bars = await _kLineDataProvider.LoadKLineAsync(source.Symbol, "day", 260, cancellationToken);
        var orderedBars = bars
            .Where(item => item.Close > 0)
            .OrderBy(item => item.TradingTime)
            .ToArray();
        if (orderedBars.Length == 0)
        {
            return [];
        }

        var signalDate = DateOnly.FromDateTime(source.EventTime.LocalDateTime);
        var signalIndex = Array.FindIndex(orderedBars, item => DateOnly.FromDateTime(item.TradingTime) >= signalDate);
        if (signalIndex < 0)
        {
            return [];
        }

        var signalBar = orderedBars[signalIndex];
        var entryPrice = source.SignalPrice is > 0m ? source.SignalPrice.Value : signalBar.Close;
        if (entryPrice <= 0m)
        {
            return [];
        }

        var createdAt = DateTimeOffset.Now;
        return Horizons
            .Select(horizon => CalculateHorizon(source, horizon, orderedBars, signalIndex, signalBar, entryPrice, createdAt))
            .ToArray();
    }

    private static SignalReturnRecord CalculateHorizon(
        SignalReturnSource source,
        SignalReturnHorizon horizon,
        IReadOnlyList<KLineBar> orderedBars,
        int signalIndex,
        KLineBar signalBar,
        decimal entryPrice,
        DateTimeOffset createdAt)
    {
        var targetIndex = signalIndex + horizon.TradingDays;
        KLineBar? targetBar = targetIndex < orderedBars.Count ? orderedBars[targetIndex] : null;
        var completed = targetBar is not null;
        var windowEndIndex = completed ? targetIndex : orderedBars.Count - 1;
        var windowBars = windowEndIndex > signalIndex
            ? orderedBars.Skip(signalIndex + 1).Take(windowEndIndex - signalIndex).ToArray()
            : [];

        var targetClose = targetBar?.Close;
        var returnPercent = targetClose.HasValue
            ? CalculateReturn(entryPrice, targetClose.Value)
            : (decimal?)null;
        var maxReturnPercent = windowBars.Length > 0
            ? CalculateReturn(entryPrice, windowBars.Max(item => item.High))
            : (decimal?)null;
        var minReturnPercent = windowBars.Length > 0
            ? CalculateReturn(entryPrice, windowBars.Min(item => item.Low))
            : (decimal?)null;

        return new SignalReturnRecord(
            source.EventId,
            source.OpportunityId,
            source.EventTime,
            DateOnly.FromDateTime(signalBar.TradingTime),
            source.Symbol,
            source.Name,
            source.StrategyCode,
            source.StrategyName,
            ResolveStrategyGroup(source.StrategyCode),
            source.StrategyVersionId,
            source.StrategyVersion,
            source.Score,
            source.SignalPrice,
            entryPrice,
            horizon.Code,
            horizon.Name,
            horizon.TradingDays,
            horizon.Group,
            targetBar is null ? null : DateOnly.FromDateTime(targetBar.TradingTime),
            targetClose,
            returnPercent,
            maxReturnPercent,
            minReturnPercent,
            completed ? "Completed" : "Pending",
            createdAt,
            createdAt);
    }

    private static decimal CalculateReturn(decimal entryPrice, decimal price)
    {
        return Math.Round((price - entryPrice) / entryPrice * 100m, 4);
    }

    public static string ResolveStrategyGroup(string strategyCode)
    {
        return MainlineStrategyCodes.Contains(strategyCode) ? "mainline" : "observation";
    }
}

public interface ISignalReturnStatsStore
{
    IReadOnlyList<SignalReturnSource> QuerySignalSources(SignalReturnQuery query);

    void UpsertRecords(IReadOnlyList<SignalReturnRecord> records);

    SignalReturnQueryResult QueryRecords(SignalReturnQuery query);

    IReadOnlyList<SignalReturnStrategySummary> QueryStrategySummaries(SignalReturnSummaryQuery query);
}

public sealed record SignalReturnHorizon(
    string Code,
    string Name,
    int TradingDays,
    string Group);

public sealed record SignalReturnRecalculateRequest(SignalReturnQuery Query);

public sealed record SignalReturnRecalculateResult(
    DateTimeOffset CalculatedAt,
    int SourceSignalCount,
    int ProcessedSignalCount,
    int SkippedSignalCount,
    int FailedSignalCount,
    int RecordCount);

public sealed record SignalReturnQuery(
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

public sealed record SignalReturnSummaryQuery(
    DateOnly? FromDate,
    DateOnly? ToDate,
    string? StrategyCode,
    string? StrategyGroup,
    string? StrategyVersion,
    string? HorizonGroup,
    string? HorizonCode,
    int Count);

public sealed record SignalReturnSource(
    Guid EventId,
    Guid OpportunityId,
    DateTimeOffset EventTime,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? SignalPrice,
    string? StrategyVersionId,
    string? StrategyVersion);

public sealed record SignalReturnRecord(
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

public sealed record SignalReturnQueryResult(
    int TotalCount,
    IReadOnlyList<SignalReturnRecord> Items);

public sealed record SignalReturnStrategySummary(
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
