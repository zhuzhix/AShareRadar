using System.Globalization;
using System.Text;
using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class SinaRealtimeProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly MarketDataOptions _options;
    private readonly IHistoricalSymbolProvider _historicalSymbolProvider;
    private IReadOnlyList<string>? _cachedUniverseSymbols;
    private DateTimeOffset _cachedUniverseTime;

    public SinaRealtimeProvider(
        HttpClient httpClient,
        MarketDataOptions options,
        IHistoricalSymbolProvider historicalSymbolProvider)
    {
        _httpClient = httpClient;
        _options = options;
        _historicalSymbolProvider = historicalSymbolProvider;
    }

    public string ProviderName => "Sina";

    public async Task<MarketSnapshot> LoadMarketSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var symbols = await ResolveSymbolsAsync(cancellationToken);
        var quotes = new List<StockQuote>(symbols.Count);
        using var throttler = new SemaphoreSlim(Math.Clamp(_options.RequestConcurrency, 1, 8));

        var tasks = symbols
            .Chunk(Math.Clamp(_options.RequestBatchSize, 1, 100))
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
            var url = "https://hq.sinajs.cn/list=" + string.Join(",", batch);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri("https://finance.sina.com.cn/");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var content = DecodeResponse(bytes);
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

    private static string DecodeResponse(byte[] bytes)
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

    private static StockQuote? TryParseQuote(string line, DateTimeOffset fallbackTime)
    {
        var symbolStart = line.IndexOf("hq_str_", StringComparison.OrdinalIgnoreCase);
        var equals = line.IndexOf('=');
        var quoteStart = line.IndexOf('"');
        var quoteEnd = line.LastIndexOf('"');
        if (symbolStart < 0 || equals <= symbolStart || quoteStart < 0 || quoteEnd <= quoteStart)
        {
            return null;
        }

        var symbol = line[(symbolStart + "hq_str_".Length)..equals].Trim();
        var body = line[(quoteStart + 1)..quoteEnd];
        var parts = body.Split(',');
        if (parts.Length < 32 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        var name = parts[0];
        var open = ParseDecimal(parts.ElementAtOrDefault(1));
        var previousClose = ParseDecimal(parts.ElementAtOrDefault(2));
        var price = ParseDecimal(parts.ElementAtOrDefault(3));
        var high = ParseDecimal(parts.ElementAtOrDefault(4));
        var low = ParseDecimal(parts.ElementAtOrDefault(5));
        var volume = ParseDecimal(parts.ElementAtOrDefault(8));
        var amount = ParseDecimal(parts.ElementAtOrDefault(9));
        if (price <= 0)
        {
            return null;
        }

        var changePercent = previousClose > 0
            ? Math.Round((price - previousClose) / previousClose * 100m, 4)
            : 0m;

        open = open > 0 ? open : price;
        high = high > 0 ? Math.Max(high, price) : Math.Max(open, price);
        low = low > 0 ? Math.Min(low, price) : Math.Min(open, price);

        var quoteTime = TryParseQuoteTime(parts.ElementAtOrDefault(30), parts.ElementAtOrDefault(31), fallbackTime);
        return new StockQuote(
            symbol,
            name,
            price,
            changePercent,
            VolumeRatio: 0,
            TurnoverRate: 0,
            amount,
            quoteTime,
            open,
            high,
            low,
            volume);
    }

    private static DateTimeOffset TryParseQuoteTime(string? date, string? time, DateTimeOffset fallbackTime)
    {
        var value = $"{date} {time}".Trim();
        return DateTime.TryParseExact(
            value,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? new DateTimeOffset(parsed)
            : fallbackTime;
    }

    private static decimal ParseDecimal(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }
}
