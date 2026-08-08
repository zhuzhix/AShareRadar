using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AShareRadar.Application.Review;

public sealed class QlibNextDayPredictionRunner
{
    private readonly QlibNextDayPredictionOptions _options;
    private readonly QlibTomorrowPredictionCsvReader _csvReader;

    public QlibNextDayPredictionRunner(
        QlibNextDayPredictionOptions options,
        QlibTomorrowPredictionCsvReader csvReader)
    {
        _options = options;
        _csvReader = csvReader;
    }

    public async Task<QlibNextDayPredictionRunResult> RunAsync(
        DateOnly signalDate,
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken = default,
        Action<string, bool>? lineSink = null)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Qlib next-day prediction is disabled.");
        }

        if (symbols.Count == 0)
        {
            return new QlibNextDayPredictionRunResult(string.Empty, []);
        }

        if (!File.Exists(_options.ScriptPath))
        {
            throw new FileNotFoundException($"Qlib next-day prediction script not found: {_options.ScriptPath}", _options.ScriptPath);
        }

        var symbolsDirectory = ResolvePath(_options.SymbolsWorkDirectory);
        Directory.CreateDirectory(symbolsDirectory);
        Directory.CreateDirectory(_options.OutputRoot);

        var runStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var symbolsPath = Path.Combine(symbolsDirectory, $"next_day_symbols_{signalDate:yyyyMMdd}_{runStamp}.txt");
        var beforeRun = Directory.GetDirectories(_options.OutputRoot, $"next_day_direction_{signalDate:yyyyMMdd}_*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            await File.WriteAllLinesAsync(
                symbolsPath,
                symbols.Select(ToSuffixSymbol),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            await RunProcessAsync(signalDate, symbolsPath, cancellationToken, lineSink);
            var outputDirectory = FindOutputDirectory(signalDate, beforeRun);
            var csvPath = Path.Combine(outputDirectory, "tomorrow_predictions.csv");
            var predictions = _csvReader.Read(csvPath, signalDate);
            return new QlibNextDayPredictionRunResult(outputDirectory, predictions);
        }
        finally
        {
            CleanupSymbolsFile(symbolsPath);
        }
    }

    private async Task RunProcessAsync(
        DateOnly signalDate,
        string symbolsPath,
        CancellationToken cancellationToken,
        Action<string, bool>? lineSink)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, _options.TimeoutMinutes)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var output = new StringBuilder();
        var error = new StringBuilder();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _options.PowerShellPath,
            WorkingDirectory = string.IsNullOrWhiteSpace(_options.WorkingDirectory)
                ? Environment.CurrentDirectory
                : _options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(_options.ScriptPath);
        process.StartInfo.ArgumentList.Add("-SymbolsFile");
        process.StartInfo.ArgumentList.Add(symbolsPath);
        process.StartInfo.ArgumentList.Add("-SignalDate");
        process.StartInfo.ArgumentList.Add(signalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-Threads");
        process.StartInfo.ArgumentList.Add(_options.Threads.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-OutputRoot");
        process.StartInfo.ArgumentList.Add(_options.OutputRoot);

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
                lineSink?.Invoke(args.Data, false);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                error.AppendLine(args.Data);
                lineSink?.Invoke(args.Data, true);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Qlib next-day prediction timed out after {_options.TimeoutMinutes} minutes.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Qlib next-day prediction failed with exit code {process.ExitCode}.\nSTDOUT:\n{output}\nSTDERR:\n{error}");
        }
    }

    private string FindOutputDirectory(DateOnly signalDate, ISet<string> beforeRun)
    {
        var directories = Directory.GetDirectories(_options.OutputRoot, $"next_day_direction_{signalDate:yyyyMMdd}_*")
            .Where(path => !beforeRun.Contains(path))
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToArray();
        var outputDirectory = directories.FirstOrDefault()?.FullName;
        if (outputDirectory is null)
        {
            outputDirectory = Directory.GetDirectories(_options.OutputRoot, $"next_day_direction_{signalDate:yyyyMMdd}_*")
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }

        if (outputDirectory is null)
        {
            throw new DirectoryNotFoundException($"Qlib next-day prediction output directory was not created for {signalDate:yyyy-MM-dd}.");
        }

        return outputDirectory;
    }

    private void CleanupSymbolsFile(string symbolsPath)
    {
        if (!File.Exists(symbolsPath))
        {
            return;
        }

        if (_options.DeleteSymbolsFileAfterRun)
        {
            File.Delete(symbolsPath);
        }
        else
        {
            File.WriteAllText(symbolsPath, string.Empty, Encoding.UTF8);
        }
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path, Environment.CurrentDirectory);
    }

    private static string ToSuffixSymbol(string value)
    {
        var code = value.Trim();
        if (code.Contains('.', StringComparison.Ordinal))
        {
            return code.ToUpperInvariant();
        }

        code = code.PadLeft(6, '0');
        var exchange = code.StartsWith("6", StringComparison.Ordinal)
            || code.StartsWith("5", StringComparison.Ordinal)
            || code.StartsWith("9", StringComparison.Ordinal)
                ? "SH"
                : "SZ";
        return $"{code}.{exchange}";
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
            // Best effort cleanup; the caller receives the original timeout/failure.
        }
    }
}
