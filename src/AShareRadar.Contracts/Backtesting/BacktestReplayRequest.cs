namespace AShareRadar.Contracts.Backtesting;

public sealed record BacktestReplayRequest(
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string>? StrategyCodes,
    int LookbackDays = 80,
    string StockPool = "Manual",
    int MaxSymbols = 20);
