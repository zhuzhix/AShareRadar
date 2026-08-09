namespace AShareRadar.Contracts.MarketData;

public sealed record MappingBoardItemDto(
    string Code,
    string Name,
    int StockCount,
    int Rank);
