using System.Globalization;

namespace AShareRadar.Application.Qlib;

public sealed class QlibSignalFileReader
{
    private readonly QlibSignalOptions _options;

    public QlibSignalFileReader(QlibSignalOptions options)
    {
        _options = options;
    }

    public QlibSignalStatus GetStatus()
    {
        var path = WatchlistPath;
        var exists = File.Exists(path);
        if (!exists)
        {
            return new QlibSignalStatus(_options.Enabled, false, _options.SignalRoot, path, null, 0, null, "Qlib signal file not found.");
        }

        try
        {
            var snapshot = LoadLatest();
            return new QlibSignalStatus(
                _options.Enabled,
                true,
                _options.SignalRoot,
                path,
                snapshot.SignalDate,
                snapshot.Records.Count,
                File.GetLastWriteTime(path),
                null);
        }
        catch (Exception ex)
        {
            return new QlibSignalStatus(
                _options.Enabled,
                true,
                _options.SignalRoot,
                path,
                null,
                0,
                File.GetLastWriteTime(path),
                ex.Message);
        }
    }

    public QlibSignalSnapshot LoadLatest()
    {
        return LoadSnapshot(WatchlistPath, allowEmpty: false);
    }

    public QlibSignalSnapshot LoadRebalancePlan()
    {
        if (!File.Exists(RebalancePlanPath))
        {
            var metadata = LoadManifestMetadata();
            return new QlibSignalSnapshot(
                metadata.StrategyCode,
                metadata.StrategyName,
                metadata.SourceExperimentId,
                metadata.SignalDate,
                DateTimeOffset.Now,
                Array.Empty<QlibSignalRecord>());
        }

        return LoadSnapshot(RebalancePlanPath, allowEmpty: true);
    }

    private QlibSignalSnapshot LoadSnapshot(string path, bool allowEmpty)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Qlib signal file not found.", path);
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length < 1 || (lines.Length < 2 && !allowEmpty))
        {
            throw new InvalidOperationException("Qlib signal file has no data rows.");
        }

        var headers = SplitCsvLine(lines[0]).Select(NormalizeHeader).ToArray();
        var records = new List<QlibSignalRecord>();
        for (var index = 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            var values = SplitCsvLine(lines[index]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var column = 0; column < headers.Length && column < values.Count; column++)
            {
                row[headers[column]] = values[column];
            }

            records.Add(ParseRecord(row));
        }

        if (records.Count == 0)
        {
            if (!allowEmpty)
            {
                throw new InvalidOperationException("Qlib signal file has no readable records.");
            }

            var metadata = LoadManifestMetadata();
            return new QlibSignalSnapshot(
                metadata.StrategyCode,
                metadata.StrategyName,
                metadata.SourceExperimentId,
                metadata.SignalDate,
                DateTimeOffset.Now,
                Array.Empty<QlibSignalRecord>());
        }

        var first = records[0];
        return new QlibSignalSnapshot(
            first.StrategyCode,
            first.StrategyName,
            first.SourceExperimentId,
            first.SignalDate,
            DateTimeOffset.Now,
            records.OrderBy(item => item.ModelRank).ToArray());
    }

    private string WatchlistPath => Path.Combine(_options.SignalRoot, _options.WatchlistFileName);

    private string RebalancePlanPath => Path.Combine(_options.SignalRoot, _options.RebalancePlanFileName);

    private (string StrategyCode, string StrategyName, string SourceExperimentId, DateOnly SignalDate) LoadManifestMetadata()
    {
        var path = Path.Combine(_options.SignalRoot, "manifest.json");
        if (!File.Exists(path))
        {
            return (_options.StrategyCode, _options.StrategyName, string.Empty, DateOnly.FromDateTime(DateTime.Today));
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var strategyCode = GetString(root, "strategy_code") ?? _options.StrategyCode;
            var strategyName = GetString(root, "strategy_name") ?? _options.StrategyName;
            var sourceExperimentId = GetString(root, "source_experiment_id") ?? string.Empty;
            var signalDateText = GetString(root, "signal_date");
            var signalDate = !string.IsNullOrWhiteSpace(signalDateText) && DateOnly.TryParse(signalDateText, out var parsed)
                ? parsed
                : DateOnly.FromDateTime(File.GetLastWriteTime(path));
            return (strategyCode, strategyName, sourceExperimentId, signalDate);
        }
        catch
        {
            return (_options.StrategyCode, _options.StrategyName, string.Empty, DateOnly.FromDateTime(File.GetLastWriteTime(path)));
        }
    }

    private static string? GetString(System.Text.Json.JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == System.Text.Json.JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static QlibSignalRecord ParseRecord(IReadOnlyDictionary<string, string> row)
    {
        return new QlibSignalRecord(
            ParseDate(row, "signal_date"),
            Get(row, "code"),
            Get(row, "symbol"),
            Get(row, "exchange"),
            Get(row, "name"),
            ParseDecimal(row, "pred_score"),
            ParseInt(row, "rank_total"),
            ParseInt(row, "model_rank"),
            ParseDecimal(row, "model_score_100"),
            ParseDecimal(row, "target_weight"),
            Get(row, "action"),
            Get(row, "confidence"),
            Get(row, "strategy_code"),
            Get(row, "strategy_name"),
            Get(row, "source_experiment_id"),
            Get(row, "reason"),
            EmptyToNull(Get(row, "risk")));
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
    }

    private static DateOnly ParseDate(IReadOnlyDictionary<string, string> row, string key)
    {
        return DateOnly.Parse(Get(row, key), CultureInfo.InvariantCulture);
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> row, string key)
    {
        return int.Parse(Get(row, key), CultureInfo.InvariantCulture);
    }

    private static decimal ParseDecimal(IReadOnlyDictionary<string, string> row, string key)
    {
        return decimal.Parse(Get(row, key), CultureInfo.InvariantCulture);
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string NormalizeHeader(string value)
    {
        return value.Trim().TrimStart('\ufeff');
    }

    private static IReadOnlyList<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (ch == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());
        return values;
    }
}
