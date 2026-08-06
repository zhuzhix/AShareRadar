using System.Diagnostics;
using AShareRadar.Application.MarketData;
using AShareRadar.Contracts.MarketData;

namespace AShareRadar.ServiceHost.Workers;

public sealed class MarketMappingUpdateService
{
    private readonly MarketMappingUpdateOptions _options;
    private readonly ISectorHeatService _sectorHeatService;
    private readonly ILogger<MarketMappingUpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private MarketMappingUpdateState _state = new();

    public MarketMappingUpdateService(
        MarketMappingUpdateOptions options,
        ISectorHeatService sectorHeatService,
        ILogger<MarketMappingUpdateService> logger)
    {
        _options = options;
        _sectorHeatService = sectorHeatService;
        _logger = logger;
    }

    public MarketMappingUpdateStatusDto GetStatus()
    {
        lock (_sync)
        {
            return BuildStatus(_state);
        }
    }

    public bool TryStartManualUpdate()
    {
        if (!_options.Enabled || !_gate.Wait(0))
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunUpdateCoreAsync("Manual", CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }
        });

        return true;
    }

    private async Task RunUpdateCoreAsync(string trigger, CancellationToken cancellationToken)
    {
        var pythonPath = ResolvePath(_options.PythonPath);
        var scriptPath = ResolvePath(_options.ScriptPath);
        var outputDataDir = ResolvePath(_options.OutputDataDir);

        StartRun(trigger, "Mapping update started.");

        try
        {
            ValidatePaths(pythonPath, scriptPath, outputDataDir);
            var arguments = BuildArguments(scriptPath, outputDataDir);
            _logger.LogInformation(
                "Market mapping update started. Trigger={Trigger} Script={ScriptPath} OutputDataDir={OutputDataDir}",
                trigger,
                scriptPath,
                outputDataDir);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            AddLocalPythonPackages(process.StartInfo, scriptPath);

            process.OutputDataReceived += (_, args) => HandleProcessLine(args.Data, isError: false);
            process.ErrorDataReceived += (_, args) => HandleProcessLine(args.Data, isError: true);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                _sectorHeatService.ReloadMappings();
                FinishRun(process.ExitCode, "Mapping update completed.", null);
                _logger.LogInformation("Market mapping update completed.");
            }
            else
            {
                var message = $"Mapping update exited with code {process.ExitCode}.";
                FinishRun(process.ExitCode, message, message);
                _logger.LogWarning("Market mapping update exited with code {ExitCode}.", process.ExitCode);
            }
        }
        catch (Exception ex)
        {
            FinishRun(null, $"Mapping update failed: {ex.Message}", ex.Message);
            _logger.LogError(ex, "Market mapping update failed.");
        }
    }

    private void StartRun(string trigger, string message)
    {
        lock (_sync)
        {
            _state = _state with
            {
                IsRunning = true,
                LastStartedAt = DateTime.Now,
                LastFinishedAt = null,
                LastExitCode = null,
                LastTrigger = trigger,
                LastMessage = message,
                LastError = null
            };
        }
    }

    private void FinishRun(int? exitCode, string message, string? error)
    {
        lock (_sync)
        {
            _state = _state with
            {
                IsRunning = false,
                LastFinishedAt = DateTime.Now,
                LastExitCode = exitCode,
                LastMessage = message,
                LastError = error
            };
        }
    }

    private void HandleProcessLine(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (isError)
        {
            _logger.LogWarning("[mapping-update] {Message}", line);
        }
        else
        {
            _logger.LogInformation("[mapping-update] {Message}", line);
        }

        lock (_sync)
        {
            var message = line.Trim();
            _state = _state with
            {
                LastMessage = message,
                LastError = isError ? message : _state.LastError
            };
        }
    }

    private MarketMappingUpdateStatusDto BuildStatus(MarketMappingUpdateState state)
    {
        var outputDataDir = ResolvePath(_options.OutputDataDir);
        var sectorMappingPath = Path.Combine(outputDataDir, "sector-mapping.csv");
        var conceptMappingPath = Path.Combine(outputDataDir, "concept-mapping.csv");
        return new MarketMappingUpdateStatusDto(
            _options.Enabled,
            state.IsRunning,
            state.LastStartedAt,
            state.LastFinishedAt,
            state.LastExitCode,
            state.LastTrigger,
            state.LastMessage,
            state.LastError,
            CountCsvRows(sectorMappingPath),
            CountCsvRows(conceptMappingPath),
            sectorMappingPath,
            conceptMappingPath);
    }

    private void ValidatePaths(string pythonPath, string scriptPath, string outputDataDir)
    {
        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException("Python runtime not found.", pythonPath);
        }

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("Mapping update script not found.", scriptPath);
        }

        if (!Directory.Exists(outputDataDir))
        {
            Directory.CreateDirectory(outputDataDir);
        }
    }

    private string BuildArguments(string scriptPath, string outputDataDir)
    {
        var args = new List<string>
        {
            Quote(scriptPath),
            "--output-dir",
            Quote(outputDataDir),
            "--sleep-seconds",
            _options.SleepSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        if (_options.Limit > 0)
        {
            args.Add("--limit");
            args.Add(_options.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_options.IncludeDynamicConcepts)
        {
            args.Add("--include-dynamic-concepts");
        }

        return string.Join(' ', args);
    }

    private static void AddLocalPythonPackages(ProcessStartInfo startInfo, string scriptPath)
    {
        var packageDirs = new[]
        {
            Path.Combine(Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory, ".python_packages"),
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory, "..", "history_update", ".python_packages"))
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

    private static int CountCsvRows(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            return Math.Max(0, File.ReadLines(path).Count() - 1);
        }
        catch
        {
            return 0;
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

    private sealed record MarketMappingUpdateState
    {
        public bool IsRunning { get; init; }

        public DateTime? LastStartedAt { get; init; }

        public DateTime? LastFinishedAt { get; init; }

        public int? LastExitCode { get; init; }

        public string LastTrigger { get; init; } = "NotRun";

        public string LastMessage { get; init; } = "Mapping update has not run.";

        public string? LastError { get; init; }
    }
}
