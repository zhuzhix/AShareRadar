using System.Globalization;
using AShareRadar.Application.MarketData;
using AShareRadar.Application.Review;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.Review;

public sealed class SqliteLongTermTrackingStore : ILongTermTrackingStore
{
    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqliteLongTermTrackingStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public void UpsertSignals(IReadOnlyList<LongTermTrackingSignal> signals)
    {
        if (signals.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var signal in signals)
            {
                UpsertSignal(connection, transaction, signal);
            }

            transaction.Commit();
        }
    }

    public LongTermTrackingBackfillResult Backfill()
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            var signals = new List<LongTermTrackingSignal>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    e.id,
                    e.event_time,
                    e.symbol,
                    e.name,
                    COALESCE(h.strategy_code, e.strategy_code) AS strategy_code,
                    COALESCE(h.strategy_name, e.strategy_name) AS strategy_name,
                    COALESCE(h.score, e.score) AS score,
                    COALESCE(h.price, e.price) AS price,
                    COALESCE(h.reason, e.reason) AS reason,
                    COALESCE(h.risk, e.risk) AS risk
                FROM signal_events e
                LEFT JOIN strategy_hits h ON h.event_id = e.id
                ORDER BY e.event_time ASC;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var strategyCode = reader.GetString(4);
                var strategyName = reader.GetString(5);
                if (!LongTermTrackingService.IsTrackableStrategy(strategyCode, strategyName))
                {
                    continue;
                }

                signals.Add(
                    new LongTermTrackingSignal(
                        Guid.Parse(reader.GetString(0)),
                        DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        reader.GetString(2),
                        reader.GetString(3),
                        strategyCode,
                        strategyName,
                        ParseDecimal(reader.GetString(6)),
                        ReadNullableDecimal(reader, 7),
                        reader.GetString(8),
                        ReadNullableString(reader, 9)));
            }

            reader.Close();

            using var transaction = connection.BeginTransaction();
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM long_term_tracking_items;";
            clear.ExecuteNonQuery();

            foreach (var signal in signals)
            {
                UpsertSignal(connection, transaction, signal);
            }

            transaction.Commit();
            return new LongTermTrackingBackfillResult(DateTimeOffset.Now, CountItems(connection), signals.Count);
        }
    }

    public LongTermTrackingQueryResult Query(LongTermTrackingQuery query)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            var where = BuildWhere(command, query);
            Add(command, "$count", Math.Clamp(query.Count, 1, 5000));
            command.CommandText = $"""
                SELECT
                    id, symbol, name, strategy_code, strategy_name, first_hit_at, last_hit_at,
                    hit_count, latest_price, latest_score, best_score, latest_reason, latest_risk,
                    status, manual_priority, note, tags, latest_event_id, created_at, updated_at
                FROM long_term_tracking_items
                {where}
                ORDER BY {ResolveSortColumn(query.SortBy)} {(query.Descending ? "DESC" : "ASC")}
                LIMIT $count;
                """;

            using var reader = command.ExecuteReader();
            var items = new List<LongTermTrackingItem>();
            while (reader.Read())
            {
                items.Add(ReadItem(reader));
            }

            using var countCommand = connection.CreateCommand();
            var countWhere = BuildWhere(countCommand, query);
            countCommand.CommandText = $"SELECT COUNT(*), MAX(last_hit_at) FROM long_term_tracking_items {countWhere};";
            using var countReader = countCommand.ExecuteReader();
            countReader.Read();
            return new LongTermTrackingQueryResult(
                countReader.GetInt32(0),
                countReader.IsDBNull(1)
                    ? null
                    : DateTimeOffset.Parse(countReader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                items);
        }
    }

    public IReadOnlyList<string> GetActiveTrackingSymbols(int count)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            Add(command, "$count", Math.Clamp(count, 1, 1000));
            command.CommandText = """
                SELECT symbol
                FROM long_term_tracking_items
                WHERE status NOT IN ('GiveUp', 'Archived')
                GROUP BY symbol
                ORDER BY
                    MAX(last_hit_at) DESC,
                    SUM(hit_count) DESC,
                    MAX(CAST(best_score AS REAL)) DESC
                LIMIT $count;
                """;

            using var reader = command.ExecuteReader();
            var symbols = new List<string>();
            while (reader.Read())
            {
                symbols.Add(StockSymbolNormalizer.NormalizeCode(reader.GetString(0)));
            }

            return symbols;
        }
    }

    public IReadOnlyList<LongTermTrackingTimelineItem> QueryTimeline(string symbol, int count)
    {
        var normalized = StockSymbolNormalizer.NormalizeCode(symbol);
        if (normalized.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            Add(command, "$symbol", normalized);
            Add(command, "$count", Math.Clamp(count, 1, 1000));
            command.CommandText = """
                SELECT
                    e.id,
                    e.event_time,
                    e.symbol,
                    e.name,
                    COALESCE(h.strategy_code, e.strategy_code) AS strategy_code,
                    COALESCE(h.strategy_name, e.strategy_name) AS strategy_name,
                    COALESCE(h.score, e.score) AS score,
                    COALESCE(h.price, e.price) AS price,
                    COALESCE(h.reason, e.reason) AS reason,
                    COALESCE(h.risk, e.risk) AS risk
                FROM signal_events e
                LEFT JOIN strategy_hits h ON h.event_id = e.id
                WHERE e.symbol = $symbol
                ORDER BY e.event_time DESC
                LIMIT $count;
                """;

            using var reader = command.ExecuteReader();
            var items = new List<LongTermTrackingTimelineItem>();
            while (reader.Read())
            {
                var strategyCode = reader.GetString(4);
                var strategyName = reader.GetString(5);
                if (!LongTermTrackingService.IsTrackableStrategy(strategyCode, strategyName))
                {
                    continue;
                }

                items.Add(new LongTermTrackingTimelineItem(
                    Guid.Parse(reader.GetString(0)),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetString(2),
                    reader.GetString(3),
                    strategyCode,
                    strategyName,
                    ParseDecimal(reader.GetString(6)),
                    ReadNullableDecimal(reader, 7),
                    reader.GetString(8),
                    ReadNullableString(reader, 9)));
            }

            return items;
        }
    }

    public LongTermTrackingItem? UpdateStatus(Guid id, string status)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            Add(command, "$id", id.ToString());
            Add(command, "$status", status);
            Add(command, "$updated_at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            command.CommandText = """
                UPDATE long_term_tracking_items
                SET status = $status, updated_at = $updated_at
                WHERE id = $id;
                """;
            command.ExecuteNonQuery();
            return GetById(connection, id);
        }
    }

    public LongTermTrackingItem? UpdateNote(Guid id, string? note)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            Add(command, "$id", id.ToString());
            Add(command, "$note", string.IsNullOrWhiteSpace(note) ? null : note.Trim());
            Add(command, "$updated_at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            command.CommandText = """
                UPDATE long_term_tracking_items
                SET note = $note, updated_at = $updated_at
                WHERE id = $id;
                """;
            command.ExecuteNonQuery();
            return GetById(connection, id);
        }
    }

    private static void UpsertSignal(SqliteConnection connection, SqliteTransaction transaction, LongTermTrackingSignal signal)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var now = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        Add(command, "$id", Guid.NewGuid().ToString());
        Add(command, "$symbol", StockSymbolNormalizer.NormalizeCode(signal.Symbol));
        Add(command, "$name", signal.Name);
        Add(command, "$strategy_code", signal.StrategyCode);
        Add(command, "$strategy_name", signal.StrategyName);
        Add(command, "$hit_at", signal.HitTime.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$latest_price", signal.Price.HasValue ? FormatDecimal(signal.Price.Value) : null);
        Add(command, "$latest_score", FormatDecimal(signal.Score));
        Add(command, "$latest_reason", signal.Reason);
        Add(command, "$latest_risk", signal.Risk);
        Add(command, "$latest_event_id", signal.EventId.ToString());
        Add(command, "$now", now);
        command.CommandText = """
            INSERT INTO long_term_tracking_items (
                id, symbol, name, strategy_code, strategy_name, first_hit_at, last_hit_at,
                hit_count, latest_price, latest_score, best_score, latest_reason, latest_risk,
                status, manual_priority, note, tags, latest_event_id, created_at, updated_at)
            VALUES (
                $id, $symbol, $name, $strategy_code, $strategy_name, $hit_at, $hit_at,
                1, $latest_price, $latest_score, $latest_score, $latest_reason, $latest_risk,
                'Watch', 0, NULL, NULL, $latest_event_id, $now, $now)
            ON CONFLICT(symbol, strategy_code) DO UPDATE SET
                name = excluded.name,
                strategy_name = excluded.strategy_name,
                first_hit_at = CASE
                    WHEN excluded.first_hit_at < long_term_tracking_items.first_hit_at THEN excluded.first_hit_at
                    ELSE long_term_tracking_items.first_hit_at
                END,
                last_hit_at = CASE
                    WHEN excluded.last_hit_at > long_term_tracking_items.last_hit_at THEN excluded.last_hit_at
                    ELSE long_term_tracking_items.last_hit_at
                END,
                hit_count = long_term_tracking_items.hit_count + 1,
                latest_price = excluded.latest_price,
                latest_score = excluded.latest_score,
                best_score = CASE
                    WHEN CAST(excluded.best_score AS REAL) > CAST(long_term_tracking_items.best_score AS REAL) THEN excluded.best_score
                    ELSE long_term_tracking_items.best_score
                END,
                latest_reason = excluded.latest_reason,
                latest_risk = excluded.latest_risk,
                latest_event_id = excluded.latest_event_id,
                updated_at = excluded.updated_at;
            """;
        command.ExecuteNonQuery();
    }

    private static string BuildWhere(SqliteCommand command, LongTermTrackingQuery query)
    {
        var where = new List<string>();
        if (query.FromDate.HasValue)
        {
            where.Add("date(last_hit_at) >= $from_date");
            Add(command, "$from_date", query.FromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (query.ToDate.HasValue)
        {
            where.Add("date(last_hit_at) <= $to_date");
            Add(command, "$to_date", query.ToDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(query.Symbol))
        {
            where.Add("symbol = $symbol");
            Add(command, "$symbol", StockSymbolNormalizer.NormalizeCode(query.Symbol));
        }

        if (!string.IsNullOrWhiteSpace(query.StrategyCode))
        {
            where.Add("strategy_code = $strategy_code");
            Add(command, "$strategy_code", query.StrategyCode.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            where.Add("status = $status");
            Add(command, "$status", query.Status.Trim());
        }

        return where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
    }

    private static string ResolveSortColumn(string sortBy)
    {
        return sortBy.Trim() switch
        {
            "FirstHitAt" => "first_hit_at",
            "HitCount" => "hit_count",
            "LatestScore" => "CAST(latest_score AS REAL)",
            "BestScore" => "CAST(best_score AS REAL)",
            "LatestPrice" => "CAST(latest_price AS REAL)",
            "StrategyCode" => "strategy_code",
            "Symbol" => "symbol",
            _ => "last_hit_at"
        };
    }

    private static LongTermTrackingItem? GetById(SqliteConnection connection, Guid id)
    {
        using var command = connection.CreateCommand();
        Add(command, "$id", id.ToString());
        command.CommandText = """
            SELECT
                id, symbol, name, strategy_code, strategy_name, first_hit_at, last_hit_at,
                hit_count, latest_price, latest_score, best_score, latest_reason, latest_risk,
                status, manual_priority, note, tags, latest_event_id, created_at, updated_at
            FROM long_term_tracking_items
            WHERE id = $id;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }

    private static int CountItems(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM long_term_tracking_items;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static LongTermTrackingItem ReadItem(SqliteDataReader reader)
    {
        return new LongTermTrackingItem(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt32(7),
            ReadNullableDecimal(reader, 8),
            ParseDecimal(reader.GetString(9)),
            ParseDecimal(reader.GetString(10)),
            reader.GetString(11),
            ReadNullableString(reader, 12),
            reader.GetString(13),
            reader.GetInt32(14),
            ReadNullableString(reader, 15),
            ReadNullableString(reader, 16),
            reader.IsDBNull(17) ? null : Guid.Parse(reader.GetString(17)),
            DateTimeOffset.Parse(reader.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
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

    private static decimal ParseDecimal(string value)
    {
        return decimal.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
