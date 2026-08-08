namespace AShareRadar.Application.MarketData;

public sealed record KLineBar(
    DateTime TradingTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal Amount = 0m,
    decimal? TurnoverRate = null);
