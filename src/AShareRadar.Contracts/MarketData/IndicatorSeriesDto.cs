namespace AShareRadar.Contracts.MarketData;

public sealed record IndicatorSeriesDto(
    string Type,
    IReadOnlyList<IndicatorPointDto> Points);
