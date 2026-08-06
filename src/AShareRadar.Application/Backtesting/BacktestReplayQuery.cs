namespace AShareRadar.Application.Backtesting;

public sealed record BacktestReplayQuery(
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string>? StrategyCodes,
    int LookbackDays,
    string StockPool,
    int MaxSymbols);
