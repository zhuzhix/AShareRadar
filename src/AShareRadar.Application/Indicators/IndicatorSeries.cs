namespace AShareRadar.Application.Indicators;

public sealed record IndicatorSeries(
    IndicatorType Type,
    IReadOnlyList<IndicatorPoint> Points);
