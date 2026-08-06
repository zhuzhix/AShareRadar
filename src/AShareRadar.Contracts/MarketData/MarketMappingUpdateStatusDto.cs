namespace AShareRadar.Contracts.MarketData;

public sealed record MarketMappingUpdateStatusDto(
    bool Enabled,
    bool IsRunning,
    DateTime? LastStartedAt,
    DateTime? LastFinishedAt,
    int? LastExitCode,
    string LastTrigger,
    string LastMessage,
    string? LastError,
    int SectorMappingCount,
    int ConceptMappingCount,
    string SectorMappingPath,
    string ConceptMappingPath);
