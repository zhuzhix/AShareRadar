using System.Globalization;
using System.Text.Json;
using AShareRadar.Application.Opportunities.Storage;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.Opportunities;

public sealed class SqliteOpportunityStateStore : IOpportunityStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqliteOpportunityStateStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public OpportunityState Load()
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            var opportunities = LoadOpportunities(connection);
            var hits = LoadStrategyHits(connection);
            var events = LoadEvents(connection, hits);

            return new OpportunityState(opportunities, events);
        }
    }

    public void Save(OpportunityState state)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            Execute(connection, transaction, "DELETE FROM strategy_hits;");
            Execute(connection, transaction, "DELETE FROM signal_events;");
            Execute(connection, transaction, "DELETE FROM opportunities;");

            foreach (var item in state.Opportunities)
            {
                InsertOpportunity(connection, transaction, item);
            }

            foreach (var item in state.Events)
            {
                InsertEvent(connection, transaction, item);
                for (var i = 0; i < item.StrategyHits.Count; i++)
                {
                    InsertStrategyHit(connection, transaction, item.Id, i, item.StrategyHits[i]);
                }
            }

            transaction.Commit();
        }
    }

    private static IReadOnlyList<OpportunityStateItem> LoadOpportunities(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, trading_date, symbol, name, first_seen_time, last_seen_time, status,
                   hit_count, current_score, best_score, manual_tag, note
            FROM opportunities
            ORDER BY last_seen_time DESC;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<OpportunityStateItem>();
        while (reader.Read())
        {
            items.Add(new OpportunityStateItem(
                Guid.Parse(reader.GetString(0)),
                DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(6),
                reader.GetInt32(7),
                ParseDecimal(reader.GetString(8)),
                ParseDecimal(reader.GetString(9)),
                ReadNullableString(reader, 10),
                ReadNullableString(reader, 11)));
        }

        return items;
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<StrategyHitStateItem>> LoadStrategyHits(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, strategy_code, strategy_name, score, price, reason, risk, metrics_json, tags_json,
                   passed_conditions_json, failed_conditions_json, stop_loss_price, take_profit_price
            FROM strategy_hits
            ORDER BY event_id, row_index;
            """;

        using var reader = command.ExecuteReader();
        var groups = new Dictionary<Guid, List<StrategyHitStateItem>>();
        while (reader.Read())
        {
            var eventId = Guid.Parse(reader.GetString(0));
            if (!groups.TryGetValue(eventId, out var items))
            {
                items = [];
                groups[eventId] = items;
            }

            items.Add(new StrategyHitStateItem(
                reader.GetString(1),
                reader.GetString(2),
                ParseDecimal(reader.GetString(3)),
                ReadNullableDecimal(reader, 4),
                reader.GetString(5),
                ReadNullableString(reader, 6),
                ReadJson<Dictionary<string, decimal>>(reader, 7),
                ReadJson<string[]>(reader, 8),
                ReadJson<string[]>(reader, 9),
                ReadJson<string[]>(reader, 10),
                ReadNullableDecimal(reader, 11),
                ReadNullableDecimal(reader, 12)));
        }

        return groups.ToDictionary(item => item.Key, item => (IReadOnlyList<StrategyHitStateItem>)item.Value);
    }

    private static IReadOnlyList<SignalEventStateItem> LoadEvents(
        SqliteConnection connection,
        IReadOnlyDictionary<Guid, IReadOnlyList<StrategyHitStateItem>> hits)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, opportunity_id, run_id, event_time, event_type, symbol, name,
                   strategy_code, strategy_name, score, price, reason, risk
            FROM signal_events
            ORDER BY event_time DESC;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<SignalEventStateItem>();
        while (reader.Read())
        {
            var id = Guid.Parse(reader.GetString(0));
            items.Add(new SignalEventStateItem(
                id,
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                ParseDecimal(reader.GetString(9)),
                ReadNullableDecimal(reader, 10),
                reader.GetString(11),
                ReadNullableString(reader, 12),
                hits.TryGetValue(id, out var eventHits) ? eventHits : []));
        }

        return items;
    }

    private static void InsertOpportunity(SqliteConnection connection, SqliteTransaction transaction, OpportunityStateItem item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO opportunities (
                id, trading_date, symbol, name, first_seen_time, last_seen_time, status,
                hit_count, current_score, best_score, manual_tag, note)
            VALUES (
                $id, $trading_date, $symbol, $name, $first_seen_time, $last_seen_time, $status,
                $hit_count, $current_score, $best_score, $manual_tag, $note);
            """;
        Add(command, "$id", item.Id.ToString());
        Add(command, "$trading_date", item.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "$symbol", item.Symbol);
        Add(command, "$name", item.Name);
        Add(command, "$first_seen_time", item.FirstSeenTime.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$last_seen_time", item.LastSeenTime.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$status", item.Status);
        Add(command, "$hit_count", item.HitCount);
        Add(command, "$current_score", FormatDecimal(item.CurrentScore));
        Add(command, "$best_score", FormatDecimal(item.BestScore));
        Add(command, "$manual_tag", item.ManualTag);
        Add(command, "$note", item.Note);
        command.ExecuteNonQuery();
    }

    private static void InsertEvent(SqliteConnection connection, SqliteTransaction transaction, SignalEventStateItem item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO signal_events (
                id, opportunity_id, run_id, event_time, event_type, symbol, name,
                strategy_code, strategy_name, score, price, reason, risk)
            VALUES (
                $id, $opportunity_id, $run_id, $event_time, $event_type, $symbol, $name,
                $strategy_code, $strategy_name, $score, $price, $reason, $risk);
            """;
        Add(command, "$id", item.Id.ToString());
        Add(command, "$opportunity_id", item.OpportunityId.ToString());
        Add(command, "$run_id", item.RunId.ToString());
        Add(command, "$event_time", item.EventTime.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$event_type", item.EventType);
        Add(command, "$symbol", item.Symbol);
        Add(command, "$name", item.Name);
        Add(command, "$strategy_code", item.StrategyCode);
        Add(command, "$strategy_name", item.StrategyName);
        Add(command, "$score", FormatDecimal(item.Score));
        Add(command, "$price", item.Price.HasValue ? FormatDecimal(item.Price.Value) : null);
        Add(command, "$reason", item.Reason);
        Add(command, "$risk", item.Risk);
        command.ExecuteNonQuery();
    }

    private static void InsertStrategyHit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventId,
        int rowIndex,
        StrategyHitStateItem item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO strategy_hits (
                event_id, row_index, strategy_code, strategy_name, score, price, reason, risk, metrics_json, tags_json,
                passed_conditions_json, failed_conditions_json, stop_loss_price, take_profit_price)
            VALUES (
                $event_id, $row_index, $strategy_code, $strategy_name, $score, $price, $reason, $risk, $metrics_json, $tags_json,
                $passed_conditions_json, $failed_conditions_json, $stop_loss_price, $take_profit_price);
            """;
        Add(command, "$event_id", eventId.ToString());
        Add(command, "$row_index", rowIndex);
        Add(command, "$strategy_code", item.StrategyCode);
        Add(command, "$strategy_name", item.StrategyName);
        Add(command, "$score", FormatDecimal(item.Score));
        Add(command, "$price", item.Price.HasValue ? FormatDecimal(item.Price.Value) : null);
        Add(command, "$reason", item.Reason);
        Add(command, "$risk", item.Risk);
        Add(command, "$metrics_json", item.Metrics is null ? null : JsonSerializer.Serialize(item.Metrics, JsonOptions));
        Add(command, "$tags_json", item.Tags is null ? null : JsonSerializer.Serialize(item.Tags, JsonOptions));
        Add(command, "$passed_conditions_json", item.PassedConditions is null ? null : JsonSerializer.Serialize(item.PassedConditions, JsonOptions));
        Add(command, "$failed_conditions_json", item.FailedConditions is null ? null : JsonSerializer.Serialize(item.FailedConditions, JsonOptions));
        Add(command, "$stop_loss_price", item.StopLossPrice.HasValue ? FormatDecimal(item.StopLossPrice.Value) : null);
        Add(command, "$take_profit_price", item.TakeProfitPrice.HasValue ? FormatDecimal(item.TakeProfitPrice.Value) : null);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ParseDecimal(reader.GetString(ordinal));
    }

    private static T? ReadJson<T>(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(reader.GetString(ordinal), JsonOptions);
        }
        catch
        {
            return default;
        }
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
