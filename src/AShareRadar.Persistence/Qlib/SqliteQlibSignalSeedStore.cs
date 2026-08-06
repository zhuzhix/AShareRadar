using System.Globalization;
using AShareRadar.Application.Qlib;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.Qlib;

public sealed class SqliteQlibSignalSeedStore : IQlibSignalSeedStore
{
    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqliteQlibSignalSeedStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public QlibSignalSeedImportResult ImportSnapshot(QlibSignalSnapshot snapshot)
    {
        var importedAt = DateTimeOffset.Now;
        var seeds = snapshot.Records
            .Select(item => new QlibSignalSeed(
                Guid.NewGuid(),
                item.SignalDate,
                item.Code,
                item.Symbol,
                item.Exchange,
                item.Name,
                item.PredScore,
                item.RankTotal,
                item.ModelRank,
                item.ModelScore100,
                item.TargetWeight,
                item.Action,
                item.Confidence,
                item.StrategyCode,
                item.StrategyName,
                item.SourceExperimentId,
                item.Reason,
                item.Risk,
                importedAt))
            .ToArray();

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM qlib_signal_seeds
                    WHERE signal_date = $signal_date
                      AND strategy_code = $strategy_code;
                    """;
                Add(delete, "$signal_date", snapshot.SignalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(delete, "$strategy_code", snapshot.StrategyCode);
                delete.ExecuteNonQuery();
            }

            foreach (var seed in seeds)
            {
                Insert(connection, transaction, seed);
            }

            transaction.Commit();
        }

        return new QlibSignalSeedImportResult(
            importedAt,
            snapshot.SignalDate,
            snapshot.StrategyCode,
            snapshot.StrategyName,
            snapshot.SourceExperimentId,
            seeds.Length,
            seeds);
    }

    public IReadOnlyList<QlibSignalSeed> Query(DateOnly? signalDate, string? strategyCode, int count)
    {
        var take = Math.Clamp(count, 1, 1000);
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            var filters = new List<string>();
            if (signalDate.HasValue)
            {
                filters.Add("signal_date = $signal_date");
                Add(command, "$signal_date", signalDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(strategyCode))
            {
                filters.Add("strategy_code = $strategy_code");
                Add(command, "$strategy_code", strategyCode);
            }

            Add(command, "$take", take);
            command.CommandText = $"""
                SELECT id, signal_date, code, symbol, exchange, name, pred_score, rank_total, model_rank,
                       model_score_100, target_weight, action, confidence, strategy_code, strategy_name,
                       source_experiment_id, reason, risk, imported_at
                FROM qlib_signal_seeds
                {(filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters))}
                ORDER BY signal_date DESC, strategy_code, model_rank
                LIMIT $take;
                """;

            using var reader = command.ExecuteReader();
            var result = new List<QlibSignalSeed>();
            while (reader.Read())
            {
                result.Add(ReadSeed(reader));
            }

            return result;
        }
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, QlibSignalSeed seed)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO qlib_signal_seeds (
                id, signal_date, code, symbol, exchange, name, pred_score, rank_total, model_rank,
                model_score_100, target_weight, action, confidence, strategy_code, strategy_name,
                source_experiment_id, reason, risk, imported_at)
            VALUES (
                $id, $signal_date, $code, $symbol, $exchange, $name, $pred_score, $rank_total, $model_rank,
                $model_score_100, $target_weight, $action, $confidence, $strategy_code, $strategy_name,
                $source_experiment_id, $reason, $risk, $imported_at);
            """;
        Add(command, "$id", seed.Id.ToString());
        Add(command, "$signal_date", seed.SignalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "$code", seed.Code);
        Add(command, "$symbol", seed.Symbol);
        Add(command, "$exchange", seed.Exchange);
        Add(command, "$name", seed.Name);
        Add(command, "$pred_score", FormatDecimal(seed.PredScore));
        Add(command, "$rank_total", seed.RankTotal);
        Add(command, "$model_rank", seed.ModelRank);
        Add(command, "$model_score_100", FormatDecimal(seed.ModelScore100));
        Add(command, "$target_weight", FormatDecimal(seed.TargetWeight));
        Add(command, "$action", seed.Action);
        Add(command, "$confidence", seed.Confidence);
        Add(command, "$strategy_code", seed.StrategyCode);
        Add(command, "$strategy_name", seed.StrategyName);
        Add(command, "$source_experiment_id", seed.SourceExperimentId);
        Add(command, "$reason", seed.Reason);
        Add(command, "$risk", seed.Risk);
        Add(command, "$imported_at", seed.ImportedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static QlibSignalSeed ReadSeed(SqliteDataReader reader)
    {
        return new QlibSignalSeed(
            Guid.Parse(reader.GetString(0)),
            DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ParseDecimal(reader.GetString(6)),
            reader.GetInt32(7),
            reader.GetInt32(8),
            ParseDecimal(reader.GetString(9)),
            ParseDecimal(reader.GetString(10)),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            DateTimeOffset.Parse(reader.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static decimal ParseDecimal(string value)
    {
        return decimal.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}