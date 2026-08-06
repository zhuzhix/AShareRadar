namespace AShareRadar.Application.MarketData;

public sealed record KLineBar(
    DateTime TradingTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
