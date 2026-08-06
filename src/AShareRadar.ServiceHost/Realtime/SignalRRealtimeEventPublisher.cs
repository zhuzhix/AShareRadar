using AShareRadar.Application.Monitoring;
using AShareRadar.Application.Realtime;
using AShareRadar.Domain.Opportunities;
using AShareRadar.ServiceHost.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AShareRadar.ServiceHost.Realtime;

public sealed class SignalRRealtimeEventPublisher : IRealtimeEventPublisher
{
    private readonly IHubContext<MonitorHub> _hubContext;

    public SignalRRealtimeEventPublisher(IHubContext<MonitorHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishMonitorStatusChangedAsync(MonitorRuntimeStatus status, CancellationToken cancellationToken)
    {
        return _hubContext.Clients.All.SendAsync("MonitorStatusChanged", status, cancellationToken);
    }

    public Task PublishSignalEventCreatedAsync(SignalEvent signalEvent, CancellationToken cancellationToken)
    {
        return _hubContext.Clients.All.SendAsync("SignalEventCreated", signalEvent, cancellationToken);
    }
}
