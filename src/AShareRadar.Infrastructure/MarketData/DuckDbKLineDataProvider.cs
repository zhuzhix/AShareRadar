using System.Data;
using System.Numerics;
using AShareRadar.Application.MarketData;
using DuckDB.NET.Data;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class DuckDbKLineDataProvider : IBatchKLineDataProvider, IHistoricalSymbolProvider, IKLineDataProviderDiagnostics
{
    private readonly string _duckDbPath;
    private readonly IKLineDataProvider _fallbackProvider;

    public DuckDbKLineDataProvider(string duckDbPath, IKLineDataProvider fallbackProvider)
    {
        _duckDbPath = ResolvePath(duckDbPath);
        _fallbackProvider = fallbackProvider;
    }

    public string ProviderName => "DuckDB";

    public bool LastFallbackUsed { get; private set; }

    public void Reset()
    {
        LastFallbackUsed = false;
    }

    public Task<IReadOnlyList<string>> LoadSymbolsAsync(
        string stockPool,
        int count,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_duckDbPath))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        try
        {
            return Task.FromResult<IReadOnlyList<string>>(LoadSymbols(
                stockPool,
                Math.Clamp(count, 1, 6000),
                cancellationToken));
        }
        catch (DuckDBException ex)
        {
            LogFallback(ex);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        catch (IOException ex)
        {
            LogFallback(ex);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    public async Task<IReadOnlyList<KLineBar>> LoadKLineAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var normalizedPeriod = SimulatedKLineDataProvider.NormalizePeriod(period);
        if (IsIntradayPeriod(normalizedPeriod) || !File.Exists(_duckDbPath))
        {
            LastFallbackUsed = true;
            return await _fallbackProvider.LoadKLineAsync(symbol, period, count, cancellationToken);
        }

        var takeCount = Math.Clamp(count, 1, 720);
        var duckDbCode = ToDuckDbCode(symbol);
        if (duckDbCode.Length == 0)
        {
            LastFallbackUsed = true;
            return await _fallbackProvider.LoadKLineAsync(symbol, period, count, cancellationToken);
        }

        try
        {
            var bars = LoadBars(duckDbCode, normalizedPeriod, takeCount, cancellationToken);
            if (bars.Count > 0)
            {
                return bars;
            }

            LastFallbackUsed = true;
            return await _fallbackProvider.LoadKLineAsync(symbol, period, count, cancellationToken);
        }
        catch (DuckDBException ex)
        {
            LogFallback(ex);
            LastFallbackUsed = true;
            return await _fallbackProvider.LoadKLineAsync(symbol, period, count, cancellationToken);
        }
        catch (IOException ex)
        {
            LogFallback(ex);
            LastFallbackUsed = true;
            return await _fallbackProvider.LoadKLineAsync(symbol, period, count, cancellationToken);
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>> LoadKLinesAsync(
        IReadOnlyList<string> symbols,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var normalizedPeriod = SimulatedKLineDataProvider.NormalizePeriod(period);
        if (symbols.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        }

        if (IsIntradayPeriod(normalizedPeriod) || !File.Exists(_duckDbPath))
        {
            LastFallbackUsed = true;
            return await LoadFallbackBatchAsync(symbols, period, count, cancellationToken);
        }

        var requestedSymbols = symbols
            .Select(StockSymbolNormalizer.NormalizeCode)
            .Where(item => item.Length == 6 && item.All(ch => ch is >= '0' and <= '9'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedSymbols.Length == 0)
        {
            LastFallbackUsed = true;
            return await LoadFallbackBatchAsync(symbols, period, count, cancellationToken);
        }

        try
        {
            var results = LoadBarsBatch(requestedSymbols, normalizedPeriod, Math.Clamp(count, 1, 720), cancellationToken);
            var missingSymbols = requestedSymbols
                .Where(symbol => !results.ContainsKey(symbol))
                .ToArray();
            if (missingSymbols.Length > 0)
            {
                LastFallbackUsed = true;
                foreach (var item in await LoadFallbackBatchAsync(missingSymbols, period, count, cancellationToken))
                {
                    results[item.Key] = item.Value;
                }
            }

            return results;
        }
        catch (DuckDBException ex)
        {
            LogFallback(ex);
            LastFallbackUsed = true;
            return await LoadFallbackBatchAsync(symbols, period, count, cancellationToken);
        }
        catch (IOException ex)
        {
            LogFallback(ex);
            LastFallbackUsed = true;
            return await LoadFallbackBatchAsync(symbols, period, count, cancellationToken);
        }
    }

    private List<KLineBar> LoadBars(
        string duckDbCode,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        using var connection = new DuckDBConnection($"Data Source={_duckDbPath};ACCESS_MODE=READ_ONLY");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = BuildSql(duckDbCode, period, count);

        using var reader = command.ExecuteReader();
        var bars = new List<KLineBar>(count);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            bars.Add(new KLineBar(
                ReadDateTime(reader, 0),
                ReadDecimal(reader, 1),
                ReadDecimal(reader, 2),
                ReadDecimal(reader, 3),
                ReadDecimal(reader, 4),
                ReadDecimal(reader, 5)));
        }

        bars.Reverse();
        return bars;
    }

    private Dictionary<string, IReadOnlyList<KLineBar>> LoadBarsBatch(
        IReadOnlyList<string> symbols,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        using var connection = new DuckDBConnection($"Data Source={_duckDbPath};ACCESS_MODE=READ_ONLY");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = BuildBatchSql(symbols.Select(ToDuckDbCode).Where(item => item.Length > 0), period, count);

        using var reader = command.ExecuteReader();
        var grouped = new Dictionary<string, List<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = FromDuckDbCode(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
            if (!grouped.TryGetValue(symbol, out var bars))
            {
                bars = new List<KLineBar>(count);
                grouped[symbol] = bars;
            }

            bars.Add(new KLineBar(
                ReadDateTime(reader, 1),
                ReadDecimal(reader, 2),
                ReadDecimal(reader, 3),
                ReadDecimal(reader, 4),
                ReadDecimal(reader, 5),
                ReadDecimal(reader, 6)));
        }

        return grouped.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<KLineBar>)item.Value.OrderBy(bar => bar.TradingTime).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, IReadOnlyList<KLineBar>>> LoadFallbackBatchAsync(
        IReadOnlyList<string> symbols,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            var bars = await _fallbackProvider.LoadKLineAsync(symbol, period, count, cancellationToken);
            if (bars.Count > 0)
            {
                results[StockSymbolNormalizer.NormalizeCode(symbol)] = bars;
            }
        }

        return results;
    }

    private static bool IsIntradayPeriod(string period)
    {
        return period is "minute" or "five-day" or "m1" or "m5" or "m15" or "m30" or "m60";
    }

    private List<string> LoadSymbols(
        string stockPool,
        int count,
        CancellationToken cancellationToken)
    {
        using var connection = new DuckDBConnection($"Data Source={_duckDbPath};ACCESS_MODE=READ_ONLY");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = BuildSymbolPoolSql(stockPool, count);

        using var reader = command.ExecuteReader();
        var symbols = new List<string>(count);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var code = Convert.ToString(reader.GetValue(0)) ?? string.Empty;
            var symbol = FromDuckDbCode(code);
            if (symbol.Length == 6)
            {
                symbols.Add(symbol);
            }
        }

        return symbols;
    }

    private static string BuildSymbolPoolSql(string stockPool, int count)
    {
        var safeCount = Math.Clamp(count, 1, 6000);
        if (string.Equals(stockPool, "AShare", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
                WITH latest AS (
                    SELECT max(date) AS latest_date
                    FROM daily_bars
                    WHERE adjustflag = '2'
                      AND tradestatus = 1
                      AND isST = 0
                )
                SELECT
                    code
                FROM daily_bars, latest
                WHERE adjustflag = '2'
                  AND tradestatus = 1
                  AND isST = 0
                  AND close >= 1
                  AND date >= latest_date - INTERVAL 260 DAY
                  AND (
                       code LIKE 'sh.600%'
                    OR code LIKE 'sh.601%'
                    OR code LIKE 'sh.603%'
                    OR code LIKE 'sh.605%'
                    OR code LIKE 'sh.688%'
                    OR code LIKE 'sh.689%'
                    OR code LIKE 'sz.000%'
                    OR code LIKE 'sz.001%'
                    OR code LIKE 'sz.002%'
                    OR code LIKE 'sz.003%'
                    OR code LIKE 'sz.300%'
                    OR code LIKE 'sz.301%'
                  )
                  AND upper(code_name) NOT LIKE '%ST%'
                GROUP BY code, latest_date
                HAVING count(*) >= 120
                   AND max(date) >= latest_date - INTERVAL 10 DAY
                ORDER BY code ASC
                LIMIT {safeCount};
                """;
        }

        if (string.Equals(stockPool, "RecentActive", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
                WITH latest AS (
                    SELECT max(date) AS latest_date
                    FROM daily_bars
                    WHERE adjustflag = '2'
                      AND tradestatus = 1
                      AND isST = 0
                )
                SELECT
                    code
                FROM daily_bars, latest
                WHERE adjustflag = '2'
                  AND tradestatus = 1
                  AND isST = 0
                  AND close >= 2
                  AND amount >= 10000000
                  AND date >= latest_date - INTERVAL 30 DAY
                  AND upper(code_name) NOT LIKE '%ST%'
                  AND code_name NOT LIKE '%退%'
                GROUP BY code, latest_date
                HAVING count(*) >= 20
                   AND avg(amount) >= 20000000
                ORDER BY avg(amount) DESC, avg(volume) DESC, code ASC
                LIMIT {safeCount};
                """;
        }

        return $"""
            WITH latest AS (
                SELECT max(date) AS latest_date
                FROM daily_bars
                WHERE adjustflag = '2'
                  AND tradestatus = 1
                  AND isST = 0
            )
            SELECT
                code
            FROM daily_bars, latest
            WHERE adjustflag = '2'
              AND tradestatus = 1
              AND isST = 0
              AND close >= 2
              AND date >= latest_date - INTERVAL 260 DAY
              AND upper(code_name) NOT LIKE '%ST%'
              AND code_name NOT LIKE '%退%'
            GROUP BY code, latest_date
            HAVING count(*) >= 120
               AND avg(CASE WHEN date >= latest_date - INTERVAL 60 DAY THEN amount END) >= 20000000
            ORDER BY max(date) DESC,
                     avg(CASE WHEN date >= latest_date - INTERVAL 60 DAY THEN amount END) DESC,
                     code ASC
            LIMIT {safeCount};
            """;
    }

    private static string BuildSql(string duckDbCode, string period, int count)
    {
        var escapedCode = duckDbCode.Replace("'", "''", StringComparison.Ordinal);
        var safeCount = Math.Clamp(count, 1, 720);

        if (period == "week")
        {
            return $"""
                SELECT date, open, high, low, close, volume
                FROM weekly_bars
                WHERE code = '{escapedCode}'
                  AND adjustflag = '2'
                  AND tradestatus = 1
                ORDER BY date DESC
                LIMIT {safeCount};
                """;
        }

        if (period == "month")
        {
            return $"""
                WITH source AS (
                    SELECT
                        CAST(date_trunc('month', date) AS DATE) AS trading_time,
                        date,
                        open,
                        high,
                        low,
                        close,
                        volume,
                        row_number() OVER (PARTITION BY CAST(date_trunc('month', date) AS DATE) ORDER BY date ASC) AS open_rank,
                        row_number() OVER (PARTITION BY CAST(date_trunc('month', date) AS DATE) ORDER BY date DESC) AS close_rank
                    FROM daily_bars
                    WHERE code = '{escapedCode}'
                      AND adjustflag = '2'
                      AND tradestatus = 1
                ),
                grouped AS (
                    SELECT
                        trading_time,
                        max(CASE WHEN open_rank = 1 THEN open END) AS open,
                        max(high) AS high,
                        min(low) AS low,
                        max(CASE WHEN close_rank = 1 THEN close END) AS close,
                        sum(volume) AS volume
                    FROM source
                    GROUP BY trading_time
                )
                SELECT trading_time, open, high, low, close, volume
                FROM grouped
                ORDER BY trading_time DESC
                LIMIT {safeCount};
                """;
        }

        return $"""
            SELECT date, open, high, low, close, volume
            FROM daily_bars
            WHERE code = '{escapedCode}'
              AND adjustflag = '2'
              AND tradestatus = 1
            ORDER BY date DESC
            LIMIT {safeCount};
            """;
    }

    private static string BuildBatchSql(IEnumerable<string> duckDbCodes, string period, int count)
    {
        var codeList = duckDbCodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(item => "'" + item.Replace("'", "''", StringComparison.Ordinal) + "'")
            .ToArray();
        var safeCount = Math.Clamp(count, 1, 720);
        if (codeList.Length == 0)
        {
            codeList = ["''"];
        }

        if (period == "week")
        {
            return $"""
            WITH ranked AS (
                SELECT
                    code,
                    date,
                    open,
                    high,
                    low,
                    close,
                    volume,
                    row_number() OVER (PARTITION BY code ORDER BY date DESC) AS rn
                FROM weekly_bars
                WHERE code IN ({string.Join(",", codeList)})
                  AND adjustflag = '2'
                  AND tradestatus = 1
            )
            SELECT code, date, open, high, low, close, volume
            FROM ranked
            WHERE rn <= {safeCount}
            ORDER BY code ASC, date DESC;
            """;
        }

        if (period == "month")
        {
            return $"""
                WITH source AS (
                    SELECT
                        code,
                        CAST(date_trunc('month', date) AS DATE) AS trading_time,
                        date,
                        open,
                        high,
                        low,
                        close,
                        volume,
                        row_number() OVER (PARTITION BY code, CAST(date_trunc('month', date) AS DATE) ORDER BY date ASC) AS open_rank,
                        row_number() OVER (PARTITION BY code, CAST(date_trunc('month', date) AS DATE) ORDER BY date DESC) AS close_rank
                    FROM daily_bars
                    WHERE code IN ({string.Join(",", codeList)})
                      AND adjustflag = '2'
                      AND tradestatus = 1
                ),
                grouped AS (
                    SELECT
                        code,
                        trading_time,
                        max(CASE WHEN open_rank = 1 THEN open END) AS open,
                        max(high) AS high,
                        min(low) AS low,
                        max(CASE WHEN close_rank = 1 THEN close END) AS close,
                        sum(volume) AS volume
                    FROM source
                    GROUP BY code, trading_time
                ),
                ranked AS (
                    SELECT *,
                           row_number() OVER (PARTITION BY code ORDER BY trading_time DESC) AS rn
                    FROM grouped
                )
                SELECT code, trading_time, open, high, low, close, volume
                FROM ranked
                WHERE rn <= {safeCount}
                ORDER BY code ASC, trading_time DESC;
                """;
        }

        return $"""
            WITH ranked AS (
                SELECT
                    code,
                    date,
                    open,
                    high,
                    low,
                    close,
                    volume,
                    row_number() OVER (PARTITION BY code ORDER BY date DESC) AS rn
                FROM daily_bars
                WHERE code IN ({string.Join(",", codeList)})
                  AND adjustflag = '2'
                  AND tradestatus = 1
            )
            SELECT code, date, open, high, low, close, volume
            FROM ranked
            WHERE rn <= {safeCount}
            ORDER BY code ASC, date DESC;
            """;
    }

    private static string ToDuckDbCode(string symbol)
    {
        var code = StockSymbolNormalizer.NormalizeCode(symbol);
        if (code.Length != 6 || code.Any(ch => ch is < '0' or > '9'))
        {
            return string.Empty;
        }

        return code.StartsWith('6') ? $"sh.{code}" : $"sz.{code}";
    }

    private static string FromDuckDbCode(string code)
    {
        var value = code.Trim().ToLowerInvariant();
        if ((value.StartsWith("sh.") || value.StartsWith("sz.")) && value.Length == 9)
        {
            return value[3..];
        }

        return StockSymbolNormalizer.NormalizeCode(value);
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
            _ => Convert.ToDateTime(value)
        };
    }

    private static decimal ReadDecimal(IDataRecord reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal decimalValue => decimalValue,
            BigInteger integerValue => (decimal)integerValue,
            _ => Convert.ToDecimal(value)
        };
    }

    private static void LogFallback(Exception exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "duckdb-kline-fallback.log"),
                $"{DateTimeOffset.Now:O} DuckDB K-line provider fell back to simulation. {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break market data fallback.
        }
    }
}
