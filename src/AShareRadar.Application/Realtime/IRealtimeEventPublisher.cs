using AShareRadar.Domain.Opportunities;

namespace AShareRadar.Application.Realtime;

public interface IRealtimeEventPublisher
{
    Task PublishMonitorStatusChangedAsync(Monitoring.MonitorRuntimeStatus status, CancellationToken cancellationToken);

    Task PublishSignalEventCreatedAsync(SignalEvent signalEvent, CancellationToken cancellationToken);
}
