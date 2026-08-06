using System.Globalization;
using System.Text;

namespace AShareRadar.Application.Review;

public sealed class QlibTomorrowPredictionCsvReader
{
    public IReadOnlyList<QlibTomorrowPrediction> Read(string path, DateOnly expectedSignalDate)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Qlib tomorrow prediction file not found: {path}", path);
        }

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return [];
        }

        var headers = SplitCsvLine(headerLine)
            .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);

        var items = new List<QlibTomorrowPrediction>();
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cells = SplitCsvLine(line);
            var signalDate = ReadDate(cells, headers, "signal_date", expectedSignalDate);
            if (signalDate != expectedSignalDate)
            {
                throw new InvalidOperationException(
                    $"Qlib prediction signal_date mismatch. Expected {expectedSignalDate:yyyy-MM-dd}, got {signalDate:yyyy-MM-dd}.");
            }

            var symbol = NormalizeSymbol(ReadString(cells, headers, "symbol"));
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var upProbability = ReadProbability(cells, headers, "up_probability");
            var downProbability = headers.ContainsKey("down_probability")
                ? ReadProbability(cells, headers, "down_probability")
                : 1m - upProbability;

            items.Add(new QlibTomorrowPrediction(
                signalDate,
                symbol,
                ReadOptionalString(cells, headers, "name") ?? symbol,
                upProbability,
                downProbability,
                ReadOptionalString(cells, headers, "pred_direction")
                    ?? ReadOptionalString(cells, headers, "direction")
                    ?? "震荡",
                ReadOptionalString(cells, headers, "confidence") ?? "低",
                headers.ContainsKey("pred_score") ? ReadDecimal(cells, headers, "pred_score") : upProbability,
                headers.ContainsKey("executable") ? ReadBool(cells, headers, "executable") : null,
                ReadOptionalString(cells, headers, "block_reason")));
        }

        return items
            .GroupBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.UpProbability).First())
            .OrderByDescending(item => item.UpProbability)
            .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DateOnly ReadDate(
        IReadOnlyList<string> cells,
        IReadOnlyDictionary<string, int> headers,
        string name,
        DateOnly fallback)
    {
        if (!headers.TryGetValue(name, out var index) || index >= cells.Count || string.IsNullOrWhiteSpace(cells[index]))
        {
            return fallback;
        }

        return DateOnly.Parse(cells[index], CultureInfo.InvariantCulture);
    }

    private static string ReadString(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headers, string name)
    {
        if (!headers.TryGetValue(name, out var index) || index >= cells.Count)
        {
            throw new InvalidOperationException($"Qlib prediction CSV missing required column: {name}");
        }

        return cells[index].Trim();
    }

    private static string? ReadOptionalString(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headers, string name)
    {
        if (!headers.TryGetValue(name, out var index) || index >= cells.Count)
        {
            return null;
        }

        var value = cells[index].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static decimal ReadProbability(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headers, string name)
    {
        var value = ReadDecimal(cells, headers, name);
        return value > 1m ? value / 100m : value;
    }

    private static decimal ReadDecimal(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headers, string name)
    {
        var text = ReadString(cells, headers, name).Trim().TrimEnd('%');
        return decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static bool? ReadBool(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headers, string name)
    {
        var text = ReadOptionalString(cells, headers, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("1", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSymbol(string value)
    {
        var text = value.Trim().ToUpperInvariant();
        if (text.Length >= 8 && (text.StartsWith("SH", StringComparison.Ordinal) || text.StartsWith("SZ", StringComparison.Ordinal)))
        {
            return text[2..8];
        }

        if (text.Contains('.', StringComparison.Ordinal))
        {
            return text.Split('.', 2)[0].PadLeft(6, '0');
        }

        return text.PadLeft(6, '0');
    }

    private static string NormalizeHeader(string value)
    {
        return value.Trim().TrimStart('\uFEFF');
    }

    private static IReadOnlyList<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                result.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(ch);
            }
        }

        result.Add(builder.ToString());
        return result;
    }
}
