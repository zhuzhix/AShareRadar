namespace AShareRadar.Application.Monitoring;

public sealed record MonitorRuntimeStatus(
    string MarketStatus,
    string MonitorStatus,
    DateTimeOffset? LastScanTime,
    DateTimeOffset? NextScanTime,
    int ActiveOpportunityCount,
    int TodayNewCount,
    int DisappearedCount,
    int FocusedCount,
    string HistoricalStrategyScanStatus = "NotStarted",
    DateTimeOffset? LastHistoricalStrategyScanTime = null,
    DateTimeOffset? NextHistoricalStrategyScanTime = null,
    int HistoricalStrategyScanSymbolCount = 0,
    int HistoricalStrategyScanSignalCount = 0,
    string RealtimePoolStatus = "NotStarted",
    string ObservationPoolStatus = "NotStarted",
    int RealtimePoolSignalCount = 0,
    int ObservationPoolSignalCount = 0,
    int PlatformBreakoutAlertCount = 0,
    int PlatformBreakoutConfirmedCount = 0);
