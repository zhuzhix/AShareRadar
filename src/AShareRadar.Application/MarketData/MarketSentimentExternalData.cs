namespace AShareRadar.Application.MarketData;

public interface IMarketSentimentExternalDataProvider
{
    Task<MarketSentimentExternalSnapshot> LoadAsync(CancellationToken cancellationToken);

    MarketSentimentDataSourceStatus GetStatus();
}

public sealed class MarketSentimentExternalDataOptions
{
    public bool Enabled { get; set; }

    public string DataPath { get; set; } = "";

    public decimal? FinancingBalanceChange { get; set; }

    public decimal? EtfNetSubscription { get; set; }

    public decimal? NorthboundNetFlow { get; set; }

    public decimal? IndexFutureBasis { get; set; }

    public decimal? OptionPcr { get; set; }
}

public sealed class ConfiguredMarketSentimentExternalDataProvider : IMarketSentimentExternalDataProvider
{
    private readonly MarketSentimentExternalDataOptions _options;
    private MarketSentimentDataSourceStatus _status = MarketSentimentDataSourceStatus.Disabled("ExternalSentimentData");

    public ConfiguredMarketSentimentExternalDataProvider(MarketSentimentExternalDataOptions options)
    {
        _options = options;
        _status = options.Enabled
            ? MarketSentimentDataSourceStatus.Unavailable("ExternalSentimentData", "数据源已启用，等待首次刷新。")
            : MarketSentimentDataSourceStatus.Disabled("ExternalSentimentData");
    }

    public Task<MarketSentimentExternalSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
        {
            _status = MarketSentimentDataSourceStatus.Disabled("ExternalSentimentData");
            return Task.FromResult(MarketSentimentExternalSnapshot.Empty(_status));
        }

        var fileSnapshot = TryLoadFromCsv();
        if (fileSnapshot is not null)
        {
            _status = fileSnapshot.AvailableMetricCount > 0
                ? MarketSentimentDataSourceStatus.Available("ExternalSentimentData", $"已从文件读取 {fileSnapshot.AvailableMetricCount}/5 项外部指标。")
                : MarketSentimentDataSourceStatus.Unavailable("ExternalSentimentData", "外部数据文件存在，但未解析到可用指标。");
            return Task.FromResult(fileSnapshot with { Status = _status });
        }

        var snapshot = new MarketSentimentExternalSnapshot(
            DateTimeOffset.Now,
            _options.FinancingBalanceChange,
            _options.EtfNetSubscription,
            _options.NorthboundNetFlow,
            _options.IndexFutureBasis,
            _options.OptionPcr,
            BuildMetricStatuses());

