namespace AShareRadar.Application.Qlib;

public sealed class QlibSignalOptions
{
    public bool Enabled { get; init; } = true;

    public string SignalRoot { get; init; } =
        @"C:\Users\Administrator\Documents\QuantResearch\shared_data\qlib_strategy_signals\r013_active_low_amount";

    public string WatchlistFileName { get; init; } = "latest_watchlist.csv";

    public string RebalancePlanFileName { get; init; } = "rebalance_plan.csv";

    public string StrategyCode { get; init; } = "qlib-r013";

    public string StrategyName { get; init; } = "\u4F4E\u4F4D\u661F\u706B\u7B56\u7565";

    public int MaxSignalAgeDays { get; init; } = 5;

    public int TopK { get; init; } = 55;

    public int CandidateTopK { get; init; } = 20;

    public int ConfirmTopK { get; init; } = 10;

    public decimal MinRealtimeAmount { get; init; } = 30_000_000m;

    public decimal ConfirmRealtimeAmount { get; init; } = 50_000_000m;

    public decimal MaxRealtimeChangePercent { get; init; } = 5.0m;

    public decimal MaxWatchChangePercent { get; init; } = 7.0m;

    public decimal CandidateVolumeRatio { get; init; } = 1.05m;

    public decimal ConfirmVolumeRatio { get; init; } = 1.20m;

    public bool ExcludeSt { get; init; } = true;

    public bool ExcludeBeijingExchange { get; init; } = true;
}
