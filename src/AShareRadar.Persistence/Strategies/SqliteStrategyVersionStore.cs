using System.Globalization;
using AShareRadar.Application.Strategies;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.Strategies;

public sealed class SqliteStrategyVersionStore : IStrategyVersionStore
{
    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqliteStrategyVersionStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public StrategyVersion UpsertActiveVersion(StrategyVersion version)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = GetByCodeAndVersion(connection, version.StrategyCode, version.Version);
            if (existing is not null)
            {
                ActivateVersion(connection, transaction, existing);
                transaction.Commit();
                return existing with { Status = "Active", DeactivatedAt = null };
            }

            DeactivateCurrentVersions(connection, transaction, version.StrategyCode, version.ActivatedAt ?? DateTimeOffset.Now);
            InsertVersion(connection, transaction, version);
            transaction.Commit();
            return version;
        }
    }

    public IReadOnlyList<StrategyVersion> QueryVersions(string? strategyCode = null)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            var where = string.Empty;
            if (!string.IsNullOrWhiteSpace(strategyCode))
            {
                where = "WHERE strategy_code = $strategy_code";
                Add(command, "$strategy_code", strategyCode.Trim());
            }

            command.CommandText = $"""
                SELECT id, strategy_code, strategy_name, version, status, rule_summary,
                       parameter_json, data_requirement_json, definition_hash, created_at,
                       activated_at, deactivated_at, source
                FROM strategy_versions
                {where}
                ORDER BY strategy_code ASC, activated_at DESC, created_at DESC;
                """;

            using var reader = command.ExecuteReader();
            var items = new List<StrategyVersion>();
            while (reader.Read())
            {
                items.Add(ReadVersion(reader));
            }

            return items;
        }
    }

    public void SaveHitVersions(IReadOnlyList<StrategyHitVersion> hitVersions)
    {
        if (hitVersions.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var item in hitVersions)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO strategy_hit_versions(
                        event_id, strategy_code, strategy_version_id, version,
                        parameter_json, rule_summary, created_at)
                    VALUES(
                        $event_id, $strategy_code, $strategy_version_id, $version,
                        $parameter_json, $rule_summary, $created_at)
                    ON CONFLICT(event_id, strategy_code) DO UPDATE SET
                        strategy_version_id = excluded.strategy_version_id,
                        version = excluded.version,
                        parameter_json = excluded.parameter_json,
                        rule_summary = excluded.rule_summary,
                        created_at = excluded.created_at;
                    """;
                Add(command, "$event_id", item.EventId.ToString());
                Add(command, "$strategy_code", item.StrategyCode);
                Add(command, "$strategy_version_id", item.StrategyVersionId);
                Add(command, "$version", item.Version);
                Add(command, "$parameter_json", item.ParameterJson);
                Add(command, "$rule_summary", item.RuleSummary);
                Add(command, "$created_at", FormatDateTime(item.CreatedAt));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<StrategyHitVersion> GetHitVersions(Guid eventId)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id, strategy_code, strategy_version_id, version,
                       parameter_json, rule_summary, created_at
                FROM strategy_hit_versions
                WHERE event_id = $event_id
                ORDER BY strategy_code ASC;
                """;
            Add(command, "$event_id", eventId.ToString());
            using var reader = command.ExecuteReader();
            var items = new List<StrategyHitVersion>();
            while (reader.Read())
            {
                items.Add(ReadHitVersion(reader));
            }

            return items;
        }
    }

    private static StrategyVersion? GetByCodeAndVersion(SqliteConnection connection, string strategyCode, string version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, strategy_code, strategy_name, version, status, rule_summary,
                   parameter_json, data_requirement_json, definition_hash, created_at,
                   activated_at, deactivated_at, source
            FROM strategy_versions
            WHERE strategy_code = $strategy_code AND version = $version
            LIMIT 1;
            """;
        Add(command, "$strategy_code", strategyCode);
        Add(command, "$version", version);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadVersion(reader) : null;
    }

    private static void InsertVersion(SqliteConnection connection, SqliteTransaction transaction, StrategyVersion version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO strategy_versions(
                id, strategy_code, strategy_name, version, status, rule_summary,
                parameter_json, data_requirement_json, definition_hash, created_at,
                activated_at, deactivated_at, source)
            VALUES(
                $id, $strategy_code, $strategy_name, $version, $status, $rule_summary,
                $parameter_json, $data_requirement_json, $definition_hash, $created_at,
                $activated_at, $deactivated_at, $source);
            """;
        Add(command, "$id", version.Id);
        Add(command, "$strategy_code", version.StrategyCode);
        Add(command, "$strategy_name", version.StrategyName);
        Add(command, "$version", version.Version);
        Add(command, "$status", version.Status);
        Add(command, "$rule_summary", version.RuleSummary);
        Add(command, "$parameter_json", version.ParameterJson);
        Add(command, "$data_requirement_json", version.DataRequirementJson);
        Add(command, "$definition_hash", version.DefinitionHash);
        Add(command, "$created_at", FormatDateTime(version.CreatedAt));
        Add(command, "$activated_at", version.ActivatedAt.HasValue ? FormatDateTime(version.ActivatedAt.Value) : null);
        Add(command, "$deactivated_at", version.DeactivatedAt.HasValue ? FormatDateTime(version.DeactivatedAt.Value) : null);
        Add(command, "$source", version.Source);
        command.ExecuteNonQuery();
    }

    private static void ActivateVersion(SqliteConnection connection, SqliteTransaction transaction, StrategyVersion version)
    {
        DeactivateCurrentVersions(connection, transaction, version.StrategyCode, DateTimeOffset.Now, version.Id);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE strategy_versions
            SET status = 'Active',
                activated_at = COALESCE(activated_at, $activated_at),
                deactivated_at = NULL
            WHERE id = $id;
            """;
        Add(command, "$id", version.Id);
        Add(command, "$activated_at", FormatDateTime(DateTimeOffset.Now));
        command.ExecuteNonQuery();
    }

    private static void DeactivateCurrentVersions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string strategyCode,
        DateTimeOffset deactivatedAt,
        string? exceptId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE strategy_versions
            SET status = 'Inactive',
                deactivated_at = $deactivated_at
            WHERE strategy_code = $strategy_code
              AND status = 'Active'
              AND ($except_id IS NULL OR id <> $except_id);
            """;
        Add(command, "$strategy_code", strategyCode);
        Add(command, "$deactivated_at", FormatDateTime(deactivatedAt));
        Add(command, "$except_id", exceptId);
        command.ExecuteNonQuery();
    }

    private static StrategyVersion ReadVersion(SqliteDataReader reader)
    {
        return new StrategyVersion(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            ParseDateTime(reader.GetString(9)),
            reader.IsDBNull(10) ? null : ParseDateTime(reader.GetString(10)),
            reader.IsDBNull(11) ? null : ParseDateTime(reader.GetString(11)),
            reader.GetString(12));
    }

    private static StrategyHitVersion ReadHitVersion(SqliteDataReader reader)
    {
        return new StrategyHitVersion(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ParseDateTime(reader.GetString(6)));
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string FormatDateTime(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTime(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
