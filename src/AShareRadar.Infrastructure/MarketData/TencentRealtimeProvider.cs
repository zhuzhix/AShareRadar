using System.Globalization;
using System.Text;
using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class TencentRealtimeProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly MarketDataOptions _options;
    private readonly IHistoricalSymbolProvider _historicalSymbolProvider;
    private IReadOnlyList<string>? _cachedUniverseSymbols;
    private DateTimeOffset _cachedUniverseTime;

    public TencentRealtimeProvider(
        HttpClient httpClient,
        MarketDataOptions options,
        IHistoricalSymbolProvider historicalSymbolProvider)
    {
        _httpClient = httpClient;
        _options = options;
        _historicalSymbolProvider = historicalSymbolProvider;
    }

    public string ProviderName => "Tencent";

    public async Task<MarketSnapshot> LoadMarketSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var symbols = await ResolveSymbolsAsync(cancellationToken);
        var quotes = new List<StockQuote>(symbols.Count);
        using var throttler = new SemaphoreSlim(Math.Clamp(_options.RequestConcurrency, 1, 12));

        var tasks = symbols
            .Chunk(Math.Clamp(_options.RequestBatchSize, 1, 120))
            .Select(async batch =>
            {
                await throttler.WaitAsync(cancellationToken);
                try
                {
                    var batchQuotes = await LoadBatchAsync(batch, now, cancellationToken);
                    lock (quotes)
                    {
                        quotes.AddRange(batchQuotes);
                    }
                }
                finally
                {
                    throttler.Release();
                }
            });

        await Task.WhenAll(tasks);
        return new MarketSnapshot(now, ProviderName, quotes);
    }

    private async Task<IReadOnlyList<StockQuote>> LoadBatchAsync(
        IReadOnlyList<string> batch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = "http://qt.gtimg.cn/q=" + string.Join(",", batch);
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var content = DecodeTencentResponse(bytes);
            return content
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => TryParseQuote(line, now))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> ResolveSymbolsAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_options.Universe, "AShare", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeProviderSymbols(_options.SeedSymbols);
        }

        if (_cachedUniverseSymbols is { Count: > 0 } &&
            DateTimeOffset.Now - _cachedUniverseTime < TimeSpan.FromMinutes(10))
        {
            return _cachedUniverseSymbols;
        }

        var poolSymbols = await _historicalSymbolProvider.LoadSymbolsAsync(
            _options.StockPool,
            Math.Clamp(_options.MaxSymbols, 1, 6000),
            cancellationToken);
        var symbols = NormalizeProviderSymbols(poolSymbols);
        if (symbols.Count == 0)
        {
            symbols = NormalizeProviderSymbols(_options.SeedSymbols);
        }

        _cachedUniverseSymbols = symbols;
        _cachedUniverseTime = DateTimeOffset.Now;
        return symbols;
    }

    private static IReadOnlyList<string> NormalizeProviderSymbols(IEnumerable<string> symbols)
    {
        var normalized = symbols
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(StockSymbolNormalizer.ToPrefixedCode)
            .Where(item => item.Length == 8)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? ["sh600000", "sz000001"] : normalized;
    }

    private static string DecodeTencentResponse(byte[] bytes)
    {
        try
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static StockQuote? TryParseQuote(string line, DateTimeOffset now)
    {
        var start = line.IndexOf('"');
        var end = line.LastIndexOf('"');
        if (start < 0 || end <= start)
        {
            return null;
        }

        var body = line[(start + 1)..end];
        var parts = body.Split('~');
        if (parts.Length < 39)
        {
            return null;
        }

        var symbol = parts[2];
        var name = parts[1];
        var price = ParseDecimal(parts.ElementAtOrDefault(3));
        var open = ParseDecimal(parts.ElementAtOrDefault(5));
        var volume = ParseDecimal(parts.ElementAtOrDefault(6));
        var changePercent = ParseDecimal(parts.ElementAtOrDefault(32));
        var high = ParseDecimal(parts.ElementAtOrDefault(33));
        var low = ParseDecimal(parts.ElementAtOrDefault(34));
        var turnoverRate = ParseDecimal(parts.ElementAtOrDefault(38));
        var amount = ParseDecimal(parts.ElementAtOrDefault(37)) * 10_000m;
        if (price <= 0)
        {
            return null;
        }

        open = open > 0 ? open : price;
        high = high > 0 ? high : Math.Max(open, price);
        low = low > 0 ? low : Math.Min(open, price);

        return new StockQuote(
            symbol,
            name,
            price,
            changePercent,
            VolumeRatio: 0,
            turnoverRate,
            amount,
            now,
            open,
            high,
            low,
            volume);
    }

    private static decimal ParseDecimal(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }
}
