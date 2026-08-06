using System.Globalization;

namespace AShareRadar.ServiceHost.Workers;

public sealed class ExternalSentimentCsvStore
{
    private const string Header = "trading_date,financing_balance_change,etf_net_subscription,northbound_net_flow,index_future_basis,option_pcr";

    public void Upsert(string configuredPath, DateOnly tradingDate, IReadOnlyDictionary<string, decimal?> values)
    {
        var path = ResolvePath(configuredPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        var rows = File.Exists(path)
            ? File.ReadAllLines(path).Where(item => !string.IsNullOrWhiteSpace(item)).ToList()
            : [];

        if (rows.Count == 0)
        {
            rows.Add(Header);
        }

        var rowIndex = rows.FindIndex(1, item => item.StartsWith(tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var existing = rowIndex >= 0 ? ParseRow(rows[0], rows[rowIndex]) : null;
        var updatedRow = BuildRow(tradingDate, values, existing);
        if (rowIndex >= 0)
        {
            rows[rowIndex] = updatedRow;
        }
        else
        {
            rows.Add(updatedRow);
        }

        File.WriteAllLines(path, rows);
    }

    private static IReadOnlyDictionary<string, string> ParseRow(string headerLine, string rowLine)
    {
        var headers = headerLine.Split(',');
        var values = rowLine.Split(',');
        return headers
            .Select((header, index) => new
            {
                Header = header.Trim(),
                Value = index < values.Length ? values[index].Trim() : ""
            })
            .ToDictionary(item => item.Header, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildRow(DateOnly tradingDate, IReadOnlyDictionary<string, decimal?> values)
    {
        return BuildRow(tradingDate, values, existing: null);
    }

    private static string BuildRow(
        DateOnly tradingDate,
        IReadOnlyDictionary<string, decimal?> values,
        IReadOnlyDictionary<string, string>? existing)
    {
        return string.Join(
            ',',
            tradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Format(values, existing, "financing_balance_change"),
            Format(values, existing, "etf_net_subscription"),
            Format(values, existing, "northbound_net_flow"),
            Format(values, existing, "index_future_basis"),
            Format(values, existing, "option_pcr"));
    }

    private static string Format(
        IReadOnlyDictionary<string, decimal?> values,
        IReadOnlyDictionary<string, string>? existing,
        string code)
    {
        if (values.TryGetValue(code, out var value) && value.HasValue)
        {
            return value.Value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        return existing is not null && existing.TryGetValue(code, out var text)
            ? text
            : "";
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var currentPath = Path.GetFullPath(path);
        if (File.Exists(currentPath) || Directory.Exists(Path.GetDirectoryName(currentPath)))
        {
            return currentPath;
        }

        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            var projectDataPath = Path.Combine(directory.FullName, "src", "AShareRadar.ServiceHost", path);
            var parent = Path.GetDirectoryName(projectDataPath);
            if (File.Exists(projectDataPath) || (parent is not null && Directory.Exists(parent)))
            {
                return projectDataPath;
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, path);
    }
}
