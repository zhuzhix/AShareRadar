using System.Diagnostics;
using AShareRadar.Application.MarketData;
using AShareRadar.Application.Monitoring;
using AShareRadar.Application.Strategies;
using AShareRadar.Application.StrategyTraining;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Application.Backtesting;

public sealed class BacktestReplayService
{
    private const int MaxSymbols = 100;
    private const int MaxLookbackDays = 240;
    private const int MaxLoadBars = 720;

    private readonly IKLineDataProvider _kLineDataProvider;
    private readonly IKLineDataProviderDiagnostics? _kLineDataProviderDiagnostics;
    private readonly IHistoricalSymbolProvider _historicalSymbolProvider;
    private readonly IStrategyRegistry _strategyRegistry;
    private readonly IMarketSentimentStore _marketSentimentStore;
    private readonly ISectorHeatService _sectorHeatService;
    private readonly MarketSentimentStrategyOptions _marketSentimentStrategyOptions;
    private readonly StrategyParameterProfileService _strategyParameterProfileService;

    public BacktestReplayService(
        IKLineDataProvider kLineDataProvider,
        IHistoricalSymbolProvider historicalSymbolProvider,
        IStrategyRegistry strategyRegistry,
        IMarketSentimentStore marketSentimentStore,
        ISectorHeatService sectorHeatService,
        MarketSentimentStrategyOptions marketSentimentStrategyOptions,
        StrategyParameterProfileService strategyParameterProfileService)
    {
        _kLineDataProvider = kLineDataProvider;
        _kLineDataProviderDiagnostics = kLineDataProvider as IKLineDataProviderDiagnostics;
        _historicalSymbolProvider = historicalSymbolProvider;
        _strategyRegistry = strategyRegistry;
        _marketSentimentStore = marketSentimentStore;
        _sectorHeatService = sectorHeatService;
        _marketSentimentStrategyOptions = marketSentimentStrategyOptions;
        _strategyParameterProfileService = strategyParameterProfileService;
    }

