namespace AShareRadar.Contracts.MarketData;

public sealed record HeatSnapshotOverviewDto(
    string Id,
    DateTimeOffset SnapshotTime,
    DateOnly TradeDate,
    string? SectorMappingBatchId,
    string? ConceptMappingBatchId,
    int SectorCount,
    int ConceptCount,
    IReadOnlyList<HeatSnapshotItemDto> Sectors,
    IReadOnlyList<HeatSnapshotItemDto> Concepts);

public sealed record HeatSnapshotItemDto(
    string Code,
    string Name,
    int HeatRank,
    int StockCount,
    int RisingCount,
    decimal AverageChangePercent,
    decimal RisingRatioPercent,
    decimal TotalAmount,
    decimal HeatScore,
    IReadOnlyList<HeatLeaderDto> Leaders,
    IReadOnlyList<string> LeaderSymbols);

public sealed record MappingSnapshotBatchDto(
    string Id,
    string MappingType,
    DateTimeOffset SnapshotTime,
    DateOnly TradeDate,
    string Source,
    int ItemCount,
    string FileHash,
    DateTimeOffset CreatedAt);
