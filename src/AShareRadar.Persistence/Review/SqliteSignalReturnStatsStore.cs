using System.Globalization;
using AShareRadar.Application.MarketData;
using AShareRadar.Application.Review;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.Review;

public sealed class SqliteSignalReturnStatsStore : ISignalReturnStatsStore
{
    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqliteSignalReturnStatsStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public IReadOnlyList<SignalReturnSource> QuerySignalSources(SignalReturnQuery query)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            var where = BuildSignalSourceWhere(command, query);
            Add(command, "$count", Math.Clamp(query.Count <= 0 ? 1000 : query.Count, 1, 10000));
            command.CommandText = $"""
                SELECT
                    e.id,
                    e.opportunity_id,
                    e.event_time,
                    e.symbol,
                    e.name,
                    COALESCE(h.strategy_code, e.strategy_code) AS strategy_code,
                    COALESCE(h.strategy_name, e.strategy_name) AS strategy_name,
                    COALESCE(h.score, e.score) AS score,
                    COALESCE(h.price, e.price) AS price,
                    v.strategy_version_id,
                    v.version
                FROM signal_events e
                LEFT JOIN strategy_hits h ON h.event_id = e.id
                LEFT JOIN strategy_hit_versions v
                    ON v.event_id = e.id
                   AND v.strategy_code = COALESCE(h.strategy_code, e.strategy_code)
                {where}
                ORDER BY e.event_time DESC
                LIMIT $count;
                """;

            using var reader = command.ExecuteReader();
            var items = new List<SignalReturnSource>();
            while (reader.Read())
            {
                var strategyCode = reader.GetString(5);
                if (!MatchesStrategyGroup(strategyCode, query.StrategyGroup))
                {
                    continue;
                }

                items.Add(new SignalReturnSource(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    ParseDateTime(reader.GetString(2)),
                    StockSymbolNormalizer.NormalizeCode(reader.GetString(3)),
                    reader.GetString(4),
                    strategyCode,
                    reader.GetString(6),
                    ParseDecimal(reader.GetString(7)),
                    ReadNullableDecimal(reader, 8),
                    ReadNullableString(reader, 9),
                    ReadNullableString(reader, 10)));
            }

