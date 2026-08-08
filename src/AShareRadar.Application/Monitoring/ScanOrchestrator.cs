using AShareRadar.Application.MarketData;
using AShareRadar.Application.Opportunities;
using AShareRadar.Application.Realtime;
using AShareRadar.Application.Review;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Opportunities;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Application.Monitoring;

public sealed class ScanOrchestrator
{
    private const int MomentumCandidateCount = 260;
    private const int ReboundCandidateCount = 740;
    private const int LongTermTrackingCandidateCount = 200;
    private const int MinDailyBarLoadCount = 80;
    private const int WeeklyBarLoadCount = 120;
    private const int MinuteKLineMaxCandidateCount = 600;
    private const int MinuteMomentumCandidateCount = 160;
    private const int MinuteGapRecoveryCandidateCount = 160;
    private const int MinuteVolumeSpikeCandidateCount = 100;
    private const int MinuteHotSectorCandidateCount = 120;
    private const int MinuteRescanCandidateCount = 80;
    private const int MinuteBarLoadCount = 360;
    private const int ThirtyMinuteBarLoadCount = 100;
    private const int MaxKLineLoadConcurrency = 8;

    private readonly object _scanGate = new();
    private Task? _runningScanTask;
    private DateTimeOffset? _lastObservationPoolScanTime;

    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IKLineDataProvider _kLineDataProvider;
    private readonly IStrategyRegistry _strategyRegistry;
    private readonly OpportunityAppService _opportunityAppService;
    private readonly MonitorRuntimeState _runtimeState;
    private readonly IRealtimeEventPublisher _realtimeEventPublisher;
    private readonly ISectorHeatService _sectorHeatService;
    private readonly MarketSentimentService _marketSentimentService;
    private readonly MarketSentimentStrategyOptions _marketSentimentStrategyOptions;
    private readonly StrategyPoolScanOptions _strategyPoolScanOptions;
    private readonly LongTermTrackingService _longTermTrackingService;
    private readonly DailyLimitUpExclusionService _limitUpExclusionService;

    public ScanOrchestrator(
        IMarketDataProvider marketDataProvider,
        IKLineDataProvider kLineDataProvider,
        IStrategyRegistry strategyRegistry,
        OpportunityAppService opportunityAppService,
        MonitorRuntimeState runtimeState,
        IRealtimeEventPublisher realtimeEventPublisher,
        ISectorHeatService sectorHeatService,
        MarketSentimentService marketSentimentService,
        MarketSentimentStrategyOptions marketSentimentStrategyOptions,
        StrategyPoolScanOptions strategyPoolScanOptions,
        LongTermTrackingService longTermTrackingService,
        DailyLimitUpExclusionService limitUpExclusionService)
    {
        _marketDataProvider = marketDataProvider;
        _kLineDataProvider = kLineDataProvider;
        _strategyRegistry = strategyRegistry;
        _opportunityAppService = opportunityAppService;
        _runtimeState = runtimeState;
        _realtimeEventPublisher = realtimeEventPublisher;
        _sectorHeatService = sectorHeatService;
        _marketSentimentService = marketSentimentService;
        _marketSentimentStrategyOptions = marketSentimentStrategyOptions;
        _strategyPoolScanOptions = strategyPoolScanOptions;
        _longTermTrackingService = longTermTrackingService;
        _limitUpExclusionService = limitUpExclusionService;
    }

    public Task RunOnceAsync(CancellationToken cancellationToken)
    {
        Task scanTask;
        lock (_scanGate)
        {
            if (_runningScanTask is { IsCompleted: false })
            {
                return _runningScanTask.WaitAsync(cancellationToken);
            }

            scanTask = RunOnceCoreAsync(cancellationToken);
            _runningScanTask = scanTask;
        }

        return AwaitAndClearRunningScanAsync(scanTask);
    }

    private async Task AwaitAndClearRunningScanAsync(Task scanTask)
    {
        try
        {
            await scanTask;
        }
        finally
        {
            lock (_scanGate)
            {
                if (ReferenceEquals(_runningScanTask, scanTask))
                {
                    _runningScanTask = null;
                }
            }
        }
    }

    private async Task RunOnceCoreAsync(CancellationToken cancellationToken)
    {
        _runtimeState.MarkScanning();
        await _realtimeEventPublisher.PublishMonitorStatusChangedAsync(_runtimeState.GetStatus(), cancellationToken);

        var runId = Guid.NewGuid();
        var snapshot = await _marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
        var tradingDate = DateOnly.FromDateTime(snapshot.SnapshotTime.LocalDateTime);
        MarkExistingHitsThatReachedLimitUp(snapshot, tradingDate);
        var strategySnapshot = FilterLimitUpExcludedQuotes(snapshot, tradingDate);
        var strategies = _strategyRegistry.GetEnabledStrategies();
        var realtimeStrategies = strategies
            .Where(IsRealtimePoolStrategy)
            .ToArray();
        var shouldRunObservationPool = ShouldRunObservationPool(snapshot.SnapshotTime);
        var observationStrategies = shouldRunObservationPool
            ? strategies.Where(strategy => !IsRealtimePoolStrategy(strategy)).ToArray()
            : Array.Empty<ISignalStrategy>();
        var activeStrategies = realtimeStrategies
            .Concat(observationStrategies)
            .ToArray();
        if (activeStrategies.Length == 0)
        {
            _runtimeState.ApplyScanResult(
                snapshot.SnapshotTime,
                activeOpportunityCount: 0,
                todayNewCount: 0,
                disappearedCount: 0,
                focusedCount: 0);
            await _realtimeEventPublisher.PublishMonitorStatusChangedAsync(_runtimeState.GetStatus(), cancellationToken);
            return;
        }

        var sectorHeatSnapshot = _sectorHeatService.Build(snapshot);
        var conceptHeatSnapshot = _sectorHeatService.BuildConcepts(snapshot);
        var marketSentimentTask = GetUsableMarketSentimentAsync(snapshot.SnapshotTime, cancellationToken);
        var dailyBarsTask = LoadDailyBarsForCandidatesAsync(strategySnapshot, activeStrategies, cancellationToken);
        var weeklyBarsTask = LoadWeeklyBarsForCandidatesAsync(strategySnapshot, activeStrategies, cancellationToken);
        var minuteBarsTask = LoadMinuteBarsForCandidatesAsync(
            strategySnapshot,
            activeStrategies,
            tradingDate,
            sectorHeatSnapshot,
            conceptHeatSnapshot,
            cancellationToken);
        var thirtyMinuteBarsTask = LoadThirtyMinuteBarsForWaveCandidatesAsync(
            strategySnapshot,
            activeStrategies,
            cancellationToken);
        await Task.WhenAll(marketSentimentTask, dailyBarsTask, weeklyBarsTask, minuteBarsTask, thirtyMinuteBarsTask);

        var marketSentiment = await marketSentimentTask;
        var dailyBarsBySymbol = await dailyBarsTask;
        var weeklyBarsBySymbol = await weeklyBarsTask;
        var minuteBarsBySymbol = await minuteBarsTask;
        var thirtyMinuteBarsBySymbol = await thirtyMinuteBarsTask;
        var context = new StrategyContext(
            runId,
            tradingDate,
            strategySnapshot,
            DailyBarsBySymbol: dailyBarsBySymbol,
            WeeklyBarsBySymbol: weeklyBarsBySymbol,
            MinuteBarsBySymbol: minuteBarsBySymbol,
            ThirtyMinuteBarsBySymbol: thirtyMinuteBarsBySymbol,
            SectorHeatSnapshot: sectorHeatSnapshot,
            ConceptHeatSnapshot: conceptHeatSnapshot,
            MarketSentiment: marketSentiment,
            MarketStats: BuildMarketStats(snapshot));

        var realtimeSignalTask = EvaluateStrategiesAsync(
            realtimeStrategies,
            context with { RunMode = StrategyRunMode.Realtime },
            "实时池",
            cancellationToken);
        var observationSignalTask = EvaluateStrategiesAsync(
            observationStrategies,
            context with { RunMode = StrategyRunMode.Observation },
            "观察池",
            cancellationToken);
        await Task.WhenAll(realtimeSignalTask, observationSignalTask);
        if (shouldRunObservationPool)
        {
            _lastObservationPoolScanTime = snapshot.SnapshotTime;
        }

        var strategySignals = MarketSentimentSignalAdjuster.Apply(
            (await realtimeSignalTask).Concat(await observationSignalTask),
            marketSentiment,
            _marketSentimentStrategyOptions);

        var signalEvents = _opportunityAppService.ApplyStrategySignals(
            runId,
            context.TradingDate,
            snapshot.SnapshotTime,
            strategySignals);
        _longTermTrackingService.TrackSignalEvents(signalEvents);

        foreach (var signalEvent in signalEvents)
        {
            await _realtimeEventPublisher.PublishSignalEventCreatedAsync(signalEvent, cancellationToken);
        }

        var opportunities = _opportunityAppService.GetTodayOpportunities();
        var visibleOpportunities = opportunities
            .Where(item => !_limitUpExclusionService.IsExcluded(tradingDate, item.Symbol))
            .ToArray();
        _runtimeState.ApplyScanResult(
            snapshot.SnapshotTime,
            activeOpportunityCount: visibleOpportunities.Count(item => item.Status is not Domain.Opportunities.OpportunityStatus.Disappeared and not Domain.Opportunities.OpportunityStatus.GivenUp),
            todayNewCount: visibleOpportunities.Length,
            disappearedCount: visibleOpportunities.Count(item => item.Status == Domain.Opportunities.OpportunityStatus.Disappeared),
            focusedCount: visibleOpportunities.Count(item =>
                item.Status == Domain.Opportunities.OpportunityStatus.Focused ||
                string.Equals(item.ManualTag, "Focus", StringComparison.OrdinalIgnoreCase)));

        await _realtimeEventPublisher.PublishMonitorStatusChangedAsync(_runtimeState.GetStatus(), cancellationToken);
    }

