using AShareRadar.Application.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyQuantDotNetKLineDataProvider : IKLineDataProvider
{
    private readonly EastMoneyQuantDotNetClient _client;
    private readonly EastMoneyQuantDotNetOptions _options;
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public EastMoneyQuantDotNetKLineDataProvider(
        EastMoneyQuantDotNetClient client,
        EastMoneyQuantDotNetOptions options)
    {
        _client = client;
        _options = options;
    }

    public string ProviderName => "EastMoneyQuantDotNetKLine";

    public async Task<IReadOnlyList<KLineBar>> LoadKLineAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return [];
        }

        var normalizedPeriod = SimulatedKLineDataProvider.NormalizePeriod(period);
        if (!IsSupportedPeriod(normalizedPeriod))
        {
            return [];
        }

        var normalizedSymbol = StockSymbolNormalizer.NormalizeCode(symbol);
        if (normalizedSymbol.Length != 6)
        {
            return [];
        }

        var takeCount = normalizedPeriod == "five-day"
            ? Math.Clamp(count, 1, 300)
            : Math.Clamp(count, 1, 1200);
        var cacheKey = $"{normalizedSymbol}:{normalizedPeriod}:{takeCount}";
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(cacheKey, out var cached)
                && DateTimeOffset.Now - cached.CachedAt
                    < TimeSpan.FromSeconds(Math.Clamp(_options.KLineCacheSeconds, 0, 300)))
            {
                return cached.Bars;
            }
        }

        IReadOnlyList<KLineBar> bars;
        try
        {
            bars = await _client.LoadKLineAsync(normalizedSymbol, normalizedPeriod, takeCount, cancellationToken);
        }
        catch
        {
            bars = [];
        }

        lock (_cacheSync)
        {
            _cache[cacheKey] = new CacheEntry(DateTimeOffset.Now, bars);
        }

        return bars;
    }

    private static bool IsSupportedPeriod(string period)
    {
        return period is "minute" or "five-day" or "m1" or "m5" or "m15" or "m30" or "m60" or "day" or "month";
    }

    private sealed record CacheEntry(DateTimeOffset CachedAt, IReadOnlyList<KLineBar> Bars);
}
