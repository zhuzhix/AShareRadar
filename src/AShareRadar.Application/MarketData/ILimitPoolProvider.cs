namespace AShareRadar.Application.MarketData;

public interface ILimitPoolProvider
{
    string ProviderName { get; }

    Task<LimitPoolSnapshot?> LoadAsync(DateOnly tradingDate, CancellationToken cancellationToken);

    MarketSentimentDataSourceStatus GetStatus();
}

public sealed record LimitPoolSnapshot(
    DateOnly TradingDate,
    int LimitUpCount,
    int LimitDownCount,
    string Source);

