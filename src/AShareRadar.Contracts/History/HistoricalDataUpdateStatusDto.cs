namespace AShareRadar.Contracts.History;

public sealed record HistoricalDataUpdateStatusDto(
    bool Enabled,
    bool IsRunning,
    DateTime? LastStartedAt,
    DateTime? LastFinishedAt,
    int? LastExitCode,
    string LastTrigger,
    string LastMessage,
    string? LastError,
    DateOnly? LatestTradingDate,
    DateOnly[] MissingTradingDates,
    string RunAfterTime,
    int CheckIntervalMinutes);
