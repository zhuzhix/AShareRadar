namespace AShareRadar.Contracts.MarketData;

public sealed record MarketMappingSyncRequest(
    string Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MarketMappingRowDto> SectorMappings,
    IReadOnlyList<MarketMappingRowDto> ConceptMappings);

public sealed record MarketMappingRowDto(string Symbol, string Code, string Name);

public sealed record MarketMappingSyncResult(
    bool Success,
    string Version,
    int SectorRows,
    int ConceptRows,
    string Message,
    string? Error = null);
