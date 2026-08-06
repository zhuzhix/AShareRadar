namespace AShareRadar.Contracts.MarketData;

public sealed record IndicatorPointDto(
    DateTime TradingTime,
    decimal? Value1,
    decimal? Value2,
    decimal? Value3,
    decimal? BarValue);
