namespace AShareRadar.Application.MarketData;

public interface IMarketUniverseProvider
{
    Task<MarketUniverseSnapshot?> LoadUniverseAsync(CancellationToken cancellationToken);
}

public sealed record MarketUniverseSnapshot(
    DateTimeOffset SnapshotTime,
    string ProviderName,
    int TotalCount);
