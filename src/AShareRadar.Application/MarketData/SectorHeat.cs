using AShareRadar.Domain.MarketData;

namespace AShareRadar.Application.MarketData;

public sealed record SectorMembership(
    string Symbol,
    string SectorCode,
    string SectorName,
    string Source = "Fallback");

public sealed record SectorHeat(
    string SectorCode,
    string SectorName,
    int StockCount,
    int RisingCount,
    decimal AverageChangePercent,
    decimal RisingRatioPercent,
    decimal TotalAmount,
    decimal HeatScore,
    IReadOnlyList<HeatLeader> Leaders,
    IReadOnlyList<string> LeaderSymbols);

public sealed record SectorHeatSnapshot(
    DateTimeOffset SnapshotTime,
    IReadOnlyDictionary<string, SectorHeat> SectorsByCode,
    IReadOnlyDictionary<string, SectorMembership> MembershipBySymbol,
    IReadOnlyDictionary<string, SectorHeat> HeatBySymbol);

public sealed record ConceptMembership(
    string Symbol,
    string ConceptCode,
    string ConceptName,
    string Source = "CsvMapping");

public sealed record ConceptHeat(
    string ConceptCode,
    string ConceptName,
    int StockCount,
    int RisingCount,
    decimal AverageChangePercent,
    decimal RisingRatioPercent,
    decimal TotalAmount,
    decimal HeatScore,
    IReadOnlyList<HeatLeader> Leaders,
    IReadOnlyList<string> LeaderSymbols);

public sealed record ConceptHeatSnapshot(
    DateTimeOffset SnapshotTime,
    IReadOnlyDictionary<string, ConceptHeat> ConceptsByCode,
    IReadOnlyDictionary<string, IReadOnlyList<ConceptMembership>> MembershipsBySymbol,
    IReadOnlyDictionary<string, IReadOnlyList<ConceptHeat>> HeatBySymbol);

public sealed record HeatLeader(
    int Rank,
    string Symbol,
    string Name,
    decimal ChangePercent,
    decimal Amount,
    decimal VolumeRatio);

public interface ISectorHeatService
{
    SectorHeatSnapshot Build(MarketSnapshot snapshot);

    SectorHeatMappingStatus GetMappingStatus();

    ConceptHeatSnapshot BuildConcepts(MarketSnapshot snapshot);

    SectorHeatMappingStatus GetConceptMappingStatus();

    void ReloadMappings();
}

public sealed record SectorHeatMappingStatus(
    string MappingPath,
    int MappingCount,
    DateTimeOffset? LoadedTime,
    string Source);
