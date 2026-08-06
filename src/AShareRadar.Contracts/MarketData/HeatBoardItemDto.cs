namespace AShareRadar.Contracts.MarketData;

public sealed record HeatBoardItemDto(
    string Code,
    string Name,
    int StockCount,
    int RisingCount,
    decimal AverageChangePercent,
    decimal RisingRatioPercent,
    decimal TotalAmount,
    decimal HeatScore,
    IReadOnlyList<HeatLeaderDto> Leaders,
    IReadOnlyList<string> LeaderSymbols);
