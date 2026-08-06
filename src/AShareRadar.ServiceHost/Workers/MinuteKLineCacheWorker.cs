using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.ServiceHost.Workers;

public sealed class MinuteKLineCacheWorker : BackgroundService
{
    private readonly MinuteKLineCacheWorkerOptions _options;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IKLineDataProvider _kLineDataProvider;
    private readonly ILogger<MinuteKLineCacheWorker> _logger;
    private bool _startupRunCompleted;

    public MinuteKLineCacheWorker(
        MinuteKLineCacheWorkerOptions options,
        IMarketDataProvider marketDataProvider,
        IKLineDataProvider kLineDataProvider,
        ILogger<MinuteKLineCacheWorker> logger)
    {
        _options = options;
        _marketDataProvider = marketDataProvider;
        _kLineDataProvider = kLineDataProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Minute K-line cache worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelay();
            try
            {
                if (ShouldRun())
                {
                    await WarmCacheAsync(stoppingToken);
                    _startupRunCompleted = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Minute K-line cache warmup failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task WarmCacheAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
        var candidateSymbols = BuildCandidateSymbols(snapshot);
        if (candidateSymbols.Length == 0)
        {
            return;
        }

        var count = Math.Clamp(_options.MinuteBarCount, 60, 1200);
        if (_kLineDataProvider is IBatchKLineDataProvider batchProvider)
        {
            var bars = await batchProvider.LoadKLinesAsync(candidateSymbols, "1m", count, cancellationToken);
            _logger.LogInformation(
                "Minute K-line cache warmed. Candidates={CandidateCount} Loaded={LoadedCount}",
                candidateSymbols.Length,
                bars.Count);
            return;
        }

        var loaded = 0;
        foreach (var symbol in candidateSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "1m", count, cancellationToken);
            if (bars.Count > 0)
            {
                loaded++;
            }
        }

        _logger.LogInformation(
            "Minute K-line cache warmed. Candidates={CandidateCount} Loaded={LoadedCount}",
            candidateSymbols.Length,
            loaded);
    }

    private string[] BuildCandidateSymbols(MarketSnapshot snapshot)
    {
        var candidateCount = Math.Clamp(_options.CandidateCount, 1, 2000);
        return snapshot.Quotes
            .Where(item => item.Price > 0 && item.Amount >= 30_000_000m && item.ChangePercent >= -3m && item.ChangePercent <= 7.5m)
            .OrderByDescending(item =>
                Math.Max(item.ChangePercent, 0m) * 9m
                + Math.Min(Math.Max(item.VolumeRatio, 0m), 5m) * 7m
                + Math.Min(item.Amount / 100_000_000m, 30m))
            .Take(candidateCount)
            .Select(item => item.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool ShouldRun()
    {
        if (_options.RunOnStartup && !_startupRunCompleted)
        {
            return true;
        }

        return IsActiveWindow();
    }

    private TimeSpan GetDelay()
    {
        return IsActiveWindow()
            ? TimeSpan.FromSeconds(Math.Clamp(_options.ActiveIntervalSeconds, 20, 600))
            : TimeSpan.FromMinutes(Math.Clamp(_options.IdleIntervalMinutes, 5, 240));
    }

    private bool IsActiveWindow()
    {
        if (!TimeOnly.TryParse(_options.ActiveStartTime, out var start))
        {
            start = new TimeOnly(9, 25);
        }

        if (!TimeOnly.TryParse(_options.ActiveEndTime, out var end))
        {
            end = new TimeOnly(15, 10);
        }

        var now = TimeOnly.FromDateTime(DateTime.Now);
        return now >= start && now <= end;
    }
}
