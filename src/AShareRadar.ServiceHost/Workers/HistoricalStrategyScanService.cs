using AShareRadar.Application.MarketData;
using AShareRadar.Application.Monitoring;
using AShareRadar.Application.Opportunities;
using AShareRadar.Application.Realtime;
using AShareRadar.Application.Review;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.ServiceHost.Workers;

public sealed class HistoricalStrategyScanService
{
    private readonly HistoricalStrategyScanOptions _options;
    private readonly IHistoricalSymbolProvider _symbolProvider;
    private readonly IKLineDataProvider _kLineDataProvider;
    private readonly IStrategyRegistry _strategyRegistry;
    private readonly OpportunityAppService _opportunityAppService;
    private readonly MonitorRuntimeState _runtimeState;
    private readonly IRealtimeEventPublisher _realtimeEventPublisher;
    private readonly ILogger<HistoricalStrategyScanService> _logger;
    private readonly LongTermTrackingService _longTermTrackingService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HistoricalStrategyScanService(
        HistoricalStrategyScanOptions options,
        IHistoricalSymbolProvider symbolProvider,
        IKLineDataProvider kLineDataProvider,
        IStrategyRegistry strategyRegistry,
        OpportunityAppService opportunityAppService,
        MonitorRuntimeState runtimeState,
        IRealtimeEventPublisher realtimeEventPublisher,
        ILogger<HistoricalStrategyScanService> logger,
        LongTermTrackingService longTermTrackingService)
    {
        _options = options;
        _symbolProvider = symbolProvider;
        _kLineDataProvider = kLineDataProvider;
        _strategyRegistry = strategyRegistry;
        _opportunityAppService = opportunityAppService;
        _runtimeState = runtimeState;
        _realtimeEventPublisher = realtimeEventPublisher;
        _logger = logger;
        _longTermTrackingService = longTermTrackingService;
    }

    public async Task<bool> TryRunScheduledAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !await _gate.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            await RunCoreAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        _runtimeState.MarkHistoricalStrategyScanning();
        await _realtimeEventPublisher.PublishMonitorStatusChangedAsync(_runtimeState.GetStatus(), cancellationToken);

        var startedAt = DateTimeOffset.Now;
        var nextScanTime = startedAt.AddMinutes(Math.Clamp(_options.RepeatIntervalMinutes, 30, 1440));
        try
        {
            var maxSymbols = Math.Clamp(_options.MaxSymbols, 1, 6000);
            var dailyBarCount = Math.Clamp(_options.DailyBarCount, 60, 250);
            var symbols = await _symbolProvider.LoadSymbolsAsync(
                string.IsNullOrWhiteSpace(_options.StockPool) ? "AShare" : _options.StockPool,
                maxSymbols,
                cancellationToken);

            var dailyBarsBySymbol = await LoadDailyBarsAsync(symbols, dailyBarCount, cancellationToken);
            var quotes = dailyBarsBySymbol
                .Select(item => BuildQuote(item.Key, item.Value))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();

            if (quotes.Length == 0)
            {
                _runtimeState.ApplyHistoricalStrategyScanResult(startedAt, nextScanTime, 0, 0);
                await _realtimeEventPublisher.PublishMonitorStatusChangedAsync(_runtimeState.GetStatus(), cancellationToken);
                _logger.LogWarning("Historical strategy scan skipped because no daily bars were loaded.");
                return;
            }

            var tradingDate = DateOnly.FromDateTime(
                dailyBarsBySymbol.Values
                    .SelectMany(item => item)
                    .Max(item => item.TradingTime));
            var snapshot = new MarketSnapshot(
                startedAt,
                "HistoricalDailyKLine",
                quotes);
            var strategies = _strategyRegistry.GetEnabledStrategies()
                .Where(IsHistoricalDailyStrategy)
                .ToArray();
            var weeklyBarsBySymbol = strategies.Any(item => string.Equals(item.Code, "platform-volume-breakout", StringComparison.OrdinalIgnoreCase))
                ? await LoadWeeklyBarsAsync(symbols, cancellationToken)
                : new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
            var context = new StrategyContext(
                Guid.NewGuid(),
                tradingDate,
                snapshot,
                Domain.Strategies.StrategyRunMode.Backtest,
                DailyBarsBySymbol: dailyBarsBySymbol,
                WeeklyBarsBySymbol: weeklyBarsBySymbol);

            var strategyTasks = strategies.Select(strategy =>
                strategy.EvaluateAsync(
                    context,
                    cancellationToken));
            var signalGroups = await Task.WhenAll(strategyTasks);
            var signals = signalGroups.SelectMany(item => item).ToArray();
            var signalEvents = _opportunityAppService.ApplyStrategySignals(
                context.RunId,
                context.TradingDate,
                startedAt,
                signals);
            _longTermTrackingService.TrackSignalEvents(signalEvents);

            foreach (var signalEvent in signalEvents)
            {
                await _realtimeEventPublisher.PublishSignalEventCreatedAsync(signalEvent, cancellationToken);
            }

            _runtimeState.ApplyHistoricalStrategyScanResult(
                startedAt,
                nextScanTime,
                dailyBarsBySymbol.Count,
                signals.Length);
            await _realtimeEventPublisher.PublishMonitorStatusChangedAsync(_runtimeState.GetStatus(), cancellationToken);
            _logger.LogInformation(
                "Historical strategy scan completed. Symbols={SymbolCount} Strategies={StrategyCount} Signals={SignalCount}",
                dailyBarsBySymbol.Count,
                strategies.Length,
                signals.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runtimeState.MarkHistoricalStrategyScanFailed(nextScanTime);
            await _realtimeEventPublisher.PublishMonitorStatusChangedAsync(_runtimeState.GetStatus(), cancellationToken);
            _logger.LogError(ex, "Historical strategy scan failed.");
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadDailyBarsAsync(
        IReadOnlyList<string> symbols,
        int dailyBarCount,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        using var throttler = new SemaphoreSlim(Math.Clamp(_options.LoadConcurrency, 1, 16));
        var tasks = symbols.Select(async symbol =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "day", dailyBarCount, cancellationToken);
                if (bars.Count >= 60)
                {
                    lock (results)
                    {
                        results[StockSymbolNormalizer.NormalizeCode(symbol)] = bars
                            .OrderBy(item => item.TradingTime)
                            .ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load historical daily bars for {Symbol}.", symbol);
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadWeeklyBarsAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        using var throttler = new SemaphoreSlim(Math.Clamp(_options.LoadConcurrency, 1, 16));
        var tasks = symbols.Select(async symbol =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "week", 120, cancellationToken);
                if (bars.Count >= 24)
                {
                    lock (results)
                    {
                        results[StockSymbolNormalizer.NormalizeCode(symbol)] = bars
                            .OrderBy(item => item.TradingTime)
                            .ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load historical weekly bars for {Symbol}.", symbol);
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private static StockQuote? BuildQuote(string symbol, IReadOnlyList<KLineBar> bars)
    {
        var ordered = bars.OrderBy(item => item.TradingTime).ToArray();
        if (ordered.Length < 2)
        {
            return null;
        }

        var latest = ordered[^1];
        var previous = ordered[^2];
        if (latest.Close <= 0 || previous.Close <= 0)
        {
            return null;
        }

        var averageVolume = ordered
            .Take(ordered.Length - 1)
            .TakeLast(Math.Min(20, ordered.Length - 1))
            .Average(item => item.Volume);
        var volumeRatio = averageVolume > 0 ? latest.Volume / averageVolume : 0m;
        var changePercent = (latest.Close - previous.Close) / previous.Close * 100m;
        var amount = latest.Close * latest.Volume;

        return new StockQuote(
            StockSymbolNormalizer.NormalizeCode(symbol),
            StockSymbolNormalizer.NormalizeCode(symbol),
            latest.Close,
            Math.Round(changePercent, 4),
            Math.Round(volumeRatio, 4),
            0m,
            amount,
            new DateTimeOffset(latest.TradingTime),
            latest.Open,
            latest.High,
            latest.Low,
            latest.Volume);
    }

    private static bool IsHistoricalDailyStrategy(ISignalStrategy strategy)
    {
        var requirement = strategy.Definition.DataRequirement;
        return requirement.RequiresDailyKLine
            && !requirement.RequiresMinuteKLine
            && !requirement.RequiresSectorData
            && !requirement.RequiresCapitalFlow;
    }
}
