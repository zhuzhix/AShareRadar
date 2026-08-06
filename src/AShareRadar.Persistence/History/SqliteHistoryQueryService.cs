using System.Globalization;
using AShareRadar.Application.History;
using AShareRadar.Application.MarketData;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.History;

public sealed class SqliteHistoryQueryService : IHistoryQueryService
{
    private readonly SqliteDatabase _database;

    public SqliteHistoryQueryService(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public IReadOnlyList<HistoricalSignalItem> QuerySignals(HistoricalSignalQuery query)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        var where = new List<string>();
        if (query.TradingDate.HasValue)
        {
            where.Add("date(event_time) = $trading_date");
            Add(command, "$trading_date", query.TradingDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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

        Add(command, "$count", Math.Clamp(query.Count, 1, 10000));
        command.CommandText = $"""
            SELECT
                e.id,
                e.opportunity_id,
                e.event_time,
                e.event_type,
                e.symbol,
                e.name,
                e.strategy_code,
                e.strategy_name,
                e.score,
                e.price,
                e.reason,
                e.risk,
                COUNT(h.event_id) AS hit_count
            FROM signal_events e
            LEFT JOIN strategy_hits h ON h.event_id = e.id
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty)}
            GROUP BY
                e.id,
                e.opportunity_id,
                e.event_time,
                e.event_type,
                e.symbol,
                e.name,
                e.strategy_code,
                e.strategy_name,
                e.score,
                e.price,
                e.reason,
                e.risk
            ORDER BY e.event_time DESC
            LIMIT $count;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<HistoricalSignalItem>();
        while (reader.Read())
        {
            items.Add(new HistoricalSignalItem(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                ParseDecimal(reader.GetString(8)),
                ReadNullableDecimal(reader, 9),
                reader.GetString(10),
                ReadNullableString(reader, 11),
                reader.GetInt32(12)));
        }

        return items;
    }

    public IReadOnlyList<StrategyPerformanceItem> QueryStrategyPerformance(DateOnly? tradingDate, int count)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        var where = string.Empty;
        if (tradingDate.HasValue)
        {
            where = "WHERE date(event_time) = $trading_date";
            Add(command, "$trading_date", tradingDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        Add(command, "$count", Math.Clamp(count, 1, 100));
        command.CommandText = $"""
            SELECT
                strategy_code,
                strategy_name,
                COUNT(*) AS hit_count,
                AVG(CAST(score AS REAL)) AS average_score,
                MAX(CAST(score AS REAL)) AS max_score,
                MAX(event_time) AS last_hit_time
            FROM signal_events
            {where}
            GROUP BY strategy_code, strategy_name
            ORDER BY hit_count DESC, average_score DESC
            LIMIT $count;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<StrategyPerformanceItem>();
        while (reader.Read())
        {
            items.Add(new StrategyPerformanceItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                Convert.ToDecimal(reader.GetDouble(3), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return items;
    }

    private static void Add(SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value);
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
}
