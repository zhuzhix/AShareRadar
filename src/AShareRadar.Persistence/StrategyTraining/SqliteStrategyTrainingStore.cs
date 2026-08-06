using System.Globalization;
using System.Text.Json;
using AShareRadar.Application.MarketData;
using AShareRadar.Application.StrategyTraining;
using AShareRadar.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace AShareRadar.Persistence.StrategyTraining;

public sealed class SqliteStrategyTrainingStore : IStrategyTrainingStore, IStrategyParameterProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteDatabase _database;

    public SqliteStrategyTrainingStore(SqliteDatabase database)
    {
        _database = database;
        _database.EnsureCreated();
    }

    public IReadOnlyList<StrategyTrainingSignalSource> QuerySignalSources(
        DateOnly startDate,
        DateOnly endDate,
        string? strategyCode,
        int maxCount)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                e.id,
                e.event_time,
                e.symbol,
                e.name,
                e.strategy_code,
                e.strategy_name,
                e.score,
                e.price,
                e.reason,
                h.metrics_json
            FROM signal_events e
            LEFT JOIN strategy_hits h
                ON h.event_id = e.id
               AND h.strategy_code = e.strategy_code
            WHERE date(e.event_time) BETWEEN $startDate AND $endDate
              AND ($strategyCode IS NULL OR e.strategy_code = $strategyCode)
              AND e.event_type IN ('New', 'ReHit', 'Strengthened', 'Continued')
            ORDER BY e.event_time ASC, e.score DESC
            LIMIT $maxCount;
            """;
        command.Parameters.AddWithValue("$startDate", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endDate", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$strategyCode", string.IsNullOrWhiteSpace(strategyCode) ? DBNull.Value : strategyCode.Trim());
        command.Parameters.AddWithValue("$maxCount", Math.Clamp(maxCount, 1, 20000));

        var items = new List<StrategyTrainingSignalSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var eventTime = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            var symbol = StockSymbolNormalizer.NormalizeCode(reader.GetString(2));
            items.Add(new StrategyTrainingSignalSource(
                Guid.Parse(reader.GetString(0)),
                eventTime,
                DateOnly.FromDateTime(eventTime.LocalDateTime.Date),
                symbol,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ReadDecimal(reader, 6) ?? 0m,
                ReadDecimal(reader, 7),
                reader.GetString(8),
                ReadMetrics(reader.IsDBNull(9) ? null : reader.GetString(9))));
        }

        return items;
    }

    public IReadOnlyList<StrategyTrainingSample> QuerySamples(
        DateOnly startDate,
        DateOnly endDate,
        string? strategyCode,
        int evaluationDays,
        int maxCount)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                signal_date,
                symbol,
                name,
                strategy_code,
                strategy_name,
                score,
                price,
                amount_yi,
                change_percent,
                volume_ratio,
                relative_strength_percent,
                sector_heat_score,
                concept_heat_score,
                sentiment_temperature,
                next_open_return,
                next_high_return,
                next_close_return,
                is_success,
                reason,
                evaluation_days,
                metrics_json
            FROM strategy_training_samples
            WHERE signal_date BETWEEN $startDate AND $endDate
              AND ($strategyCode IS NULL OR strategy_code = $strategyCode)
              AND evaluation_days = $evaluationDays
            ORDER BY signal_date DESC, score DESC
            LIMIT $maxCount;
            """;
        command.Parameters.AddWithValue("$startDate", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endDate", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$strategyCode", string.IsNullOrWhiteSpace(strategyCode) ? DBNull.Value : strategyCode.Trim());
        command.Parameters.AddWithValue("$evaluationDays", Math.Clamp(evaluationDays, 1, 20));
        command.Parameters.AddWithValue("$maxCount", Math.Clamp(maxCount, 1, 20000));

        var items = new List<StrategyTrainingSample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadSample(reader));
        }

        return items;
    }

    public void UpsertSamples(IReadOnlyList<StrategyTrainingSample> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var sample in samples)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO strategy_training_samples (
                    id,
                    signal_date,
                    symbol,
                    name,
                    strategy_code,
                    strategy_name,
                    score,
                    price,
                    amount_yi,
                    change_percent,
                    volume_ratio,
                    relative_strength_percent,
                    sector_heat_score,
                    concept_heat_score,
                    sentiment_temperature,
                    next_open_return,
                    next_high_return,
                    next_close_return,
                    is_success,
                    reason,
                    evaluation_days,
                    metrics_json,
                    created_at
                )
                VALUES (
                    $id,
                    $signalDate,
                    $symbol,
                    $name,
                    $strategyCode,
                    $strategyName,
                    $score,
                    $price,
                    $amountYi,
                    $changePercent,
                    $volumeRatio,
                    $relativeStrengthPercent,
                    $sectorHeatScore,
                    $conceptHeatScore,
                    $sentimentTemperature,
                    $nextOpenReturn,
                    $nextHighReturn,
                    $nextCloseReturn,
                    $isSuccess,
                    $reason,
                    $evaluationDays,
                    $metricsJson,
                    $createdAt
                )
                ON CONFLICT(signal_date, symbol, strategy_code) DO UPDATE SET
                    name = excluded.name,
                    strategy_name = excluded.strategy_name,
                    score = excluded.score,
                    price = excluded.price,
                    amount_yi = excluded.amount_yi,
                    change_percent = excluded.change_percent,
                    volume_ratio = excluded.volume_ratio,
                    relative_strength_percent = excluded.relative_strength_percent,
                    sector_heat_score = excluded.sector_heat_score,
                    concept_heat_score = excluded.concept_heat_score,
                    sentiment_temperature = excluded.sentiment_temperature,
                    next_open_return = excluded.next_open_return,
                    next_high_return = excluded.next_high_return,
                    next_close_return = excluded.next_close_return,
                    is_success = excluded.is_success,
                    reason = excluded.reason,
                    evaluation_days = excluded.evaluation_days,
                    metrics_json = excluded.metrics_json;
                """;
            AddSampleParameters(command, sample);
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SaveRun(StrategyTrainingRun run)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO strategy_training_runs (
                    id,
                    start_date,
                    end_date,
                    strategy_code,
                    source_signal_count,
                    sample_count,
                    result_count,
                    message,
                    created_at
                )
                VALUES (
                    $id,
                    $startDate,
                    $endDate,
                    $strategyCode,
                    $sourceSignalCount,
                    $sampleCount,
                    $resultCount,
                    $message,
                    $createdAt
                );
                """;
            command.Parameters.AddWithValue("$id", run.RunId.ToString());
            command.Parameters.AddWithValue("$startDate", run.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$endDate", run.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$strategyCode", string.IsNullOrWhiteSpace(run.StrategyCode) ? DBNull.Value : run.StrategyCode);
            command.Parameters.AddWithValue("$sourceSignalCount", run.SourceSignalCount);
            command.Parameters.AddWithValue("$sampleCount", run.SampleCount);
            command.Parameters.AddWithValue("$resultCount", run.ResultCount);
            command.Parameters.AddWithValue("$message", run.Message);
            command.Parameters.AddWithValue("$createdAt", run.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        foreach (var result in run.Results)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO strategy_training_results (
                    run_id,
                    rank,
                    min_score,
                    min_amount_yi,
                    min_relative_strength_percent,
                    min_heat_score,
                    max_output_per_day,
                    hit_count,
                    success_count,
                    success_rate,
                    average_next_open_return,
                    average_next_high_return,
                    average_next_close_return,
                    worst_next_close_return,
                    summary
                )
                VALUES (
                    $runId,
                    $rank,
                    $minScore,
                    $minAmountYi,
                    $minRelativeStrengthPercent,
                    $minHeatScore,
                    $maxOutputPerDay,
                    $hitCount,
                    $successCount,
                    $successRate,
                    $averageNextOpenReturn,
                    $averageNextHighReturn,
                    $averageNextCloseReturn,
                    $worstNextCloseReturn,
                    $summary
                );
                """;
            command.Parameters.AddWithValue("$runId", run.RunId.ToString());
            command.Parameters.AddWithValue("$rank", result.Rank);
            command.Parameters.AddWithValue("$minScore", ToText(result.MinScore));
            command.Parameters.AddWithValue("$minAmountYi", ToText(result.MinAmountYi));
            command.Parameters.AddWithValue("$minRelativeStrengthPercent", ToText(result.MinRelativeStrengthPercent));
            command.Parameters.AddWithValue("$minHeatScore", ToText(result.MinHeatScore));
            command.Parameters.AddWithValue("$maxOutputPerDay", result.MaxOutputPerDay);
            command.Parameters.AddWithValue("$hitCount", result.HitCount);
            command.Parameters.AddWithValue("$successCount", result.SuccessCount);
            command.Parameters.AddWithValue("$successRate", ToDb(result.SuccessRate));
            command.Parameters.AddWithValue("$averageNextOpenReturn", ToDb(result.AverageNextOpenReturn));
            command.Parameters.AddWithValue("$averageNextHighReturn", ToDb(result.AverageNextHighReturn));
            command.Parameters.AddWithValue("$averageNextCloseReturn", ToDb(result.AverageNextCloseReturn));
            command.Parameters.AddWithValue("$worstNextCloseReturn", ToDb(result.WorstNextCloseReturn));
            command.Parameters.AddWithValue("$summary", result.Summary);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<StrategyParameterProfile> GetProfiles(string? strategyCode)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                strategy_code,
                profile_name,
                source_training_run_id,
                parameters_json,
                sample_count,
                success_rate,
                average_next_high_return,
                average_next_close_return,
                is_active,
                created_at,
                activated_at
            FROM strategy_parameter_profiles
            WHERE $strategyCode IS NULL OR strategy_code = $strategyCode
            ORDER BY is_active DESC, created_at DESC;
            """;
        command.Parameters.AddWithValue("$strategyCode", string.IsNullOrWhiteSpace(strategyCode) ? DBNull.Value : strategyCode.Trim());

        var items = new List<StrategyParameterProfile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadProfile(reader));
        }

        return items;
    }

    public StrategyParameterProfile? GetActiveProfile(string strategyCode)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                strategy_code,
                profile_name,
                source_training_run_id,
                parameters_json,
                sample_count,
                success_rate,
                average_next_high_return,
                average_next_close_return,
                is_active,
                created_at,
                activated_at
            FROM strategy_parameter_profiles
            WHERE strategy_code = $strategyCode AND is_active = 1
            ORDER BY activated_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$strategyCode", strategyCode.Trim());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    public void SaveProfile(StrategyParameterProfile profile)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO strategy_parameter_profiles (
                id,
                strategy_code,
                profile_name,
                source_training_run_id,
                parameters_json,
                sample_count,
                success_rate,
                average_next_high_return,
                average_next_close_return,
                is_active,
                created_at,
                activated_at
            )
            VALUES (
                $id,
                $strategyCode,
                $profileName,
                $sourceTrainingRunId,
                $parametersJson,
                $sampleCount,
                $successRate,
                $averageNextHighReturn,
                $averageNextCloseReturn,
                $isActive,
                $createdAt,
                $activatedAt
            );
            """;
        AddProfileParameters(command, profile);
        command.ExecuteNonQuery();
    }

    public StrategyParameterProfile? Activate(Guid id)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var profile = GetProfile(connection, transaction, id);
        if (profile is null)
        {
            transaction.Commit();
            return null;
        }

        using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE strategy_parameter_profiles SET is_active = 0 WHERE strategy_code = $strategyCode;";
            deactivate.Parameters.AddWithValue("$strategyCode", profile.StrategyCode);
            deactivate.ExecuteNonQuery();
        }

        var activatedAt = DateTimeOffset.Now;
        using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = """
                UPDATE strategy_parameter_profiles
                SET is_active = 1, activated_at = $activatedAt
                WHERE id = $id;
                """;
            activate.Parameters.AddWithValue("$id", id.ToString());
            activate.Parameters.AddWithValue("$activatedAt", activatedAt.ToString("O", CultureInfo.InvariantCulture));
            activate.ExecuteNonQuery();
        }

        transaction.Commit();
        return profile with { IsActive = true, ActivatedAt = activatedAt };
    }

    public void Deactivate(string strategyCode)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE strategy_parameter_profiles
            SET is_active = 0, activated_at = NULL
            WHERE strategy_code = $strategyCode;
            """;
        command.Parameters.AddWithValue("$strategyCode", strategyCode.Trim());
        command.ExecuteNonQuery();
    }

    private static StrategyTrainingSample ReadSample(SqliteDataReader reader)
    {
        return new StrategyTrainingSample(
            Guid.Parse(reader.GetString(0)),
            DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ReadDecimal(reader, 6) ?? 0m,
            ReadDecimal(reader, 7),
            ReadDecimal(reader, 8),
            ReadDecimal(reader, 9),
            ReadDecimal(reader, 10),
            ReadDecimal(reader, 11),
            ReadDecimal(reader, 12),
            ReadDecimal(reader, 13),
            ReadDecimal(reader, 14),
            ReadDecimal(reader, 15),
            ReadDecimal(reader, 16),
            ReadDecimal(reader, 17),
            reader.GetInt32(18) == 1,
            reader.GetString(19),
            ReadMetrics(reader.IsDBNull(21) ? null : reader.GetString(21)),
            reader.GetInt32(20));
    }

    private static void AddSampleParameters(SqliteCommand command, StrategyTrainingSample sample)
    {
        command.Parameters.AddWithValue("$id", sample.Id.ToString());
        command.Parameters.AddWithValue("$signalDate", sample.SignalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$symbol", sample.Symbol);
        command.Parameters.AddWithValue("$name", sample.Name);
        command.Parameters.AddWithValue("$strategyCode", sample.StrategyCode);
        command.Parameters.AddWithValue("$strategyName", sample.StrategyName);
        command.Parameters.AddWithValue("$score", ToText(sample.Score));
        command.Parameters.AddWithValue("$price", ToDb(sample.Price));
        command.Parameters.AddWithValue("$amountYi", ToDb(sample.AmountYi));
        command.Parameters.AddWithValue("$changePercent", ToDb(sample.ChangePercent));
        command.Parameters.AddWithValue("$volumeRatio", ToDb(sample.VolumeRatio));
        command.Parameters.AddWithValue("$relativeStrengthPercent", ToDb(sample.RelativeStrengthPercent));
        command.Parameters.AddWithValue("$sectorHeatScore", ToDb(sample.SectorHeatScore));
        command.Parameters.AddWithValue("$conceptHeatScore", ToDb(sample.ConceptHeatScore));
        command.Parameters.AddWithValue("$sentimentTemperature", ToDb(sample.SentimentTemperature));
        command.Parameters.AddWithValue("$nextOpenReturn", ToDb(sample.NextOpenReturn));
        command.Parameters.AddWithValue("$nextHighReturn", ToDb(sample.NextHighReturn));
        command.Parameters.AddWithValue("$nextCloseReturn", ToDb(sample.NextCloseReturn));
        command.Parameters.AddWithValue("$isSuccess", sample.IsSuccess ? 1 : 0);
        command.Parameters.AddWithValue("$reason", sample.Reason);
        command.Parameters.AddWithValue("$evaluationDays", Math.Clamp(sample.EvaluationDays, 1, 20));
        command.Parameters.AddWithValue("$metricsJson", sample.Metrics is null || sample.Metrics.Count == 0
            ? DBNull.Value
            : JsonSerializer.Serialize(sample.Metrics, JsonOptions));
    }

    private static StrategyParameterProfile ReadProfile(SqliteDataReader reader)
    {
        return new StrategyParameterProfile(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4), JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            reader.GetInt32(5),
            ReadDecimal(reader, 6),
            ReadDecimal(reader, 7),
            ReadDecimal(reader, 8),
            reader.GetInt32(9) == 1,
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture));
    }

    private static StrategyParameterProfile? GetProfile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                strategy_code,
                profile_name,
                source_training_run_id,
                parameters_json,
                sample_count,
                success_rate,
                average_next_high_return,
                average_next_close_return,
                is_active,
                created_at,
                activated_at
            FROM strategy_parameter_profiles
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    private static void AddProfileParameters(SqliteCommand command, StrategyParameterProfile profile)
    {
        command.Parameters.AddWithValue("$id", profile.Id.ToString());
        command.Parameters.AddWithValue("$strategyCode", profile.StrategyCode);
        command.Parameters.AddWithValue("$profileName", profile.ProfileName);
        command.Parameters.AddWithValue("$sourceTrainingRunId", profile.SourceTrainingRunId.HasValue ? profile.SourceTrainingRunId.Value.ToString() : (object)DBNull.Value);
        command.Parameters.AddWithValue("$parametersJson", JsonSerializer.Serialize(profile.Parameters, JsonOptions));
        command.Parameters.AddWithValue("$sampleCount", profile.SampleCount);
        command.Parameters.AddWithValue("$successRate", ToDb(profile.SuccessRate));
        command.Parameters.AddWithValue("$averageNextHighReturn", ToDb(profile.AverageNextHighReturn));
        command.Parameters.AddWithValue("$averageNextCloseReturn", ToDb(profile.AverageNextCloseReturn));
        command.Parameters.AddWithValue("$isActive", profile.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", profile.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$activatedAt", profile.ActivatedAt.HasValue ? profile.ActivatedAt.Value.ToString("O", CultureInfo.InvariantCulture) : (object)DBNull.Value);
    }

    private static IReadOnlyDictionary<string, decimal> ReadMetrics(string? metricsJson)
    {
        if (string.IsNullOrWhiteSpace(metricsJson))
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metricsJson, JsonOptions)
                ?? new Dictionary<string, JsonElement>();
            var metrics = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in values)
            {
                var parsed = item.Value.ValueKind switch
                {
                    JsonValueKind.Number => item.Value.TryGetDecimal(out var value) ? value : null,
                    JsonValueKind.String => ParseDecimal(item.Value.GetString()),
                    _ => null
                };
                if (parsed.HasValue)
                {
                    metrics[item.Key] = parsed.Value;
                }
            }

            return metrics;
        }
        catch
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static decimal? ReadDecimal(SqliteDataReader reader, int index)
    {
        return reader.IsDBNull(index) ? null : ParseDecimal(reader.GetString(index));
    }

    private static decimal? ParseDecimal(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string ToText(decimal value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static object ToDb(decimal? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : DBNull.Value;
    }
}