            return items;
        }
    }

    public void UpsertRecords(IReadOnlyList<SignalReturnRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var item in records)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO signal_return_records(
                        event_id, opportunity_id, event_time, signal_date, symbol, name,
                        strategy_code, strategy_name, strategy_group, strategy_version_id, strategy_version,
                        score, signal_price, entry_price,
                        horizon_code, horizon_name, trading_days, horizon_group, target_date, target_close,
                        return_percent, max_return_percent, min_return_percent, status, calculated_at, updated_at)
                    VALUES(
                        $event_id, $opportunity_id, $event_time, $signal_date, $symbol, $name,
                        $strategy_code, $strategy_name, $strategy_group, $strategy_version_id, $strategy_version,
                        $score, $signal_price, $entry_price,
                        $horizon_code, $horizon_name, $trading_days, $horizon_group, $target_date, $target_close,
                        $return_percent, $max_return_percent, $min_return_percent, $status, $calculated_at, $updated_at)
                    ON CONFLICT(event_id, strategy_code, horizon_code) DO UPDATE SET
                        opportunity_id = excluded.opportunity_id,
                        event_time = excluded.event_time,
                        signal_date = excluded.signal_date,
                        symbol = excluded.symbol,
                        name = excluded.name,
                        strategy_name = excluded.strategy_name,
                        strategy_group = excluded.strategy_group,
                        strategy_version_id = excluded.strategy_version_id,
                        strategy_version = excluded.strategy_version,
                        score = excluded.score,
                        signal_price = excluded.signal_price,
                        entry_price = excluded.entry_price,
                        horizon_name = excluded.horizon_name,
                        trading_days = excluded.trading_days,
                        horizon_group = excluded.horizon_group,
                        target_date = excluded.target_date,
                        target_close = excluded.target_close,
                        return_percent = excluded.return_percent,
                        max_return_percent = excluded.max_return_percent,
                        min_return_percent = excluded.min_return_percent,
                        status = excluded.status,
                        updated_at = excluded.updated_at;
                    """;
                AddRecordParameters(command, item);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public SignalReturnQueryResult QueryRecords(SignalReturnQuery query)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var countCommand = connection.CreateCommand();
            var where = BuildReturnRecordWhere(countCommand, query);
            countCommand.CommandText = $"SELECT COUNT(*) FROM signal_return_records {where};";
            var totalCount = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var command = connection.CreateCommand();
            where = BuildReturnRecordWhere(command, query);
            Add(command, "$count", Math.Clamp(query.Count <= 0 ? 200 : query.Count, 1, 10000));
            command.CommandText = $"""
                SELECT event_id, opportunity_id, event_time, signal_date, symbol, name,
                       strategy_code, strategy_name, strategy_group, strategy_version_id, strategy_version,
                       score, signal_price, entry_price,
                       horizon_code, horizon_name, trading_days, horizon_group, target_date, target_close,
                       return_percent, max_return_percent, min_return_percent, status, calculated_at, updated_at
                FROM signal_return_records
                {where}
                ORDER BY event_time DESC, symbol ASC, horizon_code ASC
                LIMIT $count;
                """;

            using var reader = command.ExecuteReader();
            var items = new List<SignalReturnRecord>();
            while (reader.Read())
            {
                items.Add(ReadRecord(reader));
            }

            return new SignalReturnQueryResult(totalCount, items);
        }
    }

    public IReadOnlyList<SignalReturnStrategySummary> QueryStrategySummaries(SignalReturnSummaryQuery query)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            var where = BuildSummaryWhere(command, query);
            Add(command, "$count", Math.Clamp(query.Count <= 0 ? 100 : query.Count, 1, 1000));
            command.CommandText = $"""
                SELECT
                    strategy_code,
                    strategy_name,
                    strategy_group,
                    strategy_version,
                    horizon_code,
                    horizon_name,
                    horizon_group,
                    COUNT(*) AS signal_count,
                    SUM(CASE WHEN status = 'Completed' THEN 1 ELSE 0 END) AS completed_count,
                    SUM(CASE WHEN status <> 'Completed' THEN 1 ELSE 0 END) AS pending_count,
                    SUM(CASE WHEN return_percent IS NOT NULL AND CAST(return_percent AS REAL) > 0 THEN 1 ELSE 0 END) AS win_count,
                    AVG(CASE WHEN return_percent IS NOT NULL THEN CAST(return_percent AS REAL) END) AS average_return,
                    AVG(CASE WHEN max_return_percent IS NOT NULL THEN CAST(max_return_percent AS REAL) END) AS average_max_return,
                    AVG(CASE WHEN min_return_percent IS NOT NULL THEN CAST(min_return_percent AS REAL) END) AS average_min_return,
                    MAX(CASE WHEN return_percent IS NOT NULL THEN CAST(return_percent AS REAL) END) AS best_return,
                    MIN(CASE WHEN return_percent IS NOT NULL THEN CAST(return_percent AS REAL) END) AS worst_return,
                    MAX(event_time) AS last_signal_time
                FROM signal_return_records
                {where}
                GROUP BY strategy_code, strategy_name, strategy_group, strategy_version, horizon_code, horizon_name, horizon_group
                ORDER BY strategy_group ASC, horizon_group ASC, completed_count DESC, average_return DESC
                LIMIT $count;
                """;

            using var reader = command.ExecuteReader();
            var items = new List<SignalReturnStrategySummary>();
            while (reader.Read())
            {
                var completedCount = reader.GetInt32(8);
                var winCount = reader.GetInt32(10);
                items.Add(new SignalReturnStrategySummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ReadNullableString(reader, 3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetInt32(7),
                    completedCount,
                    reader.GetInt32(9),
                    winCount,
                    completedCount > 0 ? Math.Round((decimal)winCount / completedCount * 100m, 4) : null,
                    ReadNullableDoubleAsDecimal(reader, 11),
                    ReadNullableDoubleAsDecimal(reader, 12),
                    ReadNullableDoubleAsDecimal(reader, 13),
                    ReadNullableDoubleAsDecimal(reader, 14),
                    ReadNullableDoubleAsDecimal(reader, 15),
                    reader.IsDBNull(16) ? null : ParseDateTime(reader.GetString(16))));
            }

            return items;
        }
    }

    private static string BuildSignalSourceWhere(SqliteCommand command, SignalReturnQuery query)
    {
        var where = new List<string>();
        if (query.FromDate.HasValue)
        {
            where.Add("date(e.event_time) >= $from_date");
            Add(command, "$from_date", FormatDate(query.FromDate.Value));
        }

        if (query.ToDate.HasValue)
        {
            where.Add("date(e.event_time) <= $to_date");
            Add(command, "$to_date", FormatDate(query.ToDate.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.Symbol))
        {
            where.Add("e.symbol = $symbol");
            Add(command, "$symbol", StockSymbolNormalizer.NormalizeCode(query.Symbol));
        }

        if (!string.IsNullOrWhiteSpace(query.StrategyCode))
        {
            where.Add("COALESCE(h.strategy_code, e.strategy_code) = $strategy_code");
            Add(command, "$strategy_code", query.StrategyCode.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.StrategyVersion))
        {
            where.Add("v.version = $strategy_version");
            Add(command, "$strategy_version", query.StrategyVersion.Trim());
        }

        return where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
    }

    private static string BuildReturnRecordWhere(SqliteCommand command, SignalReturnQuery query)
    {
        var where = BuildCommonRecordWhere(command, query.FromDate, query.ToDate, query.Symbol, query.StrategyCode, query.StrategyGroup, query.StrategyVersion, query.HorizonGroup, query.HorizonCode);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            where.Add("status = $status");
            Add(command, "$status", query.Status.Trim());
        }

        return where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
    }

    private static string BuildSummaryWhere(SqliteCommand command, SignalReturnSummaryQuery query)
    {
        var where = BuildCommonRecordWhere(command, query.FromDate, query.ToDate, null, query.StrategyCode, query.StrategyGroup, query.StrategyVersion, query.HorizonGroup, query.HorizonCode);
        return where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);
    }

    private static List<string> BuildCommonRecordWhere(
        SqliteCommand command,
        DateOnly? fromDate,
        DateOnly? toDate,
        string? symbol,
        string? strategyCode,
        string? strategyGroup,
        string? strategyVersion,
        string? horizonGroup,
        string? horizonCode)
    {
        var where = new List<string>();
        if (fromDate.HasValue)
        {
            where.Add("signal_date >= $from_date");
            Add(command, "$from_date", FormatDate(fromDate.Value));
        }

        if (toDate.HasValue)
        {
            where.Add("signal_date <= $to_date");
            Add(command, "$to_date", FormatDate(toDate.Value));
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            where.Add("symbol = $symbol");
            Add(command, "$symbol", StockSymbolNormalizer.NormalizeCode(symbol));
        }

        if (!string.IsNullOrWhiteSpace(strategyCode))
        {
            where.Add("strategy_code = $strategy_code");
            Add(command, "$strategy_code", strategyCode.Trim());
        }

        if (!string.IsNullOrWhiteSpace(strategyGroup))
        {
            where.Add("strategy_group = $strategy_group");
            Add(command, "$strategy_group", strategyGroup.Trim());
        }

        if (!string.IsNullOrWhiteSpace(strategyVersion))
        {
            where.Add("strategy_version = $strategy_version");
            Add(command, "$strategy_version", strategyVersion.Trim());
        }

        if (!string.IsNullOrWhiteSpace(horizonGroup))
        {
            where.Add("horizon_group = $horizon_group");
            Add(command, "$horizon_group", horizonGroup.Trim());
        }

        if (!string.IsNullOrWhiteSpace(horizonCode))
        {
            where.Add("horizon_code = $horizon_code");
            Add(command, "$horizon_code", horizonCode.Trim());
        }

        return where;
    }

    private static void AddRecordParameters(SqliteCommand command, SignalReturnRecord item)
    {
        Add(command, "$event_id", item.EventId.ToString());
        Add(command, "$opportunity_id", item.OpportunityId.ToString());
        Add(command, "$event_time", FormatDateTime(item.EventTime));
        Add(command, "$signal_date", FormatDate(item.SignalDate));
        Add(command, "$symbol", item.Symbol);
        Add(command, "$name", item.Name);
        Add(command, "$strategy_code", item.StrategyCode);
        Add(command, "$strategy_name", item.StrategyName);
        Add(command, "$strategy_group", item.StrategyGroup);
        Add(command, "$strategy_version_id", item.StrategyVersionId);
        Add(command, "$strategy_version", item.StrategyVersion);
        Add(command, "$score", FormatDecimal(item.Score));
        Add(command, "$signal_price", item.SignalPrice.HasValue ? FormatDecimal(item.SignalPrice.Value) : null);
        Add(command, "$entry_price", FormatDecimal(item.EntryPrice));
        Add(command, "$horizon_code", item.HorizonCode);
        Add(command, "$horizon_name", item.HorizonName);
        Add(command, "$trading_days", item.TradingDays);
        Add(command, "$horizon_group", item.HorizonGroup);
        Add(command, "$target_date", item.TargetDate.HasValue ? FormatDate(item.TargetDate.Value) : null);
        Add(command, "$target_close", item.TargetClose.HasValue ? FormatDecimal(item.TargetClose.Value) : null);
        Add(command, "$return_percent", item.ReturnPercent.HasValue ? FormatDecimal(item.ReturnPercent.Value) : null);
        Add(command, "$max_return_percent", item.MaxReturnPercent.HasValue ? FormatDecimal(item.MaxReturnPercent.Value) : null);
        Add(command, "$min_return_percent", item.MinReturnPercent.HasValue ? FormatDecimal(item.MinReturnPercent.Value) : null);
        Add(command, "$status", item.Status);
        Add(command, "$calculated_at", FormatDateTime(item.CalculatedAt));
        Add(command, "$updated_at", FormatDateTime(item.UpdatedAt));
    }

    private static SignalReturnRecord ReadRecord(SqliteDataReader reader)
    {
        return new SignalReturnRecord(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            ParseDateTime(reader.GetString(2)),
            ParseDate(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            ReadNullableString(reader, 9),
            ReadNullableString(reader, 10),
            ParseDecimal(reader.GetString(11)),
            ReadNullableDecimal(reader, 12),
            ParseDecimal(reader.GetString(13)),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetInt32(16),
            reader.GetString(17),
            ReadNullableDate(reader, 18),
            ReadNullableDecimal(reader, 19),
            ReadNullableDecimal(reader, 20),
            ReadNullableDecimal(reader, 21),
            ReadNullableDecimal(reader, 22),
            reader.GetString(23),
            ParseDateTime(reader.GetString(24)),
            ParseDateTime(reader.GetString(25)));
    }

    private static bool MatchesStrategyGroup(string strategyCode, string? strategyGroup)
    {
        return string.IsNullOrWhiteSpace(strategyGroup)
            || string.Equals(SignalReturnStatsService.ResolveStrategyGroup(strategyCode), strategyGroup.Trim(), StringComparison.OrdinalIgnoreCase);
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

    private static string FormatDate(DateOnly value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static DateOnly ParseDate(string value)
    {
        return DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
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

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ParseDecimal(reader.GetString(ordinal));
    }

    private static DateOnly? ReadNullableDate(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static decimal? ReadNullableDoubleAsDecimal(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : Math.Round(Convert.ToDecimal(reader.GetDouble(ordinal), CultureInfo.InvariantCulture), 4);
    }
}
