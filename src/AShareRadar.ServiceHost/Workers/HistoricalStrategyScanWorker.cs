namespace AShareRadar.ServiceHost.Workers;

public sealed class HistoricalStrategyScanWorker : BackgroundService
{
    private readonly HistoricalStrategyScanOptions _options;
    private readonly HistoricalStrategyScanService _scanService;
    private readonly ILogger<HistoricalStrategyScanWorker> _logger;
    private DateTimeOffset? _lastRunAt;

    public HistoricalStrategyScanWorker(
        HistoricalStrategyScanOptions options,
        HistoricalStrategyScanService scanService,
        ILogger<HistoricalStrategyScanWorker> logger)
    {
        _options = options;
        _scanService = scanService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Historical strategy scan worker is disabled.");
            return;
        }

        await DelayStartupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ShouldRun())
                {
                    if (await _scanService.TryRunScheduledAsync(stoppingToken))
                    {
                        _lastRunAt = DateTimeOffset.Now;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Historical strategy scan failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(_options.CheckIntervalMinutes, 5, 240)), stoppingToken);
        }
    }

    private bool ShouldRun()
    {
        var now = DateTimeOffset.Now;
        if (_lastRunAt is null)
        {
            if (_options.RunOnStartup)
            {
                return true;
            }

            if (!TimeOnly.TryParse(_options.RunAfterTime, out var firstRunAfter))
            {
                firstRunAfter = new TimeOnly(18, 10);
            }

            return TimeOnly.FromDateTime(now.LocalDateTime) >= firstRunAfter;
        }

        return now - _lastRunAt.Value >= TimeSpan.FromMinutes(Math.Clamp(_options.RepeatIntervalMinutes, 30, 1440));
    }

    private async Task DelayStartupAsync(CancellationToken stoppingToken)
    {
        var delaySeconds = Math.Clamp(_options.StartupDelaySeconds, 0, 600);
        if (delaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }
}
