using System.Data;
using System.Globalization;
using System.Numerics;
using AShareRadar.Application.MarketData;
using DuckDB.NET.Data;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class DuckDbMinuteKLineCacheProvider : IBatchKLineDataProvider
{
    private const int MaxBarCount = 1200;
    private static readonly TimeSpan ActiveSessionMaxAge = TimeSpan.FromMinutes(3);

    private readonly string _duckDbPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public DuckDbMinuteKLineCacheProvider(string duckDbPath)
    {
        _duckDbPath = ResolvePath(duckDbPath);
    }

    public string ProviderName => "DuckDBMinuteCache";

    public Task<IReadOnlyList<KLineBar>> LoadKLineAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var normalizedPeriod = SimulatedKLineDataProvider.NormalizePeriod(period);
        if (!IsMinutePeriod(normalizedPeriod) || !File.Exists(_duckDbPath))
        {
            return Task.FromResult<IReadOnlyList<KLineBar>>([]);
        }

        try
        {
            var bars = LoadBars(StockSymbolNormalizer.NormalizeCode(symbol), Math.Clamp(count, 1, MaxBarCount), cancellationToken);
            return Task.FromResult<IReadOnlyList<KLineBar>>(IsFreshEnough(bars) ? bars : []);
        }
        catch (DuckDBException ex)
        {
            LogDiagnostic($"read symbol={symbol} failed. {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult<IReadOnlyList<KLineBar>>([]);
        }
        catch (IOException ex)
        {
            LogDiagnostic($"read symbol={symbol} failed. {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult<IReadOnlyList<KLineBar>>([]);
        }
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadKLinesAsync(
        IReadOnlyList<string> symbols,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var normalizedPeriod = SimulatedKLineDataProvider.NormalizePeriod(period);
        if (!IsMinutePeriod(normalizedPeriod) || symbols.Count == 0 || !File.Exists(_duckDbPath))
        {
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>>(
                new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            var normalizedSymbols = symbols
                .Select(StockSymbolNormalizer.NormalizeCode)
                .Where(item => item.Length == 6 && item.All(char.IsDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var results = LoadBarsBatch(normalizedSymbols, Math.Clamp(count, 1, MaxBarCount), cancellationToken)
                .Where(item => IsFreshEnough(item.Value))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>>(results);
        }
        catch (DuckDBException ex)
        {
            LogDiagnostic($"batch read failed. {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>>(
                new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException ex)
        {
            LogDiagnostic($"batch read failed. {ex.GetType().Name}: {ex.Message}");
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>>(
                new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase));
        }
    }

    public async Task SaveAsync(
        string symbol,
        IReadOnlyList<KLineBar> bars,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = StockSymbolNormalizer.NormalizeCode(symbol);
        if (normalizedSymbol.Length != 6 || bars.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_duckDbPath) ?? AppContext.BaseDirectory);
            using var connection = new DuckDBConnection($"Data Source={_duckDbPath}");
            connection.Open();
            EnsureTable(connection);

            using var transaction = connection.BeginTransaction();
            foreach (var bar in bars.Where(item => item.Close > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = BuildUpsertSql(normalizedSymbol, bar);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (DuckDBException ex)
        {
            LogDiagnostic($"save symbol={symbol} failed. {ex.GetType().Name}: {ex.Message}");
        }
        catch (IOException ex)
        {
            LogDiagnostic($"save symbol={symbol} failed. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private List<KLineBar> LoadBars(string symbol, int count, CancellationToken cancellationToken)
    {
        if (symbol.Length != 6 || !symbol.All(char.IsDigit))
        {
            return [];
        }

        using var connection = new DuckDBConnection($"Data Source={_duckDbPath};ACCESS_MODE=READ_ONLY");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT trading_time, open, high, low, close, volume, amount
            FROM minute_bars
            WHERE code = '{Escape(symbol)}'
            ORDER BY trading_time DESC
            LIMIT {Math.Clamp(count, 1, MaxBarCount)};
            """;

        using var reader = command.ExecuteReader();
        var bars = new List<KLineBar>(count);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            bars.Add(ReadBar(reader, 0));
        }

        bars.Reverse();
        return bars;
    }

    private Dictionary<string, IReadOnlyList<KLineBar>> LoadBarsBatch(
        IReadOnlyList<string> symbols,
        int count,
        CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        var symbolList = symbols.Select(item => $"'{Escape(item)}'").ToArray();
        using var connection = new DuckDBConnection($"Data Source={_duckDbPath};ACCESS_MODE=READ_ONLY");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH ranked AS (
                SELECT
                    code,
                    trading_time,
                    open,
                    high,
                    low,
                    close,
                    volume,
                    amount,
                    row_number() OVER (PARTITION BY code ORDER BY trading_time DESC) AS rn
                FROM minute_bars
                WHERE code IN ({string.Join(",", symbolList)})
            )
            SELECT code, trading_time, open, high, low, close, volume, amount
            FROM ranked
            WHERE rn <= {Math.Clamp(count, 1, MaxBarCount)}
            ORDER BY code ASC, trading_time ASC;
            """;

        using var reader = command.ExecuteReader();
        var grouped = new Dictionary<string, List<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!grouped.TryGetValue(symbol, out var bars))
            {
                bars = new List<KLineBar>(count);
                grouped[symbol] = bars;
            }

            bars.Add(ReadBar(reader, 1));
        }

        return grouped.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<KLineBar>)item.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static KLineBar ReadBar(IDataRecord reader, int offset)
    {
        return new KLineBar(
            ReadDateTime(reader, offset),
            ReadDecimal(reader, offset + 1),
            ReadDecimal(reader, offset + 2),
            ReadDecimal(reader, offset + 3),
            ReadDecimal(reader, offset + 4),
            ReadDecimal(reader, offset + 5),
            ReadDecimal(reader, offset + 6));
    }

    private static bool IsFreshEnough(IReadOnlyList<KLineBar> bars)
    {
        if (bars.Count == 0)
        {
            return false;
        }

        var latest = bars[^1].TradingTime;
        var now = DateTime.Now;
        if (latest.Date != now.Date)
        {
            return false;
        }

        return !IsActiveSession(now.TimeOfDay) || now - latest <= ActiveSessionMaxAge;
    }

    private static bool IsActiveSession(TimeSpan time)
    {
        return time >= new TimeSpan(9, 30, 0) && time <= new TimeSpan(11, 30, 0)
            || time >= new TimeSpan(13, 0, 0) && time <= new TimeSpan(15, 0, 0);
    }

    private static void EnsureTable(DuckDBConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS minute_bars (
                code TEXT NOT NULL,
                trading_time TIMESTAMP NOT NULL,
                open DOUBLE NOT NULL,
                high DOUBLE NOT NULL,
                low DOUBLE NOT NULL,
                close DOUBLE NOT NULL,
                volume DOUBLE NOT NULL,
                amount DOUBLE NOT NULL,
                source TEXT NOT NULL,
                updated_at TIMESTAMP NOT NULL,
                PRIMARY KEY (code, trading_time)
            );
            CREATE INDEX IF NOT EXISTS idx_minute_code_time ON minute_bars(code, trading_time);
            """;
        command.ExecuteNonQuery();
    }

    private static string BuildUpsertSql(string symbol, KLineBar bar)
    {
        var tradingTime = bar.TradingTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return $"""
            INSERT OR REPLACE INTO minute_bars (
                code,
                trading_time,
                open,
                high,
                low,
                close,
                volume,
                amount,
                source,
                updated_at
            )
            VALUES (
                '{Escape(symbol)}',
                TIMESTAMP '{tradingTime}',
                {FormatDecimal(bar.Open)},
                {FormatDecimal(bar.High)},
                {FormatDecimal(bar.Low)},
                {FormatDecimal(bar.Close)},
                {FormatDecimal(bar.Volume)},
                0,
                'provider-write-through',
                TIMESTAMP '{now}'
            );
            """;
    }

    private static bool IsMinutePeriod(string period)
    {
        return period is "minute" or "m1";
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static DateTime ReadDateTime(IDataRecord reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => dateTime,
            DateOnly dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue),
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture)
        };
    }

    private static decimal ReadDecimal(IDataRecord reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => (decimal)doubleValue,
            float floatValue => (decimal)floatValue,
            BigInteger integerValue => (decimal)integerValue,
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static void LogDiagnostic(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "duckdb-minute-kline-cache.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break market data fallback.
        }
    }
}
