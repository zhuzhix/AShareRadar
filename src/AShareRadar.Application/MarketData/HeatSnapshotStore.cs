namespace AShareRadar.Application.MarketData;

public interface IHeatSnapshotStore
{
    MappingSnapshotBatch SaveMappingSnapshot(
        string mappingType,
        DateTimeOffset snapshotTime,
        string source,
        IReadOnlyList<MappingSnapshotItem> items);

    MappingSnapshotBatch? GetLatestMappingSnapshot(string mappingType);

    HeatSnapshotSaveResult SaveHeatSnapshot(
        DateOnly tradeDate,
        SectorHeatSnapshot sectorSnapshot,
        ConceptHeatSnapshot conceptSnapshot,
        TimeSpan minimumInterval);

    HeatSnapshotOverview? GetLatestHeatSnapshot(int sectorCount, int conceptCount);

    HeatSnapshotOverview? GetHeatSnapshotAt(DateTimeOffset snapshotTime, int sectorCount, int conceptCount);
}

public sealed record MappingSnapshotBatch(
    string Id,
    string MappingType,
    DateTimeOffset SnapshotTime,
    DateOnly TradeDate,
    string Source,
    int ItemCount,
    string FileHash,
    DateTimeOffset CreatedAt);

public sealed record MappingSnapshotItem(
    string BoardCode,
    string BoardName,
    int BoardRank,
    string Symbol,
    string? StockName,
    string Source);

public sealed record HeatSnapshotSaveResult(
    bool Saved,
    string? BatchId,
    DateTimeOffset? LastSavedAt,
    string Reason);

public sealed record HeatSnapshotOverview(
    string Id,
    DateTimeOffset SnapshotTime,
    DateOnly TradeDate,
    string? SectorMappingBatchId,
    string? ConceptMappingBatchId,
    int SectorCount,
    int ConceptCount,
    IReadOnlyList<HeatSnapshotItem> Sectors,
    IReadOnlyList<HeatSnapshotItem> Concepts);

public sealed record HeatSnapshotItem(
    string Code,
    string Name,
    int HeatRank,
    int StockCount,
    int RisingCount,
    decimal AverageChangePercent,
    decimal RisingRatioPercent,
    decimal TotalAmount,
    decimal HeatScore,
    IReadOnlyList<HeatLeader> Leaders,
    IReadOnlyList<string> LeaderSymbols);
