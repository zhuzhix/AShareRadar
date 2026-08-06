namespace AShareRadar.Application.MarketData;

public interface IMarketSentimentStore
{
    void Save(MarketSentimentSnapshot snapshot);

    MarketSentimentSnapshot? GetLatest();

    IReadOnlyList<MarketSentimentSnapshot> Query(DateOnly? tradingDate, int count);
}

public sealed class InMemoryMarketSentimentStore : IMarketSentimentStore
{
    private readonly object _gate = new();
    private readonly List<MarketSentimentSnapshot> _snapshots = [];

    public void Save(MarketSentimentSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshots.Add(snapshot);
            if (_snapshots.Count > 480)
            {
                _snapshots.RemoveRange(0, _snapshots.Count - 480);
            }
        }
    }

    public MarketSentimentSnapshot? GetLatest()
    {
        lock (_gate)
        {
            return _snapshots
                .OrderByDescending(item => item.SnapshotTime)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<MarketSentimentSnapshot> Query(DateOnly? tradingDate, int count)
    {
        lock (_gate)
        {
            var query = _snapshots.AsEnumerable();
            if (tradingDate.HasValue)
            {
                query = query.Where(item => DateOnly.FromDateTime(item.SnapshotTime.LocalDateTime) == tradingDate.Value);
            }

            return query
                .OrderByDescending(item => item.SnapshotTime)
                .Take(Math.Clamp(count, 1, 10000))
                .ToArray();
        }
    }
}

public sealed class MarketSentimentRuntimeState
{
    private readonly object _gate = new();

    public bool IsEnabled { get; private set; }

    public bool IsRunning { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    public DateTimeOffset? NextRunAt { get; private set; }

    public string LastStatus { get; private set; } = "Idle";

    public string? LastError { get; private set; }

    public void Configure(bool enabled)
    {
        lock (_gate)
        {
            IsEnabled = enabled;
        }
    }

    public void MarkRunning(DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            IsRunning = true;
            LastStatus = "Running";
            LastError = null;
            LastRunAt = startedAt;
        }
    }

    public void MarkSucceeded(DateTimeOffset finishedAt, DateTimeOffset nextRunAt)
    {
        lock (_gate)
        {
            IsRunning = false;
            LastRunAt = finishedAt;
            NextRunAt = nextRunAt;
            LastStatus = "Succeeded";
            LastError = null;
        }
    }

    public void MarkFailed(DateTimeOffset failedAt, DateTimeOffset nextRunAt, string error)
    {
        lock (_gate)
        {
            IsRunning = false;
            LastRunAt = failedAt;
            NextRunAt = nextRunAt;
            LastStatus = "Failed";
            LastError = error;
        }
    }
}