        _status = snapshot.AvailableMetricCount > 0
            ? MarketSentimentDataSourceStatus.Available("ExternalSentimentData", $"已配置 {snapshot.AvailableMetricCount}/5 项外部指标。")
            : MarketSentimentDataSourceStatus.Unavailable("ExternalSentimentData", "外部数据源已启用，但未配置可用指标。");
        return Task.FromResult(snapshot with { Status = _status });
    }

    public MarketSentimentDataSourceStatus GetStatus()
    {
        return _status;
    }

    private IReadOnlyList<MarketSentimentMetricSourceStatus> BuildMetricStatuses()
    {
        return
        [
            BuildStatus("financing_balance_change", "融资余额变化", _options.FinancingBalanceChange),
            BuildStatus("etf_net_subscription", "ETF净申购", _options.EtfNetSubscription),
            BuildStatus("northbound_net_flow", "北向资金净流入", _options.NorthboundNetFlow),
            BuildStatus("index_future_basis", "股指期货基差", _options.IndexFutureBasis),
            BuildStatus("option_pcr", "期权PCR", _options.OptionPcr)
        ];
    }

    private static MarketSentimentMetricSourceStatus BuildStatus(string code, string name, decimal? value)
    {
        return value.HasValue
            ? new MarketSentimentMetricSourceStatus(code, name, "Configured", "已配置")
            : new MarketSentimentMetricSourceStatus(code, name, "Unavailable", "暂未接入");
    }

    private MarketSentimentExternalSnapshot? TryLoadFromCsv()
    {
        if (string.IsNullOrWhiteSpace(_options.DataPath))
        {
            return null;
        }

        var path = ResolvePath(_options.DataPath);
        if (!File.Exists(path))
        {
            _status = MarketSentimentDataSourceStatus.Unavailable("ExternalSentimentData", $"外部数据文件不存在：{path}");
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(path)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
            if (lines.Length < 2)
            {
                return null;
            }

            var headers = SplitCsvLine(lines[0]);
            var values = SplitCsvLine(lines[^1]);
            var row = headers
                .Select((header, index) => new { Header = header.Trim(), Value = index < values.Length ? values[index].Trim() : "" })
                .ToDictionary(item => item.Header, item => item.Value, StringComparer.OrdinalIgnoreCase);

            var financing = ReadDecimal(row, "financing_balance_change");
            var etf = ReadDecimal(row, "etf_net_subscription");
            var northbound = ReadDecimal(row, "northbound_net_flow");
            var basis = ReadDecimal(row, "index_future_basis");
            var pcr = ReadDecimal(row, "option_pcr");
            return new MarketSentimentExternalSnapshot(
                DateTimeOffset.Now,
                financing,
                etf,
                northbound,
                basis,
                pcr,
                [
                    BuildStatus("financing_balance_change", "融资余额变化", financing),
                    BuildStatus("etf_net_subscription", "ETF净申购", etf),
                    BuildStatus("northbound_net_flow", "北向资金净流入", northbound),
                    BuildStatus("index_future_basis", "股指期货基差", basis),
                    BuildStatus("option_pcr", "期权PCR", pcr)
                ]);
        }
        catch (Exception ex)
        {
            _status = MarketSentimentDataSourceStatus.Unavailable("ExternalSentimentData", $"外部数据文件解析失败：{ex.Message}");
            return null;
        }
    }

    private static decimal? ReadDecimal(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var text) && decimal.TryParse(text, out var value)
            ? value
            : null;
    }

    private static string[] SplitCsvLine(string line)
    {
        return line.Split(',');
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var currentPath = Path.GetFullPath(path);
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        var basePath = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(basePath))
        {
            return basePath;
        }

        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            var projectDataPath = Path.Combine(directory.FullName, "src", "AShareRadar.ServiceHost", path);
            if (File.Exists(projectDataPath))
            {
                return projectDataPath;
            }

            directory = directory.Parent;
        }

        return basePath;
    }
}

public sealed record MarketSentimentExternalSnapshot(
    DateTimeOffset SnapshotTime,
    decimal? FinancingBalanceChange,
    decimal? EtfNetSubscription,
    decimal? NorthboundNetFlow,
    decimal? IndexFutureBasis,
    decimal? OptionPcr,
    IReadOnlyList<MarketSentimentMetricSourceStatus> MetricStatuses,
    MarketSentimentDataSourceStatus? Status = null)
{
    public int AvailableMetricCount => MetricStatuses.Count(item => item.Status == "Configured");

    public static MarketSentimentExternalSnapshot Empty(MarketSentimentDataSourceStatus status)
    {
        return new MarketSentimentExternalSnapshot(
            DateTimeOffset.Now,
            null,
            null,
            null,
            null,
            null,
            [
                new("financing_balance_change", "融资余额变化", "Unavailable", "暂未接入"),
                new("etf_net_subscription", "ETF净申购", "Unavailable", "暂未接入"),
                new("northbound_net_flow", "北向资金净流入", "Unavailable", "暂未接入"),
                new("index_future_basis", "股指期货基差", "Unavailable", "暂未接入"),
                new("option_pcr", "期权PCR", "Unavailable", "暂未接入")
            ],
            status);
    }
}

public sealed record MarketSentimentMetricSourceStatus(
    string Code,
    string Name,
    string Status,
    string Message);

public sealed record MarketSentimentDataSourceStatus(
    string Code,
    string Status,
    string Message,
    DateTimeOffset CheckedAt)
{
    public static MarketSentimentDataSourceStatus Disabled(string code)
    {
        return new MarketSentimentDataSourceStatus(code, "Disabled", "数据源未启用，指标按降级口径处理。", DateTimeOffset.Now);
    }

    public static MarketSentimentDataSourceStatus Unavailable(string code, string message)
    {
        return new MarketSentimentDataSourceStatus(code, "Unavailable", message, DateTimeOffset.Now);
    }

    public static MarketSentimentDataSourceStatus Available(string code, string message)
    {
        return new MarketSentimentDataSourceStatus(code, "Available", message, DateTimeOffset.Now);
    }
}
