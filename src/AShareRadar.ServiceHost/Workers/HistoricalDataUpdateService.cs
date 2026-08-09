using System.Diagnostics;
using AShareRadar.Contracts.History;
using AShareRadar.ServiceHost.Services;
using DuckDB.NET.Data;

namespace AShareRadar.ServiceHost.Workers;

public sealed class HistoricalDataUpdateService
{
    private readonly HistoricalDataUpdateOptions _options;
    private readonly ILogger<HistoricalDataUpdateService> _logger;
    private readonly StockNameMapSyncService _stockNameMapSyncService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private HistoricalDataUpdateState _state = new();

    public HistoricalDataUpdateService(
        HistoricalDataUpdateOptions options,
        ILogger<HistoricalDataUpdateService> logger,
        StockNameMapSyncService stockNameMapSyncService)
    {
        _options = options;
        _logger = logger;
        _stockNameMapSyncService = stockNameMapSyncService;
    }

    public HistoricalDataUpdateStatusDto GetStatus()
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
                await RunUpdateCoreAsync("manual", null, CancellationToken.None, null);
            }
            finally
            {
                _gate.Release();
            }
        });

        return true;
    }

    public async Task<bool> RunManualJobAsync(
        CancellationToken cancellationToken,
        Action<string, bool>? lineSink = null)
    {
        if (!_options.Enabled || !await _gate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("历史更新任务已在运行，不能重复启动。");
        }

        try
        {
            return await RunUpdateCoreAsync("job", null, cancellationToken, lineSink);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryRunScheduledAsync(
        DateOnly targetDate,
        string trigger,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !await _gate.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            return await RunUpdateCoreAsync(trigger, targetDate, cancellationToken, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RunUpdateCoreAsync(
        string trigger,
        DateOnly? targetDate,
        CancellationToken cancellationToken,
        Action<string, bool>? lineSink)
    {
        var pythonPath = ResolvePath(_options.PythonPath);
        var scriptPath = ResolvePath(_options.ScriptPath);
        var dataDir = ResolvePath(_options.DataDir);

        StartRun(trigger, "历史数据更新已启动。");

        try
        {
            ValidatePaths(pythonPath, scriptPath, dataDir);
            await _stockNameMapSyncService.SyncAsync(dataDir, cancellationToken);
            var arguments = BuildArguments(scriptPath, dataDir, targetDate);
            _logger.LogInformation(
                "Historical data update started. Trigger={Trigger} Script={ScriptPath} DataDir={DataDir}",
                trigger,
                scriptPath,
                dataDir);

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

            process.OutputDataReceived += (_, args) => HandleProcessLine(args.Data, isError: false, lineSink);
            process.ErrorDataReceived += (_, args) => HandleProcessLine(args.Data, isError: true, lineSink);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                FinishRun(process.ExitCode, "历史数据更新完成。", null);
                _logger.LogInformation("Historical data update completed.");
                return true;
            }
            else
            {
                var message = $"历史数据更新退出，代码 {process.ExitCode}。";
                FinishRun(process.ExitCode, message, message);
                _logger.LogWarning("Historical data update exited with code {ExitCode}.", process.ExitCode);
                return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FinishRun(null, "历史数据更新已取消。", "任务取消");
            throw;
        }
        catch (Exception ex)
        {
            FinishRun(null, $"历史数据更新失败：{ex.Message}", ex.Message);
            _logger.LogError(ex, "Historical data update failed.");
            return false;
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
                LastError = null,
                LatestTradingDate = TryLoadLatestTradingDate()
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
                LastError = error,
                LatestTradingDate = TryLoadLatestTradingDate()
            };
        }
    }

    private void HandleProcessLine(string? line, bool isError, Action<string, bool>? lineSink)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (isError)
        {
            _logger.LogWarning("[history-update] {Message}", line);
        }
        else
        {
            _logger.LogInformation("[history-update] {Message}", line);
        }

        lineSink?.Invoke(line, isError);

        lock (_sync)
        {
            var message = line.Trim();
            _state = _state with
            {
                LastMessage = message,
                LastError = isError ? message : _state.LastError
            };

            if (TryParseMissingDates(message, out var missingDates))
            {
                _state = _state with { MissingTradingDates = missingDates };
            }
        }
    }

    private HistoricalDataUpdateStatusDto BuildStatus(HistoricalDataUpdateState state)
    {
        return new HistoricalDataUpdateStatusDto(
            _options.Enabled,
            state.IsRunning,
            state.LastStartedAt,
            state.LastFinishedAt,
            state.LastExitCode,
            state.LastTrigger,
            state.LastMessage,
            state.LastError,
            TryLoadLatestTradingDate() ?? state.LatestTradingDate,
            state.MissingTradingDates,
            _options.RunAfterTime,
            _options.CheckIntervalMinutes);
    }

    private static bool TryParseMissingDates(string message, out DateOnly[] missingDates)
    {
        missingDates = [];
        const string prefix = "[history-update] missing_dates=";
        var index = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            if (message.Contains("no missing trading day", StringComparison.OrdinalIgnoreCase))
            {
                missingDates = [];
                return true;
            }

            return false;
        }

        var raw = message[(index + prefix.Length)..].Trim();
        missingDates = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => DateOnly.TryParse(item, out var date) ? date : (DateOnly?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        return true;
    }

    private DateOnly? TryLoadLatestTradingDate()
    {
        var duckDbPath = Path.Combine(ResolvePath(_options.DataDir), "ashare.duckdb");
        if (!File.Exists(duckDbPath))
        {
            return null;
        }

        try
        {
            using var connection = new DuckDBConnection($"Data Source={duckDbPath};ACCESS_MODE=READ_ONLY");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT max(date) FROM daily_bars";
            var value = command.ExecuteScalar();
            return value switch
            {
                DateOnly date => date,
                DateTime dateTime => DateOnly.FromDateTime(dateTime),
                string text when DateOnly.TryParse(text, out var date) => date,
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load latest trading date from DuckDB.");
            return null;
        }
    }

    private void ValidatePaths(string pythonPath, string scriptPath, string dataDir)
    {
        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException("未找到 Python 运行环境", pythonPath);
        }

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("未找到历史数据更新脚本", scriptPath);
        }

        if (!Directory.Exists(dataDir))
        {
            throw new DirectoryNotFoundException($"未找到历史数据目录：{dataDir}");
        }
    }

    private string BuildArguments(string scriptPath, string dataDir, DateOnly? targetDate)
    {
        var args = new List<string>
        {
            Quote(scriptPath),
            "--data-dir",
            Quote(dataDir),
            "--name-map",
            Quote(Path.Combine(dataDir, "stock-name-map.json")),
            "--adjustflag",
            Quote(string.IsNullOrWhiteSpace(_options.AdjustFlag) ? "2" : _options.AdjustFlag)
        };

        if (targetDate.HasValue)
        {
            args.Add("--end");
            args.Add(Quote(targetDate.Value.ToString("yyyy-MM-dd")));
        }

        if (_options.Limit > 0)
        {
            args.Add("--limit");
            args.Add(_options.Limit.ToString());
        }

        if (_options.IncludeWeekly)
        {
            args.Add("--include-weekly");
        }

        if (_options.Rebuild)
        {
            args.Add("--rebuild");
            args.Add("--start");
            args.Add(Quote(string.IsNullOrWhiteSpace(_options.StartDate) ? "2015-01-01" : _options.StartDate));
        }

        return string.Join(' ', args);
    }

    private static void AddLocalPythonPackages(ProcessStartInfo startInfo, string scriptPath)
    {
        var packageDir = Path.Combine(Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory, ".python_packages");
        if (!Directory.Exists(packageDir))
        {
            return;
        }

        var existing = startInfo.Environment.TryGetValue("PYTHONPATH", out var current)
            ? current
            : Environment.GetEnvironmentVariable("PYTHONPATH");
        startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existing)
            ? packageDir
            : packageDir + Path.PathSeparator + existing;
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

    private sealed record HistoricalDataUpdateState
    {
        public bool IsRunning { get; init; }

        public DateTime? LastStartedAt { get; init; }

        public DateTime? LastFinishedAt { get; init; }

        public int? LastExitCode { get; init; }

        public string LastTrigger { get; init; } = "未运行";

        public string LastMessage { get; init; } = "历史数据更新任务未运行。";

        public string? LastError { get; init; }

        public DateOnly? LatestTradingDate { get; init; }

        public DateOnly[] MissingTradingDates { get; init; } = [];
    }
}
