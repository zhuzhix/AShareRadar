using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AShareRadar.Infrastructure.Runtime;

namespace AShareRadar.ServiceHost.Workers;

public sealed class ExternalSentimentSdkUpdateService
{
    private const string JsonPrefix = "[external-sentiment-json]";
    private readonly ExternalSentimentSdkUpdateOptions _options;
    private readonly ExternalSentimentCsvStore _store;
    private readonly ILogger<ExternalSentimentSdkUpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastRunAt;

    public ExternalSentimentSdkUpdateService(
        ExternalSentimentSdkUpdateOptions options,
        ExternalSentimentCsvStore store,
        ILogger<ExternalSentimentSdkUpdateService> logger)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    public async Task TryUpdateAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !ShouldRun())
        {
            return;
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await RunCoreAsync(cancellationToken);
            _lastRunAt = DateTimeOffset.Now;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "External sentiment SDK update timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "External sentiment SDK update failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ShouldRun()
    {
        if (_lastRunAt is null)
        {
            return true;
        }

        var minInterval = TimeSpan.FromSeconds(Math.Clamp(_options.MinIntervalSeconds, 30, 3600));
        return DateTimeOffset.Now - _lastRunAt.Value >= minInterval;
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        var pythonPath = ExecutablePathResolver.Resolve(_options.PythonPath);
        var scriptPath = ResolvePath(_options.ScriptPath);
        ValidatePaths(pythonPath, scriptPath);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = Quote(scriptPath),
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddLocalPythonPackages(process.StartInfo, scriptPath);
        AddToken(process.StartInfo);

        var jsonLines = new List<string>();
        process.OutputDataReceived += (_, args) => HandleProcessLine(args.Data, jsonLines, isError: false);
        process.ErrorDataReceived += (_, args) => HandleProcessLine(args.Data, jsonLines, isError: true);

        _logger.LogInformation("External sentiment SDK update started. Script={ScriptPath}", scriptPath);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 10, 180)));
        await process.WaitForExitAsync(timeout.Token);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("External sentiment SDK update exited with code {ExitCode}.", process.ExitCode);
            return;
        }

        var payload = jsonLines.LastOrDefault();
        if (string.IsNullOrWhiteSpace(payload))
        {
            _logger.LogWarning("External sentiment SDK update did not return a metrics payload.");
            return;
        }

        var result = JsonSerializer.Deserialize<ExternalSentimentSdkResult>(
            payload,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            });
        if (result is null)
        {
            _logger.LogWarning("External sentiment SDK update payload is empty.");
            return;
        }

        var values = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase)
        {
            ["index_future_basis"] = result.IndexFutureBasis,
            ["option_pcr"] = result.OptionPcr
        };
        if (values.Values.All(item => !item.HasValue))
        {
            _logger.LogWarning("External sentiment SDK update returned no usable metrics.");
            return;
        }

        _store.Upsert(_options.OutputPath, DateOnly.FromDateTime(DateTime.Now), values);
        _logger.LogInformation(
            "External sentiment SDK update completed. index_future_basis={IndexFutureBasis} option_pcr={OptionPcr}",
            result.IndexFutureBasis,
            result.OptionPcr);
    }

    private void HandleProcessLine(string? line, ICollection<string> jsonLines, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.StartsWith(JsonPrefix, StringComparison.Ordinal))
        {
            jsonLines.Add(line[JsonPrefix.Length..].Trim());
            return;
        }

        if (isError)
        {
            _logger.LogDebug("[external-sentiment-sdk] {Message}", line);
        }
        else
        {
            _logger.LogInformation("[external-sentiment-sdk] {Message}", line);
        }
    }

    private static void ValidatePaths(string pythonPath, string scriptPath)
    {
        if (!ExecutablePathResolver.Exists(pythonPath))
        {
            throw new FileNotFoundException("Python runtime not found.", pythonPath);
        }

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("External sentiment SDK script not found.", scriptPath);
        }
    }

    private static void AddLocalPythonPackages(ProcessStartInfo startInfo, string scriptPath)
    {
        var packageDirs = new[]
        {
            Path.Combine(Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory, ".python_packages"),
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory, "..", "history_update", ".python_packages")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "history_update", ".python_packages"))
        }.Where(Directory.Exists).ToArray();

        if (packageDirs.Length == 0)
        {
            return;
        }

        var existing = startInfo.Environment.TryGetValue("PYTHONPATH", out var current)
            ? current
            : Environment.GetEnvironmentVariable("PYTHONPATH");
        var prefix = string.Join(Path.PathSeparator, packageDirs);
        startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existing)
            ? prefix
            : prefix + Path.PathSeparator + existing;
    }

    private void AddToken(ProcessStartInfo startInfo)
    {
        var variableName = string.IsNullOrWhiteSpace(_options.TokenEnvironmentVariable)
            ? "EASTMONEY_QUANT_TOKEN"
            : _options.TokenEnvironmentVariable.Trim();
        var token = !string.IsNullOrWhiteSpace(_options.Token)
            ? _options.Token.Trim()
            : Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(token))
        {
            startInfo.Environment[variableName] = token;
            startInfo.Environment["EASTMONEY_QUANT_TOKEN"] = token;
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

    private sealed record ExternalSentimentSdkResult(
        decimal? IndexFutureBasis,
        decimal? OptionPcr);
}
