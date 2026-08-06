using AShareRadar.Application.Monitoring;
using AShareRadar.Application.MarketData;
using AShareRadar.Domain.Monitoring;

namespace AShareRadar.ServiceHost.Workers;

public sealed class MonitorWorker : BackgroundService
{
    private readonly ScanOrchestrator _scanOrchestrator;
    private readonly MonitorRuntimeState _runtimeState;
    private readonly TradingSessionService _tradingSessionService;
    private readonly TradingSessionOptions _options;
    private readonly ILogger<MonitorWorker> _logger;

    public MonitorWorker(
        ScanOrchestrator scanOrchestrator,
        MonitorRuntimeState runtimeState,
        TradingSessionService tradingSessionService,
        TradingSessionOptions options,
        ILogger<MonitorWorker> logger)
    {
        _scanOrchestrator = scanOrchestrator;
        _runtimeState = runtimeState;
        _tradingSessionService = tradingSessionService;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var marketStatus = _tradingSessionService.GetMarketStatus(now);
            _runtimeState.SetMarketStatus(marketStatus.ToString());

            var status = _runtimeState.GetStatus();
            var isTradingSession = marketStatus == MarketStatus.Trading;
            if (_options.AutoMonitorEnabled)
            {
                if (isTradingSession && status.MonitorStatus is not "Running" and not "Scanning")
                {
                    _runtimeState.Start(_options.AutoScanIntervalSeconds);
                    status = _runtimeState.GetStatus();
                    _logger.LogInformation("Monitor automatically started for the trading session.");
                }
                else if (!isTradingSession && status.MonitorStatus is not "Paused")
                {
                    _runtimeState.Pause();
                    status = _runtimeState.GetStatus();
                    _logger.LogInformation("Monitor automatically paused. MarketStatus={MarketStatus}", marketStatus);
                }
            }

            if (isTradingSession && status.MonitorStatus is "Running")
            {
                try
                {
                    await _scanOrchestrator.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Monitor scan failed.");
                }
            }

            var delaySeconds = isTradingSession
                ? Math.Clamp(_runtimeState.ScanIntervalSeconds, 5, 300)
                : Math.Clamp(_options.SchedulerPollSeconds, 2, 60);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }
}
