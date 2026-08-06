namespace AShareRadar.Application.MarketData;

public sealed class TradingSessionOptions
{
    public bool AutoMonitorEnabled { get; set; } = true;

    public int AutoScanIntervalSeconds { get; set; } = 30;

    public int SchedulerPollSeconds { get; set; } = 5;

    public string CallAuctionStartTime { get; set; } = "09:15";

    public string MorningStartTime { get; set; } = "09:30";

    public string MorningEndTime { get; set; } = "11:30";

    public string AfternoonStartTime { get; set; } = "13:00";

    public string AfternoonEndTime { get; set; } = "15:00";
}
