using System.Globalization;
using System.Text.Json;
using AShareRadar.Application.MarketData;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.MarketData;

public sealed class SqliteMarketSentimentStore : IMarketSentimentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqliteMarketSentimentStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public void Save(MarketSentimentSnapshot snapshot)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO market_sentiment_snapshots(
                    id, snapshot_time, provider_name, temperature_score, level, summary, data_quality,
                    categories_json, metrics_json, warnings_json, created_at)
                VALUES(
                    $id, $snapshot_time, $provider_name, $temperature_score, $level, $summary, $data_quality,
                    $categories_json, $metrics_json, $warnings_json, $created_at);
                """;
            Add(command, "$id", Guid.NewGuid().ToString("N"));
            Add(command, "$snapshot_time", snapshot.SnapshotTime.ToString("O", CultureInfo.InvariantCulture));
            Add(command, "$provider_name", snapshot.ProviderName);
            Add(command, "$temperature_score", FormatDecimal(snapshot.TemperatureScore));
            Add(command, "$level", snapshot.Level);
            Add(command, "$summary", snapshot.Summary);
            Add(command, "$data_quality", snapshot.DataQuality);
            Add(command, "$categories_json", JsonSerializer.Serialize(snapshot.Categories, JsonOptions));
            Add(command, "$metrics_json", JsonSerializer.Serialize(snapshot.Metrics, JsonOptions));
            Add(command, "$warnings_json", JsonSerializer.Serialize(snapshot.Warnings, JsonOptions));
            Add(command, "$created_at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    public MarketSentimentSnapshot? GetLatest()
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT snapshot_time, provider_name, temperature_score, level, summary, data_quality,
                       categories_json, metrics_json, warnings_json
                FROM market_sentiment_snapshots
                ORDER BY snapshot_time DESC
                LIMIT 1;
                """;
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSnapshot(reader) : null;
        }
    }

    public IReadOnlyList<MarketSentimentSnapshot> Query(DateOnly? tradingDate, int count)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            if (tradingDate.HasValue)
            {
                command.CommandText = """
                    SELECT snapshot_time, provider_name, temperature_score, level, summary, data_quality,
                           categories_json, metrics_json, warnings_json
                    FROM market_sentiment_snapshots
                    WHERE substr(snapshot_time, 1, 10) = $trading_date
                    ORDER BY snapshot_time DESC
                    LIMIT $count;
                    """;
                Add(command, "$trading_date", tradingDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            else
            {
                command.CommandText = """
                    SELECT snapshot_time, provider_name, temperature_score, level, summary, data_quality,
                           categories_json, metrics_json, warnings_json
                    FROM market_sentiment_snapshots
                    ORDER BY snapshot_time DESC
                    LIMIT $count;
                    """;
            }

            Add(command, "$count", Math.Clamp(count, 1, 10000));
            using var reader = command.ExecuteReader();
            var items = new List<MarketSentimentSnapshot>();
            while (reader.Read())
            {
                items.Add(ReadSnapshot(reader));
            }

            return items;
        }
    }

    private static MarketSentimentSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        return new MarketSentimentSnapshot(
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(1),
            ParseDecimal(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            JsonSerializer.Deserialize<MarketSentimentCategory[]>(reader.GetString(6), JsonOptions) ?? [],
            JsonSerializer.Deserialize<MarketSentimentMetric[]>(reader.GetString(7), JsonOptions) ?? [],
            JsonSerializer.Deserialize<string[]>(reader.GetString(8), JsonOptions) ?? []);
    }

    private static void Add(SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value);
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
