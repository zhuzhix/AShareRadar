using AShareRadar.Application.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class HybridKLineDataProvider : IBatchKLineDataProvider
{
    private const int IntradayCacheConcurrency = 8;

    private readonly DuckDbMinuteKLineCacheProvider _minuteCacheProvider;
    private readonly DuckDbIntradayKLineCacheProvider _intradayCacheProvider;
    private readonly EastMoneyQuantDotNetKLineDataProvider _eastMoneyDotNetProvider;
    private readonly EastMoneyQuantKLineDataProvider _eastMoneyIntradayProvider;
    private readonly TencentKLineDataProvider _intradayProvider;
    private readonly DuckDbKLineDataProvider _dailyProvider;

    public HybridKLineDataProvider(
        DuckDbMinuteKLineCacheProvider minuteCacheProvider,
        DuckDbIntradayKLineCacheProvider intradayCacheProvider,
        EastMoneyQuantDotNetKLineDataProvider eastMoneyDotNetProvider,
        EastMoneyQuantKLineDataProvider eastMoneyIntradayProvider,
        TencentKLineDataProvider intradayProvider,
        DuckDbKLineDataProvider dailyProvider)
    {
        _minuteCacheProvider = minuteCacheProvider;
        _intradayCacheProvider = intradayCacheProvider;
        _eastMoneyDotNetProvider = eastMoneyDotNetProvider;
        _eastMoneyIntradayProvider = eastMoneyIntradayProvider;
        _intradayProvider = intradayProvider;
        _dailyProvider = dailyProvider;
    }

    public string ProviderName => "Hybrid";

    public async Task<IReadOnlyList<KLineBar>> LoadKLineAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var normalizedPeriod = SimulatedKLineDataProvider.NormalizePeriod(period);
        if (IsIntradayPeriod(normalizedPeriod))
        {
            var cachedBars = await LoadCachedIntradayKLineAsync(symbol, normalizedPeriod, count, cancellationToken);
            if (cachedBars.Count > 0)
            {
                return cachedBars;
            }

            return await LoadExternalIntradayKLineAsync(symbol, normalizedPeriod, count, cancellationToken);
        }

        var eastMoneyDailyBars = await _eastMoneyDotNetProvider.LoadKLineAsync(symbol, normalizedPeriod, count, cancellationToken);
        if (eastMoneyDailyBars.Count > 0)
        {
            return eastMoneyDailyBars;
        }

        return [];
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadKLinesAsync(
        IReadOnlyList<string> symbols,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var normalizedPeriod = SimulatedKLineDataProvider.NormalizePeriod(period);
        if (!IsIntradayPeriod(normalizedPeriod))
        {
            var dailyResults = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
            using var dailyThrottler = new SemaphoreSlim(IntradayCacheConcurrency);
            var dailyTasks = symbols.Select(async symbol =>
            {
                await dailyThrottler.WaitAsync(cancellationToken);
                try
                {
                    var bars = await _eastMoneyDotNetProvider.LoadKLineAsync(symbol, normalizedPeriod, count, cancellationToken);
                    if (bars.Count > 0)
                    {
                        lock (dailyResults)
                            dailyResults[StockSymbolNormalizer.NormalizeCode(symbol)] = bars;
                    }
                }
                finally
                {
                    dailyThrottler.Release();
                }
            });
            await Task.WhenAll(dailyTasks);
            return dailyResults;
        }

        var results = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        var cachedResults = await LoadCachedIntradayKLinesAsync(symbols, normalizedPeriod, count, cancellationToken);
        foreach (var item in cachedResults)
        {
            results[item.Key] = item.Value;
        }

        var missingSymbols = symbols
            .Where(symbol => !results.ContainsKey(StockSymbolNormalizer.NormalizeCode(symbol)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingSymbols.Length == 0)
        {
            return results;
        }

        using var throttler = new SemaphoreSlim(IntradayCacheConcurrency);
        var tasks = missingSymbols.Select(async symbol =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var bars = await LoadKLineAsync(symbol, normalizedPeriod, count, cancellationToken);
                if (bars.Count > 0)
                {
                    lock (results)
                    {
                        results[StockSymbolNormalizer.NormalizeCode(symbol)] = bars;
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

    private async Task<IReadOnlyList<KLineBar>> LoadExternalIntradayKLineAsync(
        string symbol,
        string normalizedPeriod,
        int count,
        CancellationToken cancellationToken)
    {
        var eastMoneyDotNetBars = await _eastMoneyDotNetProvider.LoadKLineAsync(symbol, normalizedPeriod, count, cancellationToken);
        if (eastMoneyDotNetBars.Count > 0)
        {
            await SaveIntradayBarsIfNeededAsync(symbol, normalizedPeriod, eastMoneyDotNetBars, cancellationToken);
            return eastMoneyDotNetBars;
        }

        var eastMoneyBars = await _eastMoneyIntradayProvider.LoadKLineAsync(symbol, normalizedPeriod, count, cancellationToken);
        if (eastMoneyBars.Count > 0)
        {
            await SaveIntradayBarsIfNeededAsync(symbol, normalizedPeriod, eastMoneyBars, cancellationToken);
            return eastMoneyBars;
        }

        var intradayBars = await _intradayProvider.LoadKLineAsync(symbol, normalizedPeriod, count, cancellationToken);
        if (intradayBars.Count > 0)
        {
            await SaveIntradayBarsIfNeededAsync(symbol, normalizedPeriod, intradayBars, cancellationToken);
            return intradayBars;
        }

        return [];
    }

    private async Task<IReadOnlyList<KLineBar>> LoadCachedIntradayKLineAsync(
        string symbol,
        string normalizedPeriod,
        int count,
        CancellationToken cancellationToken)
    {
        return normalizedPeriod is "minute" or "m1"
            ? await _minuteCacheProvider.LoadKLineAsync(symbol, normalizedPeriod, count, cancellationToken)
            : await _intradayCacheProvider.LoadKLineAsync(symbol, normalizedPeriod, count, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadCachedIntradayKLinesAsync(
        IReadOnlyList<string> symbols,
        string normalizedPeriod,
        int count,
        CancellationToken cancellationToken)
    {
        return normalizedPeriod is "minute" or "m1"
            ? await _minuteCacheProvider.LoadKLinesAsync(symbols, normalizedPeriod, count, cancellationToken)
            : await _intradayCacheProvider.LoadKLinesAsync(symbols, normalizedPeriod, count, cancellationToken);
    }

    private async Task SaveIntradayBarsIfNeededAsync(
        string symbol,
        string normalizedPeriod,
        IReadOnlyList<KLineBar> bars,
        CancellationToken cancellationToken)
    {
        if (normalizedPeriod is "minute" or "m1")
        {
            await _minuteCacheProvider.SaveAsync(symbol, bars, cancellationToken);
        }
        else if (normalizedPeriod is "m30")
        {
            await _intradayCacheProvider.SaveAsync(symbol, normalizedPeriod, bars, cancellationToken);
        }
    }

    private static bool IsIntradayPeriod(string period)
    {
        return period is "minute" or "five-day" or "m1" or "m5" or "m15" or "m30" or "m60";
    }
}
