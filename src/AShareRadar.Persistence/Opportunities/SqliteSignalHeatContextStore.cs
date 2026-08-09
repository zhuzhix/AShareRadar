using System.Globalization;
using AShareRadar.Application.Opportunities;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.Opportunities;

public sealed class SqliteSignalHeatContextStore : ISignalHeatContextStore
{
    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqliteSignalHeatContextStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public void SaveContexts(IReadOnlyList<SignalHeatContext> contexts)
    {
        if (contexts.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            foreach (var eventId in contexts.Select(item => item.EventId).Distinct())
            {
                using var deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM signal_heat_contexts WHERE event_id = $event_id;";
                Add(deleteCommand, "$event_id", eventId.ToString());
                deleteCommand.ExecuteNonQuery();
            }

            foreach (var group in contexts.GroupBy(item => item.EventId))
            {
                var rowIndex = 0;
                foreach (var item in group)
                {
                    InsertContext(connection, transaction, rowIndex++, item);
                }
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<SignalHeatContext> GetByEventId(Guid eventId)
    {
        return GetByEventIds([eventId]).GetValueOrDefault(eventId, []);
    }

    public IReadOnlyDictionary<Guid, IReadOnlyList<SignalHeatContext>> GetByEventIds(IEnumerable<Guid> eventIds)
    {
        var ids = eventIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<SignalHeatContext>>();
        }

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            var result = new Dictionary<Guid, List<SignalHeatContext>>();
            foreach (var eventId in ids)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT event_id, symbol, event_time, context_type, code, name, heat_rank,
                           stock_count, rising_count, average_change_percent, rising_ratio_percent,
                           total_amount, heat_score, is_leader, heat_snapshot_batch_id, created_at
                    FROM signal_heat_contexts
                    WHERE event_id = $event_id
                    ORDER BY row_index ASC;
                    """;
                Add(command, "$event_id", eventId.ToString());
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var item = ReadContext(reader);
                    if (!result.TryGetValue(item.EventId, out var contexts))
                    {
                        contexts = [];
                        result[item.EventId] = contexts;
                    }

                    contexts.Add(item);
                }
            }

            return result.ToDictionary(
                item => item.Key,
                item => (IReadOnlyList<SignalHeatContext>)item.Value);
        }
    }

    private static void InsertContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int rowIndex,
        SignalHeatContext item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO signal_heat_contexts(
                event_id, row_index, symbol, event_time, context_type, code, name,
                heat_rank, stock_count, rising_count, average_change_percent,
                rising_ratio_percent, total_amount, heat_score, is_leader,
                heat_snapshot_batch_id, created_at)
            VALUES(
                $event_id, $row_index, $symbol, $event_time, $context_type, $code, $name,
                $heat_rank, $stock_count, $rising_count, $average_change_percent,
                $rising_ratio_percent, $total_amount, $heat_score, $is_leader,
                $heat_snapshot_batch_id, $created_at);
            """;
        Add(command, "$event_id", item.EventId.ToString());
        Add(command, "$row_index", rowIndex);
        Add(command, "$symbol", item.Symbol);
        Add(command, "$event_time", FormatDateTime(item.EventTime));
        Add(command, "$context_type", item.ContextType);
        Add(command, "$code", item.Code);
        Add(command, "$name", item.Name);
        Add(command, "$heat_rank", item.HeatRank);
        Add(command, "$stock_count", item.StockCount);
        Add(command, "$rising_count", item.RisingCount);
        Add(command, "$average_change_percent", FormatDecimal(item.AverageChangePercent));
        Add(command, "$rising_ratio_percent", FormatDecimal(item.RisingRatioPercent));
        Add(command, "$total_amount", FormatDecimal(item.TotalAmount));
        Add(command, "$heat_score", FormatDecimal(item.HeatScore));
        Add(command, "$is_leader", item.IsLeader ? 1 : 0);
        Add(command, "$heat_snapshot_batch_id", item.HeatSnapshotBatchId);
        Add(command, "$created_at", FormatDateTime(item.CreatedAt));
        command.ExecuteNonQuery();
    }

    private static SignalHeatContext ReadContext(SqliteDataReader reader)
    {
        return new SignalHeatContext(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            ParseDateTime(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            ParseDecimal(reader.GetString(9)),
            ParseDecimal(reader.GetString(10)),
            ParseDecimal(reader.GetString(11)),
            ParseDecimal(reader.GetString(12)),
            reader.GetInt32(13) == 1,
            reader.IsDBNull(14) ? null : reader.GetString(14),
            ParseDateTime(reader.GetString(15)));
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

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static decimal ParseDecimal(string value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }
}
