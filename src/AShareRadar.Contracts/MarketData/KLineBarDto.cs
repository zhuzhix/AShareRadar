namespace AShareRadar.Contracts.MarketData;

public sealed record KLineBarDto(
    DateTime TradingTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
