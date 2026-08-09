using System.Globalization;
using System.Text.Json;
using AShareRadar.Application.MarketData;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.MarketData;

public sealed class SqliteAuctionObservationStore : IAuctionObservationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqliteAuctionObservationStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public void ReplaceWatchPool(
        DateOnly tradingDate,
        DateOnly referenceTradeDate,
        IReadOnlyList<AuctionWatchItem> items)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM auction_watch_pool WHERE trading_date = $trading_date;", ("$trading_date", FormatDate(tradingDate)));
            foreach (var item in items)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO auction_watch_pool(
                        trading_date, reference_trade_date, symbol, name, source_rank, source_score,
                        source_strategies, source_hit_time, created_at)
                    VALUES($trading_date, $reference_trade_date, $symbol, $name, $source_rank,
                           $source_score, $source_strategies, $source_hit_time, $created_at);
                    """;
                Add(command, "$trading_date", FormatDate(tradingDate));
                Add(command, "$reference_trade_date", FormatDate(referenceTradeDate));
                Add(command, "$symbol", item.Symbol);
                Add(command, "$name", item.Name);
                Add(command, "$source_rank", item.Rank);
                Add(command, "$source_score", FormatDecimal(item.Score));
                Add(command, "$source_strategies", item.StrategyNames);
                Add(command, "$source_hit_time", item.SourceHitTime.ToString("O", CultureInfo.InvariantCulture));
                Add(command, "$created_at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<AuctionWatchItem> GetWatchPool(DateOnly tradingDate)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT symbol, name, source_rank, source_score, source_strategies, source_hit_time
                FROM auction_watch_pool
                WHERE trading_date = $trading_date
                ORDER BY source_rank;
                """;
            Add(command, "$trading_date", FormatDate(tradingDate));
            using var reader = command.ExecuteReader();
            var result = new List<AuctionWatchItem>();
            while (reader.Read())
            {
                result.Add(new AuctionWatchItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    ParseDecimal(reader.GetString(3)),
                    reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }

            return result;
        }
    }

    public void UpsertTicks(DateOnly tradingDate, IReadOnlyList<AuctionTickSnapshot> snapshots)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var snapshot in snapshots)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO auction_ticks(
                        trading_date, symbol, event_time, name, price, pre_close,
                        cum_volume, cum_amount, quotes_json)
                    VALUES($trading_date, $symbol, $event_time, $name, $price, $pre_close,
                           $cum_volume, $cum_amount, $quotes_json);
                    """;
                Add(command, "$trading_date", FormatDate(tradingDate));
                Add(command, "$symbol", snapshot.Symbol);
                Add(command, "$event_time", snapshot.EventTime.ToString("O", CultureInfo.InvariantCulture));
                Add(command, "$name", snapshot.Name);
                Add(command, "$price", snapshot.Price.HasValue ? FormatDecimal(snapshot.Price.Value) : DBNull.Value);
                Add(command, "$pre_close", FormatDecimal(snapshot.PreClose));
                Add(command, "$cum_volume", FormatDecimal(snapshot.CumVolume));
                Add(command, "$cum_amount", FormatDecimal(snapshot.CumAmount));
                Add(command, "$quotes_json", JsonSerializer.Serialize(snapshot.Quotes, JsonOptions));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<AuctionTickSnapshot> GetTicks(DateOnly tradingDate)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT symbol, name, event_time, price, pre_close, cum_volume, cum_amount, quotes_json
                FROM auction_ticks
                WHERE trading_date = $trading_date
                ORDER BY event_time;
                """;
            Add(command, "$trading_date", FormatDate(tradingDate));
            using var reader = command.ExecuteReader();
            var result = new List<AuctionTickSnapshot>();
            while (reader.Read())
            {
                result.Add(new AuctionTickSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.IsDBNull(3) ? null : ParseDecimal(reader.GetString(3)),
                    ParseDecimal(reader.GetString(4)),
                    ParseDecimal(reader.GetString(5)),
                    ParseDecimal(reader.GetString(6)),
                    JsonSerializer.Deserialize<AuctionQuoteLevel[]>(reader.GetString(7), JsonOptions) ?? []));
            }

            return result;
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value);
    }

    private static string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatDecimal(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0m;
}
