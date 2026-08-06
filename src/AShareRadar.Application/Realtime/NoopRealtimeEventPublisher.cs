using AShareRadar.Domain.Opportunities;

namespace AShareRadar.Application.Realtime;

public sealed class NoopRealtimeEventPublisher : IRealtimeEventPublisher
{
    public Task PublishMonitorStatusChangedAsync(Monitoring.MonitorRuntimeStatus status, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task PublishSignalEventCreatedAsync(SignalEvent signalEvent, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
