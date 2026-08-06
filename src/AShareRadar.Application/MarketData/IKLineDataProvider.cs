namespace AShareRadar.Application.MarketData;

public interface IKLineDataProvider
{
    string ProviderName { get; }

    Task<IReadOnlyList<KLineBar>> LoadKLineAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken);
}

public interface IBatchKLineDataProvider : IKLineDataProvider
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadKLinesAsync(
        IReadOnlyList<string> symbols,
        string period,
        int count,
        CancellationToken cancellationToken);
}
