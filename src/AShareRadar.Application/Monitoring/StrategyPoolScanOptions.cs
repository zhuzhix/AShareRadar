namespace AShareRadar.Application.Monitoring;

public sealed class StrategyPoolScanOptions
{
    public bool Enabled { get; set; } = true;

    public int ObservationIntervalSeconds { get; set; } = 300;

    public bool RunObservationOnStartup { get; set; } = true;

    public string[] RealtimeStrategyCodes { get; set; } =
    [
        "main-sector-resonance",
        "main-sector-gap-recovery"
    ];
}