    private async Task<IReadOnlyList<StrategySignal>> EvaluateStrategiesAsync(
        IReadOnlyList<ISignalStrategy> strategies,
        StrategyContext context,
        string poolTag,
        CancellationToken cancellationToken)
    {
        if (strategies.Count == 0)
        {
            return [];
        }

        var strategyTasks = strategies.Select(strategy =>
            strategy.EvaluateAsync(
                context,
                cancellationToken));
        var signalGroups = await Task.WhenAll(strategyTasks);
        return signalGroups
            .SelectMany(item => item)
            .Select(signal => AddPoolTag(signal, poolTag))
            .ToArray();
    }

    private bool IsRealtimePoolStrategy(ISignalStrategy strategy)
    {
        if (!_strategyPoolScanOptions.Enabled)
        {
            return true;
        }

        var realtimeCodes = _strategyPoolScanOptions.RealtimeStrategyCodes is { Length: > 0 }
            ? _strategyPoolScanOptions.RealtimeStrategyCodes
            : ["main-sector-resonance", "main-sector-gap-recovery"];
        return realtimeCodes.Any(code => string.Equals(code, strategy.Code, StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldRunObservationPool(DateTimeOffset snapshotTime)
    {
        if (!_strategyPoolScanOptions.Enabled)
        {
            return false;
        }

        if (_lastObservationPoolScanTime is null)
        {
            if (_strategyPoolScanOptions.RunObservationOnStartup)
            {
                return true;
            }

            _lastObservationPoolScanTime = snapshotTime;
            return false;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_strategyPoolScanOptions.ObservationIntervalSeconds, 60, 1800));
        return snapshotTime - _lastObservationPoolScanTime >= interval;
    }

    private static StrategySignal AddPoolTag(StrategySignal signal, string poolTag)
    {
        var tags = (signal.Tags ?? [])
            .Concat([poolTag])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return signal with { Tags = tags };
    }

    private void MarkExistingHitsThatReachedLimitUp(MarketSnapshot snapshot, DateOnly tradingDate)
    {
        var existingHitSymbols = _opportunityAppService.GetTodayOpportunities()
            .Where(item => item.TradingDate == tradingDate)
            .Select(item => StockSymbolNormalizer.NormalizeCode(item.Symbol))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (existingHitSymbols.Count == 0)
        {
            return;
        }

        var limitUpSymbols = snapshot.Quotes
            .Where(item => existingHitSymbols.Contains(StockSymbolNormalizer.NormalizeCode(item.Symbol)))
            .Where(item => LimitStatusCalculator.Calculate(item) == LimitStatus.LimitUp)
            .Select(item => item.Symbol)
            .ToArray();
        if (limitUpSymbols.Length > 0)
        {
            _limitUpExclusionService.MarkLimitUp(tradingDate, limitUpSymbols);
        }
    }

    private MarketSnapshot FilterLimitUpExcludedQuotes(MarketSnapshot snapshot, DateOnly tradingDate)
    {
        var excludedSymbols = _limitUpExclusionService.GetExcludedSymbols(tradingDate);
        if (excludedSymbols.Count == 0)
        {
            return snapshot;
        }

        var quotes = snapshot.Quotes
            .Where(item => !excludedSymbols.Contains(StockSymbolNormalizer.NormalizeCode(item.Symbol)))
            .ToArray();
        return snapshot with { Quotes = quotes };
    }

    private async Task<MarketSentimentSnapshot?> GetUsableMarketSentimentAsync(
        DateTimeOffset snapshotTime,
        CancellationToken cancellationToken)
    {
        var latest = _marketSentimentService.GetLatestPersistedSnapshot();
        var maxAge = TimeSpan.FromMinutes(Math.Clamp(_marketSentimentStrategyOptions.MaxSnapshotAgeMinutes, 1, 60));
        if (latest is not null && snapshotTime - latest.SnapshotTime <= maxAge)
        {
            return latest;
        }

        try
        {
            return await _marketSentimentService.GetSnapshotAsync(cancellationToken);
        }
        catch
        {
            return latest;
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadDailyBarsForCandidatesAsync(
        Domain.MarketData.MarketSnapshot snapshot,
        IReadOnlyList<ISignalStrategy> strategies,
        CancellationToken cancellationToken)
    {
        var requiresDailyBars = strategies
            .Any(item => item.Definition.DataRequirement.RequiresDailyKLine);
        if (!requiresDailyBars)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        var candidateSymbols = BuildDailyKLineCandidateSymbols(snapshot);
        var dailyBarCount = ResolveDailyBarLoadCount(strategies);

        if (candidateSymbols.Length == 0)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        if (_kLineDataProvider is IBatchKLineDataProvider batchProvider)
        {
            return await batchProvider.LoadKLinesAsync(candidateSymbols, "day", dailyBarCount, cancellationToken);
        }

        var results = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        using var throttler = new SemaphoreSlim(MaxKLineLoadConcurrency);
        var tasks = candidateSymbols.Select(async symbol =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "day", dailyBarCount, cancellationToken);
                if (bars.Count > 0)
                {
                    lock (results)
                    {
                        results[symbol] = bars;
                    }
                }
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadMinuteBarsForCandidatesAsync(
        Domain.MarketData.MarketSnapshot snapshot,
        IReadOnlyList<ISignalStrategy> strategies,
        DateOnly tradingDate,
        SectorHeatSnapshot? sectorHeatSnapshot,
        ConceptHeatSnapshot? conceptHeatSnapshot,
        CancellationToken cancellationToken)
    {
        var requiresMinuteBars = strategies
            .Any(item => item.Definition.DataRequirement.RequiresMinuteKLine);
        if (!requiresMinuteBars)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        var candidateSymbols = BuildMinuteKLineCandidateSymbols(
            snapshot,
            tradingDate,
            sectorHeatSnapshot,
            conceptHeatSnapshot);
        if (candidateSymbols.Length == 0)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        if (_kLineDataProvider is IBatchKLineDataProvider batchProvider)
        {
            return await batchProvider.LoadKLinesAsync(candidateSymbols, "1m", MinuteBarLoadCount, cancellationToken);
        }

        var results = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        using var throttler = new SemaphoreSlim(MaxKLineLoadConcurrency);
        var tasks = candidateSymbols.Select(async symbol =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "1m", MinuteBarLoadCount, cancellationToken);
                if (bars.Count > 0)
                {
                    lock (results)
                    {
                        results[symbol] = bars;
                    }
                }
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadThirtyMinuteBarsForWaveCandidatesAsync(
        Domain.MarketData.MarketSnapshot snapshot,
        IReadOnlyList<ISignalStrategy> strategies,
        CancellationToken cancellationToken)
    {
        var requiresThirtyMinuteBars = strategies
            .Any(item => string.Equals(item.Code, "long-support-rebound", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Code, "platform-volume-breakout", StringComparison.OrdinalIgnoreCase));
        if (!requiresThirtyMinuteBars)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        var candidateSymbols = BuildDailyKLineCandidateSymbols(snapshot);
        if (candidateSymbols.Length == 0)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        if (_kLineDataProvider is IBatchKLineDataProvider batchProvider)
        {
            return await batchProvider.LoadKLinesAsync(candidateSymbols, "m30", ThirtyMinuteBarLoadCount, cancellationToken);
        }

        var results = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        using var throttler = new SemaphoreSlim(MaxKLineLoadConcurrency);
        var tasks = candidateSymbols.Select(async symbol =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "m30", ThirtyMinuteBarLoadCount, cancellationToken);
                if (bars.Count > 0)
                {
                    lock (results)
                    {
                        results[symbol] = bars;
                    }
                }
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private string[] BuildDailyKLineCandidateSymbols(Domain.MarketData.MarketSnapshot snapshot)
    {
        var liquidQuotes = snapshot.Quotes
            .Where(item => item.Price > 0 && item.Amount >= 30_000_000m)
            .ToArray();
        var quoteSymbols = snapshot.Quotes
            .Select(item => StockSymbolNormalizer.NormalizeCode(item.Symbol))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackingSymbols = _longTermTrackingService.GetActiveTrackingSymbols(LongTermTrackingCandidateCount)
            .Where(item => quoteSymbols.Contains(StockSymbolNormalizer.NormalizeCode(item)))
            .ToArray();

        var momentumSymbols = liquidQuotes
            .Where(item => item.Price > 0 && item.Amount >= 50_000_000m)
            .OrderByDescending(item =>
                Math.Max(item.ChangePercent, 0m) * 10m
                + Math.Max(item.VolumeRatio, 0m) * 5m
                + Math.Min(item.Amount / 100_000_000m, 20m))
            .Take(MomentumCandidateCount)
            .Select(item => item.Symbol);

        var reboundSymbols = liquidQuotes
            .Where(item => item.ChangePercent <= 5.5m)
            .OrderByDescending(item =>
                Math.Min(item.Amount / 100_000_000m, 20m) * 3m
                + Math.Max(8m - Math.Abs(item.ChangePercent), 0m) * 3m
                + Math.Max(-item.ChangePercent, 0m) * 2m
                + Math.Min(Math.Max(item.VolumeRatio, 0m), 3m))
            .Take(ReboundCandidateCount)
            .Select(item => item.Symbol);

        return trackingSymbols
            .Concat(momentumSymbols)
            .Concat(reboundSymbols)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string[] BuildMinuteKLineCandidateSymbols(
        Domain.MarketData.MarketSnapshot snapshot,
        DateOnly tradingDate,
        SectorHeatSnapshot? sectorHeatSnapshot,
        ConceptHeatSnapshot? conceptHeatSnapshot)
    {
        var quotes = snapshot.Quotes
            .Where(item => item.Price > 0)
            .ToArray();

        var momentumPool = quotes
            .Where(item => item.Amount >= 30_000_000m && item.ChangePercent >= 0m && item.ChangePercent <= 7.5m)
            .OrderByDescending(item =>
                Math.Max(item.ChangePercent, 0m) * 9m
                + Math.Min(Math.Max(item.VolumeRatio, 0m), 5m) * 7m
                + Math.Min(item.Amount / 100_000_000m, 30m))
            .Take(MinuteMomentumCandidateCount)
            .Select(item => item.Symbol)
            .ToArray();

        var gapRecoveryPool = quotes
            .Where(item => item.Amount >= 30_000_000m
                && item.Open > 0m
                && item.Price > item.Open
                && item.ChangePercent >= -4m
                && item.ChangePercent <= 2m)
            .Select(item => new
            {
                Quote = item,
                PreviousClose = EstimatePreviousClose(item.Price, item.ChangePercent),
                ReturnFromOpen = Percent(item.Price, item.Open)
            })
            .Where(item => item.PreviousClose > 0m && item.Quote.Open < item.PreviousClose)
            .OrderByDescending(item =>
                item.ReturnFromOpen * 12m
                + Math.Min(Math.Max(item.Quote.VolumeRatio, 0m), 5m) * 8m
                + Math.Min(item.Quote.Amount / 100_000_000m, 30m) * 2m
                + Math.Max(-Percent(item.Quote.Open, item.PreviousClose), 0m) * 3m)
            .Take(MinuteGapRecoveryCandidateCount)
            .Select(item => item.Quote.Symbol)
            .ToArray();

        var volumeSpikePool = quotes
            .Where(item => item.Amount >= 30_000_000m
                && item.ChangePercent >= -3m
                && item.ChangePercent <= 7.5m
                && item.VolumeRatio >= 1.2m)
            .OrderByDescending(item =>
                Math.Min(Math.Max(item.VolumeRatio, 0m), 8m) * 10m
                + Math.Min(item.Amount / 100_000_000m, 30m) * 2m
                + Math.Min(Math.Abs(item.ChangePercent), 8m))
            .Take(MinuteVolumeSpikeCandidateCount)
            .Select(item => item.Symbol)
            .ToArray();

        var hotSectorPool = quotes
            .Where(item => item.Amount >= 20_000_000m && item.ChangePercent >= -4m && item.ChangePercent <= 7.5m)
            .Select(item => new
            {
                Quote = item,
                HeatScore = GetEffectiveHeatScore(item.Symbol, sectorHeatSnapshot, conceptHeatSnapshot)
            })
            .Where(item => item.HeatScore >= 55m)
            .OrderByDescending(item =>
                item.HeatScore
                + Math.Min(Math.Max(item.Quote.VolumeRatio, 0m), 5m) * 6m
                + Math.Min(item.Quote.Amount / 100_000_000m, 30m)
                + Math.Max(item.Quote.ChangePercent, 0m) * 2m)
            .Take(MinuteHotSectorCandidateCount)
            .Select(item => item.Quote.Symbol)
            .ToArray();

        var quoteSymbols = quotes
            .Select(item => StockSymbolNormalizer.NormalizeCode(item.Symbol))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rescanPool = _opportunityAppService.GetTodayOpportunities()
            .Where(item => item.TradingDate == tradingDate)
            .Where(item => item.Status is not OpportunityStatus.Disappeared and not OpportunityStatus.GivenUp)
            .Where(item => quoteSymbols.Contains(StockSymbolNormalizer.NormalizeCode(item.Symbol)))
            .OrderByDescending(item => item.Status == OpportunityStatus.Focused || string.Equals(item.ManualTag, "Focus", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.LastSeenTime)
            .ThenByDescending(item => item.CurrentScore)
            .Take(MinuteRescanCandidateCount)
            .Select(item => item.Symbol)
            .ToArray();

        return MergeCandidatePools(
            MinuteKLineMaxCandidateCount,
            momentumPool,
            gapRecoveryPool,
            volumeSpikePool,
            hotSectorPool,
            rescanPool);
    }

    private static string[] MergeCandidatePools(int maxCount, params IReadOnlyList<string>[] pools)
    {
        var results = new List<string>(Math.Clamp(maxCount, 1, 2000));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxPoolLength = pools.Length == 0 ? 0 : pools.Max(item => item.Count);

        for (var index = 0; index < maxPoolLength && results.Count < maxCount; index++)
        {
            foreach (var pool in pools)
            {
                if (index >= pool.Count)
                {
                    continue;
                }

                var normalized = StockSymbolNormalizer.NormalizeCode(pool[index]);
                if (normalized.Length == 0 || !seen.Add(normalized))
                {
                    continue;
                }

                results.Add(normalized);
                if (results.Count >= maxCount)
                {
                    break;
                }
            }
        }

        return results.ToArray();
    }

    private static decimal GetEffectiveHeatScore(
        string symbol,
        SectorHeatSnapshot? sectorHeatSnapshot,
        ConceptHeatSnapshot? conceptHeatSnapshot)
    {
        var normalized = StockSymbolNormalizer.NormalizeCode(symbol);
        var sectorScore = TryGetSectorHeatScore(symbol, normalized, sectorHeatSnapshot);
        var conceptScore = TryGetConceptHeatScore(symbol, normalized, conceptHeatSnapshot);
        return Math.Max(sectorScore, conceptScore);
    }

    private static decimal TryGetSectorHeatScore(
        string symbol,
        string normalized,
        SectorHeatSnapshot? sectorHeatSnapshot)
    {
        if (sectorHeatSnapshot?.HeatBySymbol.TryGetValue(symbol, out var heat) == true)
        {
            return heat.HeatScore;
        }

        return sectorHeatSnapshot?.HeatBySymbol.TryGetValue(normalized, out heat) == true
            ? heat.HeatScore
            : 0m;
    }

    private static decimal TryGetConceptHeatScore(
        string symbol,
        string normalized,
        ConceptHeatSnapshot? conceptHeatSnapshot)
    {
        if (conceptHeatSnapshot?.HeatBySymbol.TryGetValue(symbol, out var heats) == true)
        {
            return heats.Count == 0 ? 0m : heats.Max(item => item.HeatScore);
        }

        return conceptHeatSnapshot?.HeatBySymbol.TryGetValue(normalized, out heats) == true && heats.Count > 0
            ? heats.Max(item => item.HeatScore)
            : 0m;
    }

    private static decimal EstimatePreviousClose(decimal currentPrice, decimal changePercent)
    {
        var denominator = 1m + changePercent / 100m;
        return currentPrice > 0m && denominator > 0m ? currentPrice / denominator : 0m;
    }

    private static decimal Percent(decimal value, decimal baseline)
    {
        return baseline == 0m ? 0m : (value - baseline) * 100m / baseline;
    }

    private static int ResolveDailyBarLoadCount(IReadOnlyList<ISignalStrategy> strategies)
    {
        var requiredCount = strategies
            .Where(item => item.Definition.DataRequirement.RequiresDailyKLine)
            .Select(item => item.Definition.DataRequirement.MinDailyBarCount)
            .DefaultIfEmpty(MinDailyBarLoadCount)
            .Max();

        return Math.Max(MinDailyBarLoadCount, requiredCount);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadWeeklyBarsForCandidatesAsync(
        Domain.MarketData.MarketSnapshot snapshot,
        IReadOnlyList<ISignalStrategy> strategies,
        CancellationToken cancellationToken)
    {
        var requiresWeeklyBars = strategies
            .Any(item => string.Equals(item.Code, "platform-volume-breakout", StringComparison.OrdinalIgnoreCase));
        if (!requiresWeeklyBars)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        var candidateSymbols = BuildDailyKLineCandidateSymbols(snapshot);
        if (candidateSymbols.Length == 0)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        if (_kLineDataProvider is IBatchKLineDataProvider batchProvider)
        {
            return await batchProvider.LoadKLinesAsync(candidateSymbols, "week", WeeklyBarLoadCount, cancellationToken);
        }

        var results = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        using var throttler = new SemaphoreSlim(MaxKLineLoadConcurrency);
        var tasks = candidateSymbols.Select(async symbol =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "week", WeeklyBarLoadCount, cancellationToken);
                if (bars.Count > 0)
                {
                    lock (results)
                    {
                        results[symbol] = bars;
                    }
                }
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private static StrategyMarketStats BuildMarketStats(Domain.MarketData.MarketSnapshot snapshot)
    {
        var validQuotes = snapshot.Quotes
            .Where(item => item.Price > 0 && Math.Abs(item.ChangePercent) <= 30m)
            .ToArray();
        if (validQuotes.Length == 0)
        {
            return new StrategyMarketStats(0m, 0m, 0m, 0m);
        }

        var risingCount = validQuotes.Count(item => item.ChangePercent > 0);
        var fallingCount = validQuotes.Count(item => item.ChangePercent < 0);
        return new StrategyMarketStats(
            Math.Clamp(validQuotes.Average(item => item.ChangePercent), -10m, 10m),
            risingCount * 100m / validQuotes.Length,
            fallingCount * 100m / validQuotes.Length,
            validQuotes.Sum(item => item.Amount));
    }
}
