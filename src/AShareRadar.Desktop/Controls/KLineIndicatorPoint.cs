namespace AShareRadar.Desktop.Controls;

public sealed record KLineIndicatorPoint(
    DateTime TradingTime,
    decimal? Value1,
    decimal? Value2,
    decimal? Value3,
    decimal? BarValue);
