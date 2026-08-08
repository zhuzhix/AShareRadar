using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyQuantDotNetRealtimeProvider : IMarketDataProvider
{
    private readonly EastMoneyQuantDotNetClient _client;
    private readonly EastMoneyQuantDotNetOptions _options;
    private readonly MarketDataOptions _marketDataOptions;
    private readonly IHistoricalSymbolProvider _symbolProvider;
    private readonly DuckDbKLineDataProvider _dailyProvider;
    private readonly object _cacheSync = new();
    private MarketSnapshot? _cachedSnapshot;

    public EastMoneyQuantDotNetRealtimeProvider(
        EastMoneyQuantDotNetClient client,
        EastMoneyQuantDotNetOptions options,
        MarketDataOptions marketDataOptions,
        IHistoricalSymbolProvider symbolProvider,
        DuckDbKLineDataProvider dailyProvider)
    {
        _client = client;
        _options = options;
        _marketDataOptions = marketDataOptions;
        _symbolProvider = symbolProvider;
        _dailyProvider = dailyProvider;
    }

    public string ProviderName => "EastMoneyQuantDotNet";

    public async Task<MarketSnapshot> LoadMarketSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("EastMoney Quant .NET provider is disabled.");
        }

        lock (_cacheSync)
        {
            if (_cachedSnapshot is not null
                && DateTimeOffset.Now - _cachedSnapshot.SnapshotTime
                    < TimeSpan.FromSeconds(Math.Clamp(_options.SnapshotCacheSeconds, 0, 300)))
            {
                return _cachedSnapshot;
            }
        }

        var maxSymbols = Math.Clamp(_marketDataOptions.MaxSymbols, 1, 6000);
        var symbols = string.Equals(_marketDataOptions.StockPool, "AShare", StringComparison.OrdinalIgnoreCase)
            ? await _client.LoadAshareUniverseSymbolsAsync(cancellationToken)
            : await _symbolProvider.LoadSymbolsAsync(
                _marketDataOptions.StockPool,
                maxSymbols,
                cancellationToken);
        if (symbols.Count == 0)
        {
            symbols = _marketDataOptions.SeedSymbols;
        }
        else if (symbols.Count > maxSymbols)
        {
            symbols = symbols.Take(maxSymbols).ToArray();
        }

        var batchSize = Math.Clamp(_options.BatchSize, 1, 500);
        var quotes = new List<StockQuote>(symbols.Count);
        foreach (var batch in symbols
                     .Select(StockSymbolNormalizer.NormalizeCode)
                     .Where(item => item.Length == 6)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchQuotes = await _client.LoadCurrentAsync(batch, cancellationToken);
            var previousCloses = await LoadPreviousClosesAsync(batch, cancellationToken);
            foreach (var quote in batchQuotes)
            {
                quotes.Add(ToStockQuote(quote, previousCloses.GetValueOrDefault(quote.Symbol)));
            }
        }

        if (quotes.Count == 0)
        {
            throw new InvalidOperationException("EastMoney Quant .NET provider returned an empty snapshot.");
        }

        var snapshot = new MarketSnapshot(
            DateTimeOffset.Now,
            ProviderName,
            quotes
                .GroupBy(item => StockSymbolNormalizer.NormalizeCode(item.Symbol), StringComparer.OrdinalIgnoreCase)
                .Select(item => item.First())
                .ToArray());

        lock (_cacheSync)
        {
            _cachedSnapshot = snapshot;
        }

        return snapshot;
    }

    private async Task<IReadOnlyDictionary<string, decimal>> LoadPreviousClosesAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var barsBySymbol = await _dailyProvider.LoadKLinesAsync(symbols, "day", 5, cancellationToken);
            var today = DateOnly.FromDateTime(DateTime.Today);
            foreach (var (symbol, bars) in barsBySymbol)
            {
                var previousBar = bars
                    .Where(item => DateOnly.FromDateTime(item.TradingTime) < today)
                    .OrderByDescending(item => item.TradingTime)
                    .FirstOrDefault();
                if (previousBar is not null)
                {
                    result[StockSymbolNormalizer.NormalizeCode(symbol)] = previousBar.Close;
                }
            }
        }
        catch
        {
            // Previous close is used to preserve change-percent semantics; snapshot remains usable without it.
        }

        return result;
    }

    private static StockQuote ToStockQuote(EastMoneyQuantDotNetQuote quote, decimal previousClose)
    {
        var calculatedChange = previousClose > 0
            ? Math.Round((quote.Price - previousClose) / previousClose * 100m, 4)
            : quote.ChangePercent;
        var changePercent = Math.Abs(quote.ChangePercent) <= 30m
            ? quote.ChangePercent
            : calculatedChange;
        if (Math.Abs(changePercent) > 30m)
            changePercent = 0m;

        return new StockQuote(
            quote.Symbol,
            string.IsNullOrWhiteSpace(quote.Name) ? quote.Symbol : quote.Name,
            quote.Price,
            changePercent,
            quote.VolumeRatio,
            quote.TurnoverRate,
            quote.Amount,
            quote.QuoteTime,
            quote.Open,
            quote.High,
            quote.Low,
            quote.Volume);
    }
}
