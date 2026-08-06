namespace AShareRadar.Application.Monitoring;

public sealed class MonitorRuntimeState
{
    private readonly object _gate = new();

    private MonitorRuntimeStatus _status = new(
        MarketStatus: "Unknown",
        MonitorStatus: "NotStarted",
        LastScanTime: null,
        NextScanTime: null,
        ActiveOpportunityCount: 0,
        TodayNewCount: 0,
        DisappearedCount: 0,
        FocusedCount: 0);

    public int ScanIntervalSeconds { get; private set; } = 30;

    public MonitorRuntimeStatus GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public void Start(int scanIntervalSeconds)
    {
        lock (_gate)
        {
            ScanIntervalSeconds = Math.Clamp(scanIntervalSeconds, 5, 300);
            _status = _status with
            {
                MonitorStatus = "Running",
                NextScanTime = DateTimeOffset.Now.AddSeconds(ScanIntervalSeconds)
            };
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _status = _status with
            {
                MonitorStatus = "Paused",
                NextScanTime = null
            };
        }
    }

    public void SetMarketStatus(string marketStatus)
    {
        lock (_gate)
        {
            _status = _status with { MarketStatus = marketStatus };
        }
    }

    public void MarkScanning()
    {
        lock (_gate)
        {
            _status = _status with
            {
                MonitorStatus = "Scanning"
            };
        }
    }

    public void ApplyScanResult(
        DateTimeOffset scanTime,
        int activeOpportunityCount,
        int todayNewCount,
        int disappearedCount,
        int focusedCount)
    {
        lock (_gate)
        {
            _status = _status with
            {
                MonitorStatus = "Running",
                LastScanTime = scanTime,
                NextScanTime = scanTime.AddSeconds(ScanIntervalSeconds),
                ActiveOpportunityCount = activeOpportunityCount,
                TodayNewCount = todayNewCount,
                DisappearedCount = disappearedCount,
                FocusedCount = focusedCount
            };
        }
    }

    public void MarkHistoricalStrategyScanning()
    {
        lock (_gate)
        {
            _status = _status with
            {
                HistoricalStrategyScanStatus = "Scanning"
            };
        }
    }

    public void ApplyHistoricalStrategyScanResult(
        DateTimeOffset scanTime,
        DateTimeOffset? nextScanTime,
        int scannedSymbolCount,
        int signalCount)
    {
        lock (_gate)
        {
            _status = _status with
            {
                HistoricalStrategyScanStatus = "Running",
                LastHistoricalStrategyScanTime = scanTime,
                NextHistoricalStrategyScanTime = nextScanTime,
                HistoricalStrategyScanSymbolCount = scannedSymbolCount,
                HistoricalStrategyScanSignalCount = signalCount
            };
        }
    }

    public void MarkHistoricalStrategyScanFailed(DateTimeOffset? nextScanTime)
    {
        lock (_gate)
        {
            _status = _status with
            {
                HistoricalStrategyScanStatus = "Failed",
                NextHistoricalStrategyScanTime = nextScanTime
            };
        }
    }
}
