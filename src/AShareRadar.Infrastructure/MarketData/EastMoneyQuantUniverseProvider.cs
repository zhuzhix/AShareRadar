using AShareRadar.Application.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyQuantUniverseProvider : IMarketUniverseProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private readonly EastMoneyQuantDotNetClient _client;
    private readonly object _sync = new();
    private MarketUniverseSnapshot? _cached;

    public EastMoneyQuantUniverseProvider(EastMoneyQuantDotNetClient client)
    {
        _client = client;
    }

    public async Task<MarketUniverseSnapshot?> LoadUniverseAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_cached is not null && DateTimeOffset.Now - _cached.SnapshotTime < CacheDuration)
            {
                return _cached;
            }
        }

        var count = await _client.LoadAshareUniverseCountAsync(cancellationToken);
        if (count <= 0)
        {
            return null;
        }

        var snapshot = new MarketUniverseSnapshot(DateTimeOffset.Now, "EastMoneyQuantSdk", count);
        lock (_sync)
        {
            _cached = snapshot;
        }

        return snapshot;
    }
}
