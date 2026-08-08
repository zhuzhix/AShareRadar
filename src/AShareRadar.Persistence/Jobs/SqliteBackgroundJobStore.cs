using AShareRadar.Application.Jobs;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.Jobs;

public sealed class SqliteBackgroundJobStore : IBackgroundJobStore
{
    private readonly SqliteDatabase _database;

    public SqliteBackgroundJobStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public BackgroundJob Create(string type, string title, string payloadJson)
    {
        var job = new BackgroundJob(Guid.NewGuid(), type, title, "Queued", 0, "等待执行", DateTimeOffset.Now, null, null, null, null, null, payloadJson, null);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO background_jobs(
                id, type, title, status, progress_percent, current_step,
                created_at, payload_json)
            VALUES($id, $type, $title, $status, $progress_percent, $current_step, $created_at, $payload_json);
            """;
        Add(command, "$id", job.Id.ToString());
        Add(command, "$type", job.Type);
        Add(command, "$title", job.Title);
        Add(command, "$status", job.Status);
        Add(command, "$progress_percent", job.ProgressPercent);
        Add(command, "$current_step", job.CurrentStep);
        Add(command, "$created_at", job.CreatedAt.ToString("O"));
        Add(command, "$payload_json", job.PayloadJson);
        command.ExecuteNonQuery();
        return job;
    }

    public BackgroundJob? Get(Guid id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM background_jobs WHERE id = $id;";
        Add(command, "$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    public BackgroundJob? GetLatest(string? type)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(type)
            ? "SELECT * FROM background_jobs ORDER BY created_at DESC LIMIT 1;"
            : "SELECT * FROM background_jobs WHERE type = $type ORDER BY created_at DESC LIMIT 1;";
        if (!string.IsNullOrWhiteSpace(type))
        {
            Add(command, "$type", type);
        }

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    public IReadOnlyList<BackgroundJob> GetActive()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM background_jobs
            WHERE status IN ('Queued', 'Running')
            ORDER BY created_at DESC
            LIMIT 20;
            """;
        return ReadJobs(command);
    }

    public IReadOnlyList<BackgroundJob> GetQueued(int count)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM background_jobs
            WHERE status = 'Queued'
            ORDER BY created_at ASC
            LIMIT $count;
            """;
        Add(command, "$count", Math.Clamp(count, 1, 100));
        return ReadJobs(command);
    }

    public IReadOnlyList<BackgroundJobLog> GetLogs(Guid id, int count)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, job_id, created_at, stream, message
            FROM background_job_logs
            WHERE job_id = $job_id
            ORDER BY id DESC
            LIMIT $count;
            """;
        Add(command, "$job_id", id.ToString());
        Add(command, "$count", Math.Clamp(count, 1, 1000));
        var logs = new List<BackgroundJobLog>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            logs.Add(new BackgroundJobLog(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4)));
        }

        logs.Reverse();
        return logs;
    }

    public void MarkRunning(Guid id, string step)
    {
        Execute(
            """
            UPDATE background_jobs
            SET status = 'Running', started_at = COALESCE(started_at, $now),
                progress_percent = CASE WHEN progress_percent < 1 THEN 1 ELSE progress_percent END,
                current_step = $step
            WHERE id = $id;
            """,
            id,
            step,
            null,
            null);
    }

    public void UpdateProgress(Guid id, int progressPercent, string step)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE background_jobs
            SET progress_percent = $progress_percent, current_step = $step
            WHERE id = $id;
            """;
        Add(command, "$id", id.ToString());
        Add(command, "$progress_percent", Math.Clamp(progressPercent, 0, 99));
        Add(command, "$step", step);
        command.ExecuteNonQuery();
    }

    public void AppendLog(Guid id, string stream, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO background_job_logs(job_id, created_at, stream, message)
            VALUES($job_id, $created_at, $stream, $message);
            """;
        Add(command, "$job_id", id.ToString());
        Add(command, "$created_at", DateTimeOffset.Now.ToString("O"));
        Add(command, "$stream", stream);
        Add(command, "$message", message.Trim());
        command.ExecuteNonQuery();
    }

    public void MarkSucceeded(Guid id, string step, string? resultJson = null)
    {
        Execute(
            """
            UPDATE background_jobs
            SET status = 'Succeeded', progress_percent = 100, current_step = $step,
                finished_at = $now, result_json = $result_json
            WHERE id = $id;
            """,
            id,
            step,
            resultJson,
            null);
    }

    public void MarkFailed(Guid id, string step, string errorMessage, string? fixSuggestion, int? exitCode = null)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE background_jobs
            SET status = 'Failed', current_step = $step, finished_at = $now,
                exit_code = $exit_code, error_message = $error_message, fix_suggestion = $fix_suggestion
            WHERE id = $id;
            """;
        Add(command, "$id", id.ToString());
        Add(command, "$step", step);
        Add(command, "$now", DateTimeOffset.Now.ToString("O"));
        Add(command, "$exit_code", exitCode);
        Add(command, "$error_message", errorMessage);
        Add(command, "$fix_suggestion", fixSuggestion);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<BackgroundJob> ReadJobs(SqliteCommand command)
    {
        var jobs = new List<BackgroundJob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    private static BackgroundJob ReadJob(SqliteDataReader reader)
    {
        return new BackgroundJob(
            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            reader.GetString(reader.GetOrdinal("type")),
            reader.GetString(reader.GetOrdinal("title")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetInt32(reader.GetOrdinal("progress_percent")),
            reader.GetString(reader.GetOrdinal("current_step")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            ReadDateTimeOffset(reader, "started_at"),
            ReadDateTimeOffset(reader, "finished_at"),
            reader.IsDBNull(reader.GetOrdinal("exit_code")) ? null : reader.GetInt32(reader.GetOrdinal("exit_code")),
            reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
            reader.IsDBNull(reader.GetOrdinal("fix_suggestion")) ? null : reader.GetString(reader.GetOrdinal("fix_suggestion")),
            reader.GetString(reader.GetOrdinal("payload_json")),
            reader.IsDBNull(reader.GetOrdinal("result_json")) ? null : reader.GetString(reader.GetOrdinal("result_json")));
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }

    private void Execute(string sql, Guid id, string step, string? resultJson, string? errorMessage)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "$id", id.ToString());
        Add(command, "$now", DateTimeOffset.Now.ToString("O"));
        Add(command, "$step", step);
        Add(command, "$result_json", resultJson);
        Add(command, "$error_message", errorMessage);
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
