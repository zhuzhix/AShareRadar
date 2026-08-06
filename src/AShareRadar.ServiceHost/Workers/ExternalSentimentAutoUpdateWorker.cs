using System.Globalization;
using System.Text.RegularExpressions;

namespace AShareRadar.ServiceHost.Workers;

public sealed class ExternalSentimentAutoUpdateWorker : BackgroundService
{
    private readonly ExternalSentimentAutoUpdateOptions _options;
    private readonly ExternalSentimentCsvStore _store;
    private readonly ILogger<ExternalSentimentAutoUpdateWorker> _logger;
    private bool _startupRunCompleted;

    public ExternalSentimentAutoUpdateWorker(
        ExternalSentimentAutoUpdateOptions options,
        ExternalSentimentCsvStore store,
        ILogger<ExternalSentimentAutoUpdateWorker> logger)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("External sentiment auto update worker is disabled.");
            return;
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 3, 60))
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AShareRadar/1.0");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(Math.Clamp(_options.IntervalMinutes, 10, 1440));
            try
            {
                if (ShouldRun())
                {
                    var values = await FetchValuesAsync(httpClient, stoppingToken);
                    if (values.Values.Any(item => item.HasValue))
                    {
                        _store.Upsert(_options.DataPath, DateOnly.FromDateTime(DateTime.Now), values);
                        _logger.LogInformation("External sentiment data auto updated with {Count} metrics.", values.Values.Count(item => item.HasValue));
                    }

                    _startupRunCompleted = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "External sentiment data auto update failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private bool ShouldRun()
    {
        if (_options.RunOnStartup && !_startupRunCompleted)
        {
            return true;
        }

        if (!TimeOnly.TryParse(_options.RunAfterTime, out var runAfter))
        {
            runAfter = new TimeOnly(16, 30);
        }

        return TimeOnly.FromDateTime(DateTime.Now) >= runAfter;
    }

    private async Task<Dictionary<string, decimal?>> FetchValuesAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase)
        {
            ["financing_balance_change"] = null,
            ["etf_net_subscription"] = null,
            ["northbound_net_flow"] = null,
            ["index_future_basis"] = null,
            ["option_pcr"] = null
        };

        foreach (var source in _options.Sources.Where(item => item.Enabled))
        {
            if (string.IsNullOrWhiteSpace(source.Code) ||
                string.IsNullOrWhiteSpace(source.Url) ||
                string.IsNullOrWhiteSpace(source.ValuePattern))
            {
                continue;
            }

            try
            {
                var text = await httpClient.GetStringAsync(source.Url, cancellationToken);
                var match = Regex.Match(text, source.ValuePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
                if (!match.Success)
                {
                    _logger.LogWarning("External sentiment source {Code} did not match configured pattern.", source.Code);
                    continue;
                }

                var rawValue = match.Groups["value"].Success ? match.Groups["value"].Value : match.Value;
                rawValue = rawValue.Replace(",", "", StringComparison.Ordinal).Trim();
                if (decimal.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ||
                    decimal.TryParse(rawValue, NumberStyles.Any, CultureInfo.GetCultureInfo("zh-CN"), out parsed))
                {
                    values[source.Code] = parsed * source.Scale;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "External sentiment source {Code} failed.", source.Code);
            }
        }

        return values;
    }
}
