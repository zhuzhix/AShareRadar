using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class IntradayKLineOverlayService : IIntradayKLineOverlayService
{
    private readonly TencentRealtimeProvider _marketDataProvider;
    private readonly TradingCalendarService _tradingCalendarService;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private MarketSnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedSnapshotTime;

    public IntradayKLineOverlayService(
        TencentRealtimeProvider marketDataProvider,
        TradingCalendarService tradingCalendarService)
    {
        _marketDataProvider = marketDataProvider;
        _tradingCalendarService = tradingCalendarService;
    }

    public async Task<IReadOnlyList<KLineBar>> AppendTemporaryDailyBarAsync(
        string symbol,
        string period,
        IReadOnlyList<KLineBar> historicalBars,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(SimulatedKLineDataProvider.NormalizePeriod(period), "day", StringComparison.OrdinalIgnoreCase))
        {
            return historicalBars;
        }

        var todayDate = DateOnly.FromDateTime(DateTime.Today);
        if (!_tradingCalendarService.IsTradingDay(todayDate))
        {
            return historicalBars;
        }

        var today = todayDate.ToDateTime(TimeOnly.MinValue);
        if (historicalBars.Count > 0 && historicalBars[^1].TradingTime.Date >= today)
        {
            return historicalBars;
        }

        var code = StockSymbolNormalizer.NormalizeCode(symbol);
        var snapshot = await LoadCachedSnapshotAsync(cancellationToken);
        var quote = snapshot.Quotes.FirstOrDefault(item =>
            string.Equals(StockSymbolNormalizer.NormalizeCode(item.Symbol), code, StringComparison.OrdinalIgnoreCase));
        if (quote is null || quote.Price <= 0)
        {
            return historicalBars;
        }

        var temporaryBar = new KLineBar(
            today,
            quote.Open > 0 ? quote.Open : quote.Price,
            quote.High > 0 ? Math.Max(quote.High, quote.Price) : quote.Price,
            quote.Low > 0 ? Math.Min(quote.Low, quote.Price) : quote.Price,
            quote.Price,
            quote.Volume);

        return historicalBars
            .Concat([temporaryBar])
            .TakeLast(Math.Max(historicalBars.Count, 1))
            .ToArray();
    }

    private async Task<MarketSnapshot> LoadCachedSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (_cachedSnapshot is not null && now - _cachedSnapshotTime < TimeSpan.FromSeconds(45))
        {
            return _cachedSnapshot;
        }

        await _snapshotLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.Now;
            if (_cachedSnapshot is not null && now - _cachedSnapshotTime < TimeSpan.FromSeconds(45))
            {
                return _cachedSnapshot;
            }

            _cachedSnapshot = await _marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
            _cachedSnapshotTime = DateTimeOffset.Now;
            return _cachedSnapshot;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }
}
