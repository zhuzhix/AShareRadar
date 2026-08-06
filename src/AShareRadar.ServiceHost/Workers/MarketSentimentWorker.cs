using AShareRadar.Application.MarketData;

namespace AShareRadar.ServiceHost.Workers;

public sealed class MarketSentimentWorker : BackgroundService
{
    private readonly MarketSentimentWorkerOptions _options;
    private readonly MarketSentimentService _marketSentimentService;
    private readonly ExternalSentimentSdkUpdateService _externalSentimentSdkUpdateService;
    private readonly MarketSentimentRuntimeState _runtimeState;
    private readonly ILogger<MarketSentimentWorker> _logger;
    private bool _startupRunCompleted;

    public MarketSentimentWorker(
        MarketSentimentWorkerOptions options,
        MarketSentimentService marketSentimentService,
        ExternalSentimentSdkUpdateService externalSentimentSdkUpdateService,
        MarketSentimentRuntimeState runtimeState,
        ILogger<MarketSentimentWorker> logger)
    {
        _options = options;
        _marketSentimentService = marketSentimentService;
        _externalSentimentSdkUpdateService = externalSentimentSdkUpdateService;
        _runtimeState = runtimeState;
        _logger = logger;
        _runtimeState.Configure(options.Enabled);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Market sentiment worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelay();
            try
            {
                if (ShouldRun())
                {
                    var startedAt = DateTimeOffset.Now;
                    _runtimeState.MarkRunning(startedAt);
                    await _externalSentimentSdkUpdateService.TryUpdateAsync(stoppingToken);
                    await _marketSentimentService.GetSnapshotAsync(stoppingToken);
                    _startupRunCompleted = true;
                    _runtimeState.MarkSucceeded(DateTimeOffset.Now, DateTimeOffset.Now.Add(delay));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Market sentiment scan failed.");
                _runtimeState.MarkFailed(DateTimeOffset.Now, DateTimeOffset.Now.Add(delay), ex.Message);
            }

            await Task.Delay(delay, stoppingToken);
        }
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
