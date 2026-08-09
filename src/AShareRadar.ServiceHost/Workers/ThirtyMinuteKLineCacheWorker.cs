using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Monitoring;

namespace AShareRadar.ServiceHost.Workers;

public sealed class ThirtyMinuteKLineCacheWorker : BackgroundService
{
    private readonly ThirtyMinuteKLineCacheWorkerOptions _options;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IKLineDataProvider _kLineDataProvider;
    private readonly TradingSessionService _tradingSessionService;
    private readonly ILogger<ThirtyMinuteKLineCacheWorker> _logger;

    public ThirtyMinuteKLineCacheWorker(
        ThirtyMinuteKLineCacheWorkerOptions options,
        IMarketDataProvider marketDataProvider,
        IKLineDataProvider kLineDataProvider,
        TradingSessionService tradingSessionService,
        ILogger<ThirtyMinuteKLineCacheWorker> logger)
    {
        _options = options;
        _marketDataProvider = marketDataProvider;
        _kLineDataProvider = kLineDataProvider;
        _tradingSessionService = tradingSessionService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Thirty-minute K-line cache worker is disabled.");
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
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Thirty-minute K-line cache warmup failed.");
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

        var count = Math.Clamp(_options.BarCount, 40, 1200);
        if (_kLineDataProvider is IBatchKLineDataProvider batchProvider)
        {
            var bars = await batchProvider.LoadKLinesAsync(candidateSymbols, "m30", count, cancellationToken);
            _logger.LogInformation(
                "Thirty-minute K-line cache warmed. Candidates={CandidateCount} Loaded={LoadedCount}",
                candidateSymbols.Length,
                bars.Count);
            return;
        }

        var loaded = 0;
        foreach (var symbol in candidateSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "m30", count, cancellationToken);
            if (bars.Count > 0)
            {
                loaded++;
            }
        }

        _logger.LogInformation(
            "Thirty-minute K-line cache warmed. Candidates={CandidateCount} Loaded={LoadedCount}",
            candidateSymbols.Length,
            loaded);
    }

    private string[] BuildCandidateSymbols(MarketSnapshot snapshot)
    {
        var candidateCount = Math.Clamp(_options.CandidateCount, 1, 2000);
        return snapshot.Quotes
            .Where(item => item.Price > 0 && item.Amount >= 30_000_000m && item.ChangePercent >= -8m && item.ChangePercent <= 9m)
            .OrderByDescending(item =>
                Math.Max(item.ChangePercent, 0m) * 8m
                + Math.Min(Math.Max(item.VolumeRatio, 0m), 5m) * 8m
                + Math.Min(item.Amount / 100_000_000m, 35m))
            .Take(candidateCount)
            .Select(item => item.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool ShouldRun()
    {
        return IsTradingDayActiveWindow();
    }

    private TimeSpan GetDelay()
    {
        return IsTradingDayActiveWindow()
            ? TimeSpan.FromSeconds(Math.Clamp(_options.ActiveIntervalSeconds, 60, 1800))
            : TimeSpan.FromMinutes(Math.Clamp(_options.IdleIntervalMinutes, 5, 240));
    }

    private bool IsTradingDayActiveWindow()
    {
        return _tradingSessionService.GetMarketStatus(DateTimeOffset.Now) != MarketStatus.NonTradingDay
            && IsActiveWindow();
    }

    private bool IsActiveWindow()
    {
        if (!TimeOnly.TryParse(_options.ActiveStartTime, out var start))
        {
            start = new TimeOnly(9, 20);
        }

        if (!TimeOnly.TryParse(_options.ActiveEndTime, out var end))
        {
            end = new TimeOnly(15, 10);
        }

        var now = TimeOnly.FromDateTime(DateTime.Now);
        return now >= start && now <= end;
    }
}
