namespace AShareRadar.Desktop.Controls;

public sealed record KLineCandle(
    DateTime TradingTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal Amount = 0m,
    decimal? TurnoverRate = null);
