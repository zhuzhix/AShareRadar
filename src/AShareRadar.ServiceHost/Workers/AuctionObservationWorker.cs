using AShareRadar.Application.MarketData;
using AShareRadar.Domain.Monitoring;

namespace AShareRadar.ServiceHost.Workers;

public sealed class AuctionObservationWorker : BackgroundService
{
    private readonly AuctionObservationService _service;
    private readonly TradingSessionService _session;
    private readonly AuctionObservationOptions _options;
    private readonly ILogger<AuctionObservationWorker> _logger;

    public AuctionObservationWorker(
        AuctionObservationService service,
        TradingSessionService session,
        AuctionObservationOptions options,
        ILogger<AuctionObservationWorker> logger)
    {
        _service = service;
        _session = session;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            try
            {
                var status = _session.GetMarketStatus(now);
                if (_options.Enabled && status is MarketStatus.CallAuction or MarketStatus.Trading)
                {
                    await _service.RefreshAsync(now, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auction observation refresh failed.");
            }

            var seconds = _session.GetMarketStatus(DateTimeOffset.Now) == MarketStatus.CallAuction
                ? Math.Clamp(_options.PollIntervalSeconds, 1, 30)
                : 10;
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}
