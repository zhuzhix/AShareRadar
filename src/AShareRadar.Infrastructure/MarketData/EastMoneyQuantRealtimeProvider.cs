using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyQuantRealtimeProvider : IMarketDataProvider
{
    private readonly EastMoneyQuantOptions _options;
    private readonly MarketDataOptions _marketDataOptions;
    private readonly object _cacheSync = new();
    private MarketSnapshot? _cachedSnapshot;

    public EastMoneyQuantRealtimeProvider(
        EastMoneyQuantOptions options,
        MarketDataOptions marketDataOptions)
    {
        _options = options;
        _marketDataOptions = marketDataOptions;
    }

    public string ProviderName => "EastMoneyQuant";

    public async Task<MarketSnapshot> LoadMarketSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("EastMoney Quant provider is disabled.");
        }

        lock (_cacheSync)
        {
            if (_cachedSnapshot is not null
                && DateTimeOffset.Now - _cachedSnapshot.SnapshotTime
                    < TimeSpan.FromSeconds(Math.Clamp(_options.SnapshotCacheSeconds, 0, 300)))
            {
                return _cachedSnapshot;
            }
        }

        var pythonPath = ResolvePath(_options.PythonPath);
        var scriptPath = ResolvePath(_options.RealtimeScriptPath);
        var duckDbPath = ResolvePath(_options.DuckDbPath);
        ValidatePaths(pythonPath, scriptPath, duckDbPath);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 5, 300)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var arguments = string.Join(' ', new[]
        {
            Quote(scriptPath),
            "--db",
            Quote(duckDbPath),
            "--max-symbols",
            Math.Clamp(_marketDataOptions.MaxSymbols, 1, 6000).ToString(CultureInfo.InvariantCulture),
            "--batch-size",
            Math.Clamp(_options.BatchSize, 1, 2000).ToString(CultureInfo.InvariantCulture)
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
            var timedOutStderr = await TryReadAsync(stderrTask);
            LogDiagnostic($"timeout after {_options.RequestTimeoutSeconds}s. {TrimForLog(timedOutStderr)}");
            throw new TimeoutException($"EastMoney Quant realtime script timed out after {_options.RequestTimeoutSeconds} seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            LogDiagnostic($"exit={process.ExitCode}. {TrimForLog(stderr)}");
            throw new InvalidOperationException(
                $"EastMoney Quant realtime script failed with code {process.ExitCode}. {TrimForLog(stderr)}");
        }

        var payload = JsonSerializer.Deserialize<EastMoneyQuantSnapshotPayload>(
            stdout,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (payload is null || payload.Quotes.Count == 0)
        {
            LogDiagnostic($"empty snapshot. stdout={TrimForLog(stdout)} stderr={TrimForLog(stderr)}");
            throw new InvalidOperationException("EastMoney Quant realtime script returned an empty snapshot.");
        }

        var snapshotTime = TryParseTime(payload.SnapshotTime) ?? DateTimeOffset.Now;
        var result = new MarketSnapshot(
            snapshotTime,
            ProviderName,
            payload.Quotes.Select(ToStockQuote).Where(item => item is not null).Select(item => item!).ToArray());
        lock (_cacheSync)
        {
            _cachedSnapshot = result;
        }

        return result;
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

    private static StockQuote? ToStockQuote(EastMoneyQuantQuotePayload quote)
    {
        if (quote.Price <= 0 || string.IsNullOrWhiteSpace(quote.Symbol))
        {
            return null;
        }

        return new StockQuote(
            quote.Symbol,
            string.IsNullOrWhiteSpace(quote.Name) ? StockSymbolNormalizer.NormalizeCode(quote.Symbol) : quote.Name,
            quote.Price,
            quote.ChangePercent,
            quote.VolumeRatio,
            quote.TurnoverRate,
            quote.Amount,
            TryParseTime(quote.QuoteTime) ?? DateTimeOffset.Now,
            quote.Open,
            quote.High,
            quote.Low,
            quote.Volume);
    }

    private static DateTimeOffset? TryParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    private static void ValidatePaths(string pythonPath, string scriptPath, string duckDbPath)
    {
        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException("EastMoney Quant Python runtime was not found.", pythonPath);
        }

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("EastMoney Quant realtime script was not found.", scriptPath);
        }

        if (!File.Exists(duckDbPath))
        {
            throw new FileNotFoundException("DuckDB historical database was not found.", duckDbPath);
        }
    }

    private static string ResolvePath(string path)
    {
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

    private static async Task<string> TryReadAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void LogDiagnostic(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "eastmoney-quant-provider.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break provider fallback.
        }
    }

    private sealed record EastMoneyQuantSnapshotPayload(
        string SnapshotTime,
        string ProviderName,
        int Requested,
        int Returned,
        decimal ElapsedSeconds,
        IReadOnlyList<EastMoneyQuantQuotePayload> Quotes);

    private sealed record EastMoneyQuantQuotePayload(
        string Symbol,
        string Name,
        decimal Price,
        decimal ChangePercent,
        decimal VolumeRatio,
        decimal TurnoverRate,
        decimal Amount,
        string QuoteTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Volume);
}
