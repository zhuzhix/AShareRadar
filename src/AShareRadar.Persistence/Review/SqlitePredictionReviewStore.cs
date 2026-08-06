using System.Globalization;
using AShareRadar.Application.Review;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.Review;

public sealed class SqlitePredictionReviewStore : IPredictionReviewStore
{
    private readonly SqliteDatabase _database;
    private readonly object _gate = new();

    public SqlitePredictionReviewStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public IReadOnlyList<PredictionRecord> GetBySignalDate(DateOnly signalDate)
    {
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    id, signal_date, symbol, name, strategy_codes, strategy_names,
                    signal_count, strategy_hit_count, score, best_score,
                    prediction_direction, prediction_score, prediction_reason, risk_note,
                    verify_date, next_open_return, next_close_return, next_high_return, next_low_return,
                    is_close_success, is_intraday_success, verify_status, created_at, verified_at
                FROM prediction_records
                WHERE signal_date = $signal_date
                ORDER BY CAST(prediction_score AS REAL) DESC, CAST(score AS REAL) DESC, symbol ASC;
                """;
            command.Parameters.AddWithValue("$signal_date", signalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            using var reader = command.ExecuteReader();
            var items = new List<PredictionRecord>();
            while (reader.Read())
            {
                items.Add(ReadRecord(reader));
            }

            return items;
        }
    }

    public void UpsertMany(IReadOnlyList<PredictionRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var record in records)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO prediction_records(
                        id, signal_date, symbol, name, strategy_codes, strategy_names,
                        signal_count, strategy_hit_count, score, best_score,
                        prediction_direction, prediction_score, prediction_reason, risk_note,
                        verify_date, next_open_return, next_close_return, next_high_return, next_low_return,
                        is_close_success, is_intraday_success, verify_status, created_at, verified_at)
                    VALUES(
                        $id, $signal_date, $symbol, $name, $strategy_codes, $strategy_names,
                        $signal_count, $strategy_hit_count, $score, $best_score,
                        $prediction_direction, $prediction_score, $prediction_reason, $risk_note,
                        $verify_date, $next_open_return, $next_close_return, $next_high_return, $next_low_return,
                        $is_close_success, $is_intraday_success, $verify_status, $created_at, $verified_at)
                    ON CONFLICT(signal_date, symbol) DO UPDATE SET
                        name = excluded.name,
                        strategy_codes = excluded.strategy_codes,
                        strategy_names = excluded.strategy_names,
                        signal_count = excluded.signal_count,
                        strategy_hit_count = excluded.strategy_hit_count,
                        score = excluded.score,
                        best_score = excluded.best_score,
                        prediction_direction = excluded.prediction_direction,
                        prediction_score = excluded.prediction_score,
                        prediction_reason = excluded.prediction_reason,
                        risk_note = excluded.risk_note,
                        verify_date = excluded.verify_date,
                        next_open_return = excluded.next_open_return,
                        next_close_return = excluded.next_close_return,
                        next_high_return = excluded.next_high_return,
                        next_low_return = excluded.next_low_return,
                        is_close_success = excluded.is_close_success,
                        is_intraday_success = excluded.is_intraday_success,
                        verify_status = excluded.verify_status,
                        verified_at = excluded.verified_at;
                    """;
                Add(command, "$id", record.Id.ToString());
                Add(command, "$signal_date", record.SignalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(command, "$symbol", record.Symbol);
                Add(command, "$name", record.Name);
                Add(command, "$strategy_codes", record.StrategyCodes);
                Add(command, "$strategy_names", record.StrategyNames);
                Add(command, "$signal_count", record.SignalCount);
                Add(command, "$strategy_hit_count", record.StrategyHitCount);
                Add(command, "$score", FormatDecimal(record.Score));
                Add(command, "$best_score", FormatDecimal(record.BestScore));
                Add(command, "$prediction_direction", record.PredictionDirection);
                Add(command, "$prediction_score", FormatDecimal(record.PredictionScore));
                Add(command, "$prediction_reason", record.PredictionReason);
                Add(command, "$risk_note", record.RiskNote);
                Add(command, "$verify_date", record.VerifyDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
                Add(command, "$next_open_return", FormatNullableDecimal(record.NextOpenReturn));
                Add(command, "$next_close_return", FormatNullableDecimal(record.NextCloseReturn));
                Add(command, "$next_high_return", FormatNullableDecimal(record.NextHighReturn));
                Add(command, "$next_low_return", FormatNullableDecimal(record.NextLowReturn));
                Add(command, "$is_close_success", FormatNullableBool(record.IsCloseSuccess));
                Add(command, "$is_intraday_success", FormatNullableBool(record.IsIntradaySuccess));
                Add(command, "$verify_status", record.VerifyStatus);
                Add(command, "$created_at", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                Add(command, "$verified_at", record.VerifiedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private static PredictionRecord ReadRecord(SqliteDataReader reader)
    {
        return new PredictionRecord(
            Guid.Parse(reader.GetString(0)),
            DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            ParseDecimal(reader.GetString(8)),
            ParseDecimal(reader.GetString(9)),
            reader.GetString(10),
            ParseDecimal(reader.GetString(11)),
            reader.GetString(12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : DateOnly.ParseExact(reader.GetString(14), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            ReadNullableDecimal(reader, 15),
            ReadNullableDecimal(reader, 16),
            ReadNullableDecimal(reader, 17),
            ReadNullableDecimal(reader, 18),
            ReadNullableBool(reader, 19),
            ReadNullableBool(reader, 20),
            reader.GetString(21),
            DateTimeOffset.Parse(reader.GetString(22), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(23) ? null : DateTimeOffset.Parse(reader.GetString(23), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static void Add(SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value);
    }

    private static object FormatNullableDecimal(decimal? value)
    {
        return value.HasValue ? FormatDecimal(value.Value) : DBNull.Value;
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static object FormatNullableBool(bool? value)
    {
        return value.HasValue ? value.Value ? 1 : 0 : DBNull.Value;
    }

    private static decimal ParseDecimal(string value)
    {
        return decimal.Parse(value, CultureInfo.InvariantCulture);
    }

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ParseDecimal(reader.GetString(ordinal));
    }

    private static bool? ReadNullableBool(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal) == 1;
    }
}
