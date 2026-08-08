namespace AShareRadar.Application.Monitoring;

public sealed class MonitorAppService
{
    private readonly MonitorRuntimeState _runtimeState;

    public MonitorAppService(MonitorRuntimeState runtimeState)
    {
        _runtimeState = runtimeState;
    }

    public MonitorRuntimeStatus GetStatus()
    {
        return _runtimeState.GetStatus();
    }

}
