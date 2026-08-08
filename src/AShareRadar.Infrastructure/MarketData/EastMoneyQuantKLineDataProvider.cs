using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AShareRadar.Application.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyQuantKLineDataProvider : IKLineDataProvider
{
    private readonly EastMoneyQuantOptions _options;
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public EastMoneyQuantKLineDataProvider(EastMoneyQuantOptions options)
    {
        _options = options;
    }

    public string ProviderName => "EastMoneyQuantKLine";

    public async Task<IReadOnlyList<KLineBar>> LoadKLineAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return [];
        }

        var normalizedPeriod = SimulatedKLineDataProvider.NormalizePeriod(period);
        if (!IsIntradayPeriod(normalizedPeriod))
        {
            return [];
        }

        var normalizedSymbol = StockSymbolNormalizer.NormalizeCode(symbol);
        if (string.IsNullOrWhiteSpace(normalizedSymbol))
        {
            return [];
        }

        var takeCount = Math.Clamp(count, 1, 1200);
        var cacheKey = $"{normalizedSymbol}:{normalizedPeriod}:{takeCount}";
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(cacheKey, out var cached)
                && DateTimeOffset.Now - cached.CachedAt
                    < TimeSpan.FromSeconds(Math.Clamp(_options.KLineCacheSeconds, 0, 300)))
            {
                return cached.Bars;
            }
        }

        var bars = await LoadFromScriptAsync(normalizedSymbol, normalizedPeriod, takeCount, cancellationToken);
        lock (_cacheSync)
        {
            _cache[cacheKey] = new CacheEntry(DateTimeOffset.Now, bars);
        }

        return bars;
    }

    private async Task<IReadOnlyList<KLineBar>> LoadFromScriptAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var pythonPath = ResolvePath(_options.PythonPath);
        var scriptPath = ResolvePath(_options.KLineScriptPath);
        if (!File.Exists(pythonPath) || !File.Exists(scriptPath))
        {
            return [];
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 5, 300)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var arguments = string.Join(' ', new[]
        {
            Quote(scriptPath),
            "--symbol",
            Quote(symbol),
            "--period",
            Quote(period),
            "--count",
            count.ToString(CultureInfo.InvariantCulture)
        });

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        AddEnvironment(process.StartInfo);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            LogDiagnostic($"timeout after {_options.RequestTimeoutSeconds}s symbol={symbol} period={period}");
            return [];
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            LogDiagnostic($"exit={process.ExitCode} symbol={symbol} period={period}. {TrimForLog(stderr)}");
            return [];
        }

        try
        {
            var payload = JsonSerializer.Deserialize<EastMoneyQuantKLinePayload>(
                stdout,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return payload?.Bars
                .Select(ToKLineBar)
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderBy(item => item.TradingTime)
                .TakeLast(count)
                .ToArray() ?? [];
        }
        catch (JsonException ex)
        {
            LogDiagnostic($"json symbol={symbol} period={period}. {ex.Message}. stdout={TrimForLog(stdout)} stderr={TrimForLog(stderr)}");
            return [];
        }
    }

    private void AddEnvironment(ProcessStartInfo startInfo)
    {
        var tokenName = string.IsNullOrWhiteSpace(_options.TokenEnvironmentVariable)
            ? "EASTMONEY_QUANT_TOKEN"
            : _options.TokenEnvironmentVariable.Trim();
        var token = !string.IsNullOrWhiteSpace(_options.Token)
            ? _options.Token.Trim()
            : Environment.GetEnvironmentVariable(tokenName)
            ?? Environment.GetEnvironmentVariable(tokenName, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(tokenName, EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(token))
        {
            startInfo.Environment[tokenName] = token;
            if (tokenName != "EASTMONEY_QUANT_TOKEN")
            {
                startInfo.Environment["EASTMONEY_QUANT_TOKEN"] = token;
            }
        }
    }

    private static KLineBar? ToKLineBar(EastMoneyQuantKLineBarPayload payload)
    {
        if (!DateTime.TryParse(payload.TradingTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var tradingTime))
        {
            return null;
        }

        if (payload.Close <= 0)
        {
            return null;
        }

        return new KLineBar(
            tradingTime,
            payload.Open,
            payload.High,
            payload.Low,
            payload.Close,
            payload.Volume,
            payload.Amount);
    }

    private static bool IsIntradayPeriod(string period)
    {
        return period is "minute" or "five-day" or "m1" or "m5" or "m15" or "m30" or "m60";
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string TrimForLog(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Timeout cleanup is best effort.
        }
    }

    private static void LogDiagnostic(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "eastmoney-quant-kline-provider.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break provider fallback.
        }
    }

    private sealed record CacheEntry(DateTimeOffset CachedAt, IReadOnlyList<KLineBar> Bars);

    private sealed record EastMoneyQuantKLinePayload(
        string SnapshotTime,
        string ProviderName,
        string Symbol,
        string Period,
        string Frequency,
        int Requested,
        int Returned,
        decimal ElapsedSeconds,
        IReadOnlyList<EastMoneyQuantKLineBarPayload> Bars);

    private sealed record EastMoneyQuantKLineBarPayload(
        string TradingTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume,
        decimal Amount);
}
