using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class CompositeMarketDataProvider : IMarketDataProvider
{
    private static readonly TimeSpan SnapshotCacheDuration = TimeSpan.FromSeconds(60);
    private readonly IReadOnlyList<IMarketDataProvider> _providers;
    private readonly object _cacheSync = new();
    private MarketSnapshot? _cachedSnapshot;
    private Task<MarketSnapshot>? _runningSnapshotTask;

    public CompositeMarketDataProvider(IEnumerable<IMarketDataProvider> providers)
    {
        _providers = providers
            .Where(item => item is not CompositeMarketDataProvider)
            .ToArray();
    }

    public string ProviderName => "Composite";

    public Task<MarketSnapshot> LoadMarketSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (_cacheSync)
        {
            if (_cachedSnapshot is not null && DateTimeOffset.Now - _cachedSnapshot.SnapshotTime < SnapshotCacheDuration)
            {
                return Task.FromResult(_cachedSnapshot);
            }

            if (_runningSnapshotTask is { IsCompleted: false })
            {
                return _runningSnapshotTask.WaitAsync(cancellationToken);
            }

            _runningSnapshotTask = LoadMarketSnapshotCoreAsync(cancellationToken);
            return AwaitAndCacheSnapshotAsync(_runningSnapshotTask);
        }
    }

    private async Task<MarketSnapshot> AwaitAndCacheSnapshotAsync(Task<MarketSnapshot> snapshotTask)
    {
        try
        {
            var snapshot = await snapshotTask;
            lock (_cacheSync)
            {
                _cachedSnapshot = snapshot;
            }

            return snapshot;
        }
        finally
        {
            lock (_cacheSync)
            {
                if (ReferenceEquals(_runningSnapshotTask, snapshotTask))
                {
                    _runningSnapshotTask = null;
                }
            }
        }
    }

    private async Task<MarketSnapshot> LoadMarketSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var quotesBySymbol = new Dictionary<string, StockQuote>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? snapshotTime = null;

        foreach (var provider in _providers)
        {
            try
            {
                var snapshot = await provider.LoadMarketSnapshotAsync(cancellationToken);
                if (snapshot.Quotes.Count == 0)
                {
                    errors.Add($"{provider.ProviderName}: empty snapshot");
                    continue;
                }

                if (string.Equals(provider.ProviderName, "EastMoneyQuantDotNet", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(provider.ProviderName, "EastMoneyQuant", StringComparison.OrdinalIgnoreCase))
                {
                    return snapshot;
                }

                snapshotTime ??= snapshot.SnapshotTime;
                foreach (var quote in snapshot.Quotes)
                {
                    quotesBySymbol.TryAdd(StockSymbolNormalizer.NormalizeCode(quote.Symbol), quote);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{provider.ProviderName}: {ex.Message}");
            }
        }

        if (quotesBySymbol.Count > 0)
        {
            return new MarketSnapshot(
                snapshotTime ?? DateTimeOffset.Now,
                ProviderName,
                quotesBySymbol.Values.ToArray());
        }

        throw new InvalidOperationException("All market data providers failed. " + string.Join(" | ", errors));
    }
}
