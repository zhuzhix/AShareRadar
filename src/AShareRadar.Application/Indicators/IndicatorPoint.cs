namespace AShareRadar.Application.Indicators;

public sealed record IndicatorPoint(
    DateTime TradingTime,
    decimal? Value1,
    decimal? Value2,
    decimal? Value3,
    decimal? BarValue);