    public async Task<BacktestReplayResult> ReplayAsync(
        BacktestReplayQuery query,
        CancellationToken cancellationToken)
    {
        if (query.EndDate < query.StartDate)
        {
            throw new ArgumentException("EndDate must be greater than or equal to StartDate.");
        }

        var stopwatch = Stopwatch.StartNew();
        _kLineDataProviderDiagnostics?.Reset();

        var symbols = await ResolveSymbolsAsync(query, cancellationToken);
        if (symbols.Length == 0)
        {
            stopwatch.Stop();
            var emptyMessage = "No symbols are available. Check manual symbols or replay range.";
            return CreateResult(query, [], [], [], [], [], stopwatch.ElapsedMilliseconds, emptyMessage);
        }

        var strategies = SelectStrategies(query.StrategyCodes);
        var strategyCodes = strategies.Select(item => item.Code).ToArray();
        if (strategies.Count == 0)
        {
            stopwatch.Stop();
            return CreateResult(query, symbols, [], [], [], [], stopwatch.ElapsedMilliseconds, "No enabled strategy matched the request.");
        }

        var lookbackDays = Math.Clamp(query.LookbackDays, 20, MaxLookbackDays);
        var barsBySymbol = await LoadBarsAsync(symbols, cancellationToken);
        var weeklyBarsBySymbol = strategies.Any(item => string.Equals(item.Code, "platform-volume-breakout", StringComparison.OrdinalIgnoreCase))
            ? await LoadWeeklyBarsAsync(symbols, cancellationToken)
            : new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        var replayDates = barsBySymbol.Values
            .SelectMany(item => item)
            .Select(item => DateOnly.FromDateTime(item.TradingTime))
            .Where(item => item >= query.StartDate && item <= query.EndDate)
            .Distinct()
            .Order()
            .ToArray();

        var signals = new List<BacktestSignalItem>();
        var sentimentDateCount = 0;
        foreach (var tradingDate in replayDates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contextBars = BuildContextBars(barsBySymbol, tradingDate, lookbackDays);
            if (contextBars.Count == 0)
            {
                continue;
            }

            var snapshot = BuildSnapshot(tradingDate, contextBars, barsBySymbol);
            var sectorHeatSnapshot = _sectorHeatService.Build(snapshot);
            var conceptHeatSnapshot = _sectorHeatService.BuildConcepts(snapshot);
            var marketSentiment = _marketSentimentStore.Query(tradingDate, 1).FirstOrDefault();
            if (marketSentiment is not null)
            {
                sentimentDateCount++;
            }

            var context = new StrategyContext(
                Guid.NewGuid(),
                tradingDate,
                snapshot,
                StrategyRunMode.HistoricalReplay,
                DailyBarsBySymbol: contextBars,
                WeeklyBarsBySymbol: BuildContextBars(weeklyBarsBySymbol, tradingDate, 120),
                SectorHeatSnapshot: sectorHeatSnapshot,
                ConceptHeatSnapshot: conceptHeatSnapshot,
                MarketSentiment: marketSentiment);

            foreach (var strategy in strategies)
            {
                var strategySignals = await strategy.EvaluateAsync(
                    context with { Parameters = _strategyParameterProfileService.GetActiveParameters(strategy.Code) },
                    cancellationToken);
                var adjustedSignals = MarketSentimentSignalAdjuster.Apply(
                    strategySignals,
                    marketSentiment,
                    _marketSentimentStrategyOptions);
                foreach (var signal in adjustedSignals)
                {
                    signals.Add(MapSignal(signal, tradingDate, barsBySymbol));
                }
            }
        }

        var orderedSignals = signals
            .OrderByDescending(item => item.TradingDate)
            .ThenByDescending(item => item.Score)
            .ToArray();

        stopwatch.Stop();
        var message = orderedSignals.Length == 0
            ? $"Replay scanned {symbols.Length} symbols across {replayDates.Length} trading days but found no strategy hits."
            : $"Replay generated {orderedSignals.Length} signals.";
        if (string.Equals(query.StockPool, "Historical", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(query.StockPool, "RecentActive", StringComparison.OrdinalIgnoreCase))
        {
            message += " Range excludes ST, delisted, suspended, low-price and low-liquidity samples by default.";
        }
        if (_marketSentimentStrategyOptions.Enabled)
        {
            message += sentimentDateCount > 0
                ? $" Applied historical sentiment snapshots for {sentimentDateCount} trading days."
                : " No historical sentiment snapshot matched; raw strategy scores are used.";
        }

        return CreateResult(
            query,
            symbols,
            strategyCodes,
            BuildStrategySummaries(orderedSignals),
            orderedSignals,
            BuildSentimentSummaries(orderedSignals),
            stopwatch.ElapsedMilliseconds,
            message);
    }

    private BacktestReplayResult CreateResult(
        BacktestReplayQuery query,
        IReadOnlyList<string> symbols,
        IReadOnlyList<string> strategyCodes,
        IReadOnlyList<BacktestStrategySummaryItem> strategySummaries,
        IReadOnlyList<BacktestSignalItem> signals,
        IReadOnlyList<BacktestSentimentSummaryItem> sentimentSummaries,
        long elapsedMilliseconds,
        string message)
    {
        return new BacktestReplayResult(
            query.StartDate,
            query.EndDate,
            symbols,
            strategyCodes,
            query.StockPool,
            BuildDataSourceStatus(),
            message,
            elapsedMilliseconds,
            strategySummaries,
            signals,
            sentimentSummaries);
    }

    private string BuildDataSourceStatus()
    {
        return _kLineDataProviderDiagnostics?.LastFallbackUsed == true
            ? $"{_kLineDataProvider.ProviderName}闂佹寧绋戦惌鍌炲磿閹绢喖绀嗛柛鈩冭壘娴滄绱掓担闈涘濠电偛娲幃浠嬪Ω鏈笟鈧獮蹇涙偄閸涘﹥顔嶉梺?fallback"
            : $"{_kLineDataProvider.ProviderName}闂佹寧绋戦張顒€锕㈤鐐嶆盯鍩€椤掆偓闇夐悗锝庝簻閻撳倹淇婇妤€澧查悗姘懇瀵偊鎮ч崼婵堛偊 fallback";
    }

    private IReadOnlyList<ISignalStrategy> SelectStrategies(IReadOnlyList<string>? strategyCodes)
    {
        var allStrategies = _strategyRegistry.GetEnabledStrategies();
        if (strategyCodes is null || strategyCodes.Count == 0)
        {
            return allStrategies;
        }

        var selected = strategyCodes
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return allStrategies
            .Where(item => selected.Contains(item.Code))
            .ToArray();
    }

    private async Task<string[]> ResolveSymbolsAsync(
        BacktestReplayQuery query,
        CancellationToken cancellationToken)
    {
        var maxSymbols = Math.Clamp(query.MaxSymbols, 1, MaxSymbols);
        if (string.Equals(query.StockPool, "Historical", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(query.StockPool, "RecentActive", StringComparison.OrdinalIgnoreCase))
        {
            var poolSymbols = await _historicalSymbolProvider.LoadSymbolsAsync(query.StockPool, maxSymbols, cancellationToken);
            return poolSymbols
                .Select(StockSymbolNormalizer.NormalizeCode)
                .Where(item => item.Length == 6)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxSymbols)
                .ToArray();
        }

        return query.Symbols
            .Select(StockSymbolNormalizer.NormalizeCode)
            .Where(item => item.Length == 6)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxSymbols)
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadBarsAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "day", MaxLoadBars, cancellationToken);
            if (bars.Count > 0)
            {
                items[symbol] = bars.OrderBy(item => item.TradingTime).ToArray();
            }
        }

        return items;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadWeeklyBarsAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "week", 120, cancellationToken);
            if (bars.Count > 0)
            {
                items[symbol] = bars.OrderBy(item => item.TradingTime).ToArray();
            }
        }

        return items;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<KLineBar>> BuildContextBars(
        IReadOnlyDictionary<string, IReadOnlyList<KLineBar>> barsBySymbol,
        DateOnly tradingDate,
        int lookbackDays)
    {
        var items = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (symbol, bars) in barsBySymbol)
        {
            var slice = bars
                .Where(item => DateOnly.FromDateTime(item.TradingTime) <= tradingDate)
                .TakeLast(lookbackDays)
                .ToArray();
            if (slice.Length > 0 && DateOnly.FromDateTime(slice[^1].TradingTime) == tradingDate)
            {
                items[symbol] = slice;
            }
        }

        return items;
    }

    private static MarketSnapshot BuildSnapshot(
        DateOnly tradingDate,
        IReadOnlyDictionary<string, IReadOnlyList<KLineBar>> contextBars,
        IReadOnlyDictionary<string, IReadOnlyList<KLineBar>> allBarsBySymbol)
    {
        var quoteTime = new DateTimeOffset(tradingDate.ToDateTime(new TimeOnly(15, 0)), TimeSpan.FromHours(8));
        var quotes = contextBars.Select(item =>
        {
            var symbol = item.Key;
            var bars = item.Value;
            var current = bars[^1];
            var previousClose = bars.Count >= 2 ? bars[^2].Close : current.Open;
            var changePercent = previousClose > 0 ? (current.Close - previousClose) / previousClose * 100m : 0m;
            var volumeRatio = CalculateVolumeRatio(bars);
            return new StockQuote(
                symbol,
                symbol,
                current.Close,
                changePercent,
                volumeRatio,
                TurnoverRate: 0m,
                Amount: current.Close * current.Volume,
                quoteTime);
        }).ToArray();

        return new MarketSnapshot(quoteTime, "HistoricalReplay", quotes);
    }

    private static decimal CalculateVolumeRatio(IReadOnlyList<KLineBar> bars)
    {
        if (bars.Count < 6)
        {
            return 1m;
        }

        var currentVolume = bars[^1].Volume;
        var averageVolume = bars
            .Take(bars.Count - 1)
            .TakeLast(5)
            .Average(item => item.Volume);
        return averageVolume > 0 ? currentVolume / averageVolume : 1m;
    }

    private static BacktestSignalItem MapSignal(
        StrategySignal signal,
        DateOnly tradingDate,
        IReadOnlyDictionary<string, IReadOnlyList<KLineBar>> barsBySymbol)
    {
        return new BacktestSignalItem(
            tradingDate,
            signal.Symbol,
            signal.Name,
            signal.StrategyCode,
            signal.StrategyName,
            signal.Action.ToString(),
            signal.Confidence.ToString(),
            signal.Score,
            signal.Price,
            signal.Reason,
            signal.Risk,
            CalculateForwardReturn(signal.Symbol, tradingDate, signal.Price, 1, barsBySymbol),
            CalculateForwardReturn(signal.Symbol, tradingDate, signal.Price, 3, barsBySymbol),
            CalculateForwardReturn(signal.Symbol, tradingDate, signal.Price, 5, barsBySymbol),
            signal.Metrics,
            signal.Tags,
            signal.PassedConditions,
            signal.FailedConditions,
            signal.StopLossPrice,
            signal.TakeProfitPrice);
    }

    private static decimal? CalculateForwardReturn(
        string symbol,
        DateOnly tradingDate,
        decimal? signalPrice,
        int forwardDays,
        IReadOnlyDictionary<string, IReadOnlyList<KLineBar>> barsBySymbol)
    {
        if (!signalPrice.HasValue || signalPrice.Value <= 0 || !barsBySymbol.TryGetValue(symbol, out var bars))
        {
            return null;
        }

        var signalIndex = -1;
        for (var i = 0; i < bars.Count; i++)
        {
            if (DateOnly.FromDateTime(bars[i].TradingTime) == tradingDate)
            {
                signalIndex = i;
                break;
            }
        }

        var targetIndex = signalIndex + forwardDays;
        if (signalIndex < 0 || targetIndex >= bars.Count)
        {
            return null;
        }

        return (bars[targetIndex].Close - signalPrice.Value) / signalPrice.Value * 100m;
    }

    private static IReadOnlyList<BacktestStrategySummaryItem> BuildStrategySummaries(
        IReadOnlyList<BacktestSignalItem> signals)
    {
        return signals
            .GroupBy(item => new { item.StrategyCode, item.StrategyName })
            .Select(group =>
            {
                var items = group.ToArray();
                return new BacktestStrategySummaryItem(
                    group.Key.StrategyCode,
                    group.Key.StrategyName,
                    items.Length,
                    items.Average(item => item.Score),
                    CalculateWinRate(items.Select(item => item.Return1Day)),
                    CalculateWinRate(items.Select(item => item.Return3Day)),
                    CalculateWinRate(items.Select(item => item.Return5Day)),
                    CalculateAverageReturn(items.Select(item => item.Return1Day)),
                    CalculateAverageReturn(items.Select(item => item.Return3Day)),
                    CalculateAverageReturn(items.Select(item => item.Return5Day)),
                    CalculateBestReturn(items.Select(item => item.Return5Day)),
                    CalculateWorstReturn(items.Select(item => item.Return5Day)));
            })
            .OrderByDescending(item => item.SignalCount)
            .ThenByDescending(item => item.AverageReturn5Day ?? decimal.MinValue)
            .ToArray();
    }

    private static IReadOnlyList<BacktestSentimentSummaryItem> BuildSentimentSummaries(
        IReadOnlyList<BacktestSignalItem> signals)
    {
        return signals
            .GroupBy(GetSentimentLevel, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToArray();
                return new BacktestSentimentSummaryItem(
                    group.Key,
                    items.Length,
                    items.Average(item => item.Score),
                    CalculateWinRate(items.Select(item => item.Return1Day)),
                    CalculateWinRate(items.Select(item => item.Return3Day)),
                    CalculateWinRate(items.Select(item => item.Return5Day)),
                    CalculateAverageReturn(items.Select(item => item.Return1Day)),
                    CalculateAverageReturn(items.Select(item => item.Return3Day)),
                    CalculateAverageReturn(items.Select(item => item.Return5Day)));
            })
            .OrderBy(item => GetSentimentLevelOrder(item.SentimentLevel))
            .ToArray();
    }

    private static string GetSentimentLevel(BacktestSignalItem signal)
    {
        var tagLevel = signal.Tags?
            .FirstOrDefault(item => item.StartsWith("sentiment:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(tagLevel))
        {
            return tagLevel["sentiment:".Length..].Trim();
        }

        return signal.Metrics?.ContainsKey("market_sentiment_temperature") == true
            ? "WithSentiment"
            : "Unknown";
    }

    private static int GetSentimentLevelOrder(string level)
    {
        return level switch
        {
            "Frozen" => 1,
            "Cold" => 2,
            "Neutral" => 3,
            "Hot" => 4,
            "Overheated" => 5,
            "WithSentiment" => 6,
            _ => 9
        };
    }

    private static decimal? CalculateWinRate(IEnumerable<decimal?> returns)
    {
        var values = returns
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Count(item => item > 0) * 100m / values.Length;
    }

    private static decimal? CalculateAverageReturn(IEnumerable<decimal?> returns)
    {
        var values = returns
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static decimal? CalculateBestReturn(IEnumerable<decimal?> returns)
    {
        var values = returns
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    private static decimal? CalculateWorstReturn(IEnumerable<decimal?> returns)
    {
        var values = returns
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Min();
    }
}
