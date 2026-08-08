namespace AShareRadar.Application.Jobs;

public sealed record BackgroundJob(
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
    string PayloadJson,
    string? ResultJson);

public sealed record BackgroundJobLog(
    long Id,
    Guid JobId,
    DateTimeOffset CreatedAt,
    string Stream,
    string Message);

public interface IBackgroundJobStore
{
    BackgroundJob Create(string type, string title, string payloadJson);

    BackgroundJob? Get(Guid id);

    BackgroundJob? GetLatest(string? type);

    IReadOnlyList<BackgroundJob> GetActive();

    IReadOnlyList<BackgroundJob> GetQueued(int count);

    IReadOnlyList<BackgroundJobLog> GetLogs(Guid id, int count);

    void MarkRunning(Guid id, string step);

    void UpdateProgress(Guid id, int progressPercent, string step);

    void AppendLog(Guid id, string stream, string message);

    void MarkSucceeded(Guid id, string step, string? resultJson = null);

    void MarkFailed(Guid id, string step, string errorMessage, string? fixSuggestion, int? exitCode = null);
}
