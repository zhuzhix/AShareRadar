using AShareRadar.Application.MarketData;

namespace AShareRadar.ServiceHost.Workers;

public sealed class HistoricalDataUpdateWorker : BackgroundService
{
    private readonly HistoricalDataUpdateOptions _options;
    private readonly HistoricalDataUpdateService _updateService;
    private readonly TradingSessionService _tradingSessionService;
    private readonly ILogger<HistoricalDataUpdateWorker> _logger;
    private bool _startupCheckCompleted;

    public HistoricalDataUpdateWorker(
        HistoricalDataUpdateOptions options,
        HistoricalDataUpdateService updateService,
        TradingSessionService tradingSessionService,
        ILogger<HistoricalDataUpdateWorker> logger)
    {
        _options = options;
        _updateService = updateService;
        _tradingSessionService = tradingSessionService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Historical data update worker is disabled.");
            return;
        }

        await DelayStartupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (TryGetPendingTarget(out var targetDate, out var trigger))
                {
                    await _updateService.TryRunScheduledAsync(targetDate, trigger, stoppingToken);
                }

                _startupCheckCompleted = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Historical data update failed.");
            }

            var delayMinutes = Math.Clamp(_options.CheckIntervalMinutes, 5, 240);
            await Task.Delay(TimeSpan.FromMinutes(delayMinutes), stoppingToken);
        }
    }

    private bool TryGetPendingTarget(out DateOnly targetDate, out string trigger)
    {
        if (!TimeOnly.TryParse(_options.RunAfterTime, out var runAfter))
        {
            runAfter = new TimeOnly(15, 15);
        }

        targetDate = _tradingSessionService.GetLatestCompletedTradingDate(DateTimeOffset.Now, runAfter);
        var latestDate = _updateService.GetStatus().LatestTradingDate;
        if (latestDate.HasValue && latestDate.Value >= targetDate)
        {
            trigger = string.Empty;
            return false;
        }

        trigger = _startupCheckCompleted ? "scheduled-catch-up" : "startup-catch-up";
        return true;
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
