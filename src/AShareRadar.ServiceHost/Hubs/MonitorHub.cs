using Microsoft.AspNetCore.SignalR;
using AShareRadar.Contracts.MarketData;
using AShareRadar.ServiceHost.Services;

namespace AShareRadar.ServiceHost.Hubs;

public sealed class MonitorHub : Hub
{
    private readonly MarketMappingSyncService _mappingSyncService;
    private readonly ILogger<MonitorHub> _logger;

    public MonitorHub(MarketMappingSyncService mappingSyncService, ILogger<MonitorHub> logger)
    {
        _mappingSyncService = mappingSyncService;
        _logger = logger;
    }

    public async Task<MarketMappingSyncResult> UploadMarketMappings(MarketMappingSyncRequest request)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = request.Version });
        _logger.LogInformation(
            "SignalR mapping invocation received. TraceId={TraceId} ConnectionId={ConnectionId} UserIdentifier={UserIdentifier} SectorRows={SectorRows} ConceptRows={ConceptRows}",
            request.Version,
            Context.ConnectionId,
            Context.UserIdentifier,
            request.SectorMappings.Count,
            request.ConceptMappings.Count);
        var result = await _mappingSyncService.SyncAsync(request, Context.ConnectionAborted);
        _logger.LogInformation(
            "SignalR mapping invocation completed. TraceId={TraceId} ConnectionId={ConnectionId} Success={Success}",
            request.Version,
            Context.ConnectionId,
            result.Success);
        return result;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR client connected. ConnectionId={ConnectionId} UserIdentifier={UserIdentifier}", Context.ConnectionId, Context.UserIdentifier);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            _logger.LogInformation("SignalR client disconnected. ConnectionId={ConnectionId}", Context.ConnectionId);
        }
        else
        {
            _logger.LogWarning(exception, "SignalR client disconnected with an error. ConnectionId={ConnectionId}", Context.ConnectionId);
        }
        return base.OnDisconnectedAsync(exception);
    }
}
