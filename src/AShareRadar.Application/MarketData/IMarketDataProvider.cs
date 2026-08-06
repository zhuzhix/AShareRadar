using AShareRadar.Domain.MarketData;

namespace AShareRadar.Application.MarketData;

public interface IMarketDataProvider
{
    string ProviderName { get; }

    Task<MarketSnapshot> LoadMarketSnapshotAsync(CancellationToken cancellationToken);
}
