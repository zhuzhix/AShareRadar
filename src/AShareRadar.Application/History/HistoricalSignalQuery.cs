namespace AShareRadar.Application.History;

public sealed record HistoricalSignalQuery(
    DateOnly? TradingDate,
    string? Symbol,
    string? StrategyCode,
    int Count);
