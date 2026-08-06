namespace AShareRadar.Application.MarketData;

public interface IIntradayKLineOverlayService
{
    Task<IReadOnlyList<KLineBar>> AppendTemporaryDailyBarAsync(
        string symbol,
        string period,
        IReadOnlyList<KLineBar> historicalBars,
        CancellationToken cancellationToken);
}
