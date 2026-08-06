namespace AShareRadar.Desktop.Controls;

public sealed record KLineIndicatorSeries(
    string IndicatorType,
    IReadOnlyList<KLineIndicatorPoint> Points);
