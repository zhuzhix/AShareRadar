namespace AShareRadar.Contracts.Jobs;

public sealed record BackgroundJobDto(
    Guid Id,
    string Type,
    string Title,
    string Status,
    int ProgressPercent,
    string CurrentStep,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? ExitCode,
    string? ErrorMessage,
    string? FixSuggestion,
    string? ResultJson);

public sealed record BackgroundJobLogDto(
    long Id,
    Guid JobId,
    DateTimeOffset CreatedAt,
    string Stream,
    string Message);

public sealed record CreateBackgroundJobResponse(Guid JobId);
