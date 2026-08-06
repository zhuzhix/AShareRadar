namespace AShareRadar.Contracts.MarketData;

public sealed record MarketSentimentStatusDto(
    bool IsEnabled,
    bool IsRunning,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    string LastStatus,
    string? LastError);
