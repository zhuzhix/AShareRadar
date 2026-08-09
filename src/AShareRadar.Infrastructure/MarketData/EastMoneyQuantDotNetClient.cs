using System.Data;
using System.Globalization;
using AShareRadar.Application.MarketData;
using GMSDK;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyQuantDotNetClient
{
    private readonly EastMoneyQuantDotNetOptions _options;
    private readonly SemaphoreSlim _sdkLock = new(1, 1);
    private bool _tokenInitialized;

    public EastMoneyQuantDotNetClient(EastMoneyQuantDotNetOptions options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<EastMoneyQuantDotNetQuote>> LoadCurrentAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || symbols.Count == 0)
        {
            return [];
        }

        await _sdkLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => LoadCurrentCore(symbols, cancellationToken), cancellationToken);
        }
        finally
        {
            _sdkLock.Release();
        }
    }

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

        await _sdkLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => LoadKLineCore(symbol, period, count, cancellationToken), cancellationToken);
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    public async Task<int> LoadAshareUniverseCountAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        await _sdkLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => LoadAshareUniverseCountCore(cancellationToken), cancellationToken);
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> LoadAshareUniverseSymbolsAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return [];
        }

        await _sdkLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => LoadAshareUniverseSymbolsCore(cancellationToken), cancellationToken);
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    private IReadOnlyList<EastMoneyQuantDotNetQuote> LoadCurrentCore(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        EnsureToken();

        var gmSymbols = symbols
            .Select(ToGmSymbol)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (gmSymbols.Length == 0)
        {
            return [];
        }

        var joined = string.Join(',', gmSymbols);
        var ticks = GMApi.Current(joined, false);
        if (ticks.status != 0 || ticks.data is null || ticks.data.Count == 0)
        {
            throw new InvalidOperationException($"EastMoney .NET current failed. status={ticks.status}; {ticks.statusInfo}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var names = LoadNames(joined);
        var preCloses = LoadPreCloses(joined);

        var result = new List<EastMoneyQuantDotNetQuote>(ticks.data.Count);
        foreach (var tick in ticks.data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(tick.symbol) || tick.price <= 0)
            {
                continue;
            }

            var code = StockSymbolNormalizer.NormalizeCode(tick.symbol);
            var preClose = preCloses.GetValueOrDefault(tick.symbol);
            var changePercent = preClose > 0
                ? ((decimal)tick.price - preClose) / preClose * 100m
                : 0m;

            result.Add(new EastMoneyQuantDotNetQuote(
                code,
                names.GetValueOrDefault(tick.symbol, code),
                (decimal)tick.price,
                Math.Round(changePercent, 4),
                0m,
                0m,
                (decimal)tick.cumAmount,
                tick.createdAt == default ? DateTimeOffset.Now : new DateTimeOffset(DateTime.SpecifyKind(tick.createdAt, DateTimeKind.Local)),
                (decimal)tick.open,
                (decimal)tick.high,
                (decimal)tick.low,
                (decimal)tick.cumVolume));
        }

        return result;
    }

    private IReadOnlyList<KLineBar> LoadKLineCore(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureToken();

        var gmSymbol = ToGmSymbol(symbol);
        var frequency = ToFrequency(period);
        if (gmSymbol.Length == 0 || frequency.Length == 0)
        {
            return [];
        }

        var takeCount = Math.Clamp(count, 1, 1200);
        var bars = GMApi.HistoryBarsN(
            gmSymbol,
            frequency,
            takeCount,
            string.Empty,
            Adjust.ADJUST_PREV,
            string.Empty,
            true,
            string.Empty);
        if (bars.status != 0 || bars.data is null || bars.data.Count == 0)
        {
            return [];
        }

        return bars.data
            .Select(item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new KLineBar(
                    item.eob == default ? item.bob : item.eob,
                    (decimal)item.open,
                    (decimal)item.high,
                    (decimal)item.low,
                    (decimal)item.close,
                    (decimal)item.volume,
                    (decimal)item.amount);
            })
            .Where(item => item.Close > 0)
            .OrderBy(item => item.TradingTime)
            .TakeLast(takeCount)
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadAshareInstrumentNamesAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("EastMoney Quant is disabled; canonical stock names cannot be generated.");
        }

        await _sdkLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => LoadAshareInstrumentNamesCore(cancellationToken), cancellationToken);
        }
        finally
        {
            _sdkLock.Release();
        }
    }

    private IReadOnlyDictionary<string, string> LoadAshareInstrumentNamesCore(CancellationToken cancellationToken)
    {
        EnsureToken();
        var table = GMApi.GetInstrumentinfos(string.Empty, "SHSE,SZSE", "1", string.Empty, "symbol,sec_name,delisted_date");
        if (table.status != 0 || table.data is null)
        {
            throw new InvalidDataException($"EastMoney instrument name query failed. status={table.status}; {table.statusInfo}");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in table.data.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = Convert.ToString(row["symbol"], CultureInfo.InvariantCulture) ?? string.Empty;
            var name = Convert.ToString(row["sec_name"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            var delisted = Convert.ToString(row["delisted_date"], CultureInfo.InvariantCulture) ?? string.Empty;
            if (!IsSupportedAshareSymbol(symbol))
            {
                continue;
            }

            var code = StockSymbolNormalizer.NormalizeCode(symbol);
            if (code.Length != 6 || !IsCanonicalStockName(name))
            {
                throw new InvalidDataException($"Invalid canonical stock name from EastMoney instrumentinfos: {symbol}={name}");
            }

            result[code] = name;
        }

        if (result.Count < 1000)
        {
            throw new InvalidDataException($"EastMoney instrument name coverage is too low: {result.Count}");
        }

        return result;
    }

    private static bool IsCanonicalStockName(string name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && !name.Contains('\uFFFD')
            && !name.Contains('?')
            && name.Any(ch => ch >= '\u4E00' && ch <= '\u9FFF')
            && !name.All(ch => char.IsDigit(ch) || ch == '.');
    }

    private int LoadAshareUniverseCountCore(CancellationToken cancellationToken)
    {
        return LoadAshareUniverseSymbolsCore(cancellationToken).Count;
    }

    private IReadOnlyList<string> LoadAshareUniverseSymbolsCore(CancellationToken cancellationToken)
    {
        EnsureToken();
        var table = GMApi.GetInstrumentinfos(
            string.Empty,
            "SHSE,SZSE",
            "1",
            string.Empty,
            "symbol,sec_name,delisted_date");
        if (table.status != 0 || table.data is null)
        {
            return [];
        }

        var symbols = new List<string>();
        foreach (DataRow row in table.data.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = Convert.ToString(row["symbol"], CultureInfo.InvariantCulture) ?? string.Empty;
            var name = Convert.ToString(row["sec_name"], CultureInfo.InvariantCulture) ?? string.Empty;
            var delistedDate = Convert.ToString(row["delisted_date"], CultureInfo.InvariantCulture) ?? string.Empty;
            if (IsSupportedAshareSymbol(symbol)
                && !name.Contains("ST", StringComparison.OrdinalIgnoreCase)
                && !IsDelisted(delistedDate))
            {
                var code = StockSymbolNormalizer.NormalizeCode(symbol);
                if (code.Length == 6)
                {
                    symbols.Add(code);
                }
            }
        }

        return symbols
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSupportedAshareSymbol(string symbol)
    {
        var value = symbol.Trim().ToUpperInvariant();
        return value.StartsWith("SHSE.600", StringComparison.Ordinal) ||
               value.StartsWith("SHSE.601", StringComparison.Ordinal) ||
               value.StartsWith("SHSE.603", StringComparison.Ordinal) ||
               value.StartsWith("SHSE.605", StringComparison.Ordinal) ||
               value.StartsWith("SHSE.688", StringComparison.Ordinal) ||
               value.StartsWith("SHSE.689", StringComparison.Ordinal) ||
               value.StartsWith("SZSE.000", StringComparison.Ordinal) ||
               value.StartsWith("SZSE.001", StringComparison.Ordinal) ||
               value.StartsWith("SZSE.002", StringComparison.Ordinal) ||
               value.StartsWith("SZSE.003", StringComparison.Ordinal) ||
               value.StartsWith("SZSE.300", StringComparison.Ordinal) ||
               value.StartsWith("SZSE.301", StringComparison.Ordinal);
    }

    private static bool IsDelisted(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "None")
        {
            return false;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var delisted)
            && delisted.Date <= DateTime.Today;
    }

    private Dictionary<string, string> LoadNames(string gmSymbols)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var table = GMApi.GetInstrumentinfos(gmSymbols, string.Empty, "1", string.Empty, "symbol,sec_name");
            if (table.status != 0 || table.data is null)
            {
                return result;
            }

            foreach (DataRow row in table.data.Rows)
            {
                var symbol = Convert.ToString(row["symbol"], CultureInfo.InvariantCulture) ?? string.Empty;
                var name = Convert.ToString(row["sec_name"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(symbol) && !string.IsNullOrWhiteSpace(name))
                {
                    result[symbol] = name;
                }
            }
        }
        catch
        {
            // Names are enrichment only; price data remains usable without them.
        }

        return result;
    }

    private Dictionary<string, decimal> LoadPreCloses(string gmSymbols)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var table = GMApi.GetInstruments(gmSymbols, string.Empty, "1", "symbol,pre_close");
            if (table.status != 0 || table.data is null)
            {
                return result;
            }

            foreach (DataRow row in table.data.Rows)
            {
                var symbol = Convert.ToString(row["symbol"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(symbol)
                    && decimal.TryParse(
                        Convert.ToString(row["pre_close"], CultureInfo.InvariantCulture),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var preClose))
                {
                    result[symbol] = preClose;
                }
            }
        }
        catch
        {
            // Missing pre-close only affects change percent; do not fail the snapshot.
        }

        return result;
    }

    private void EnsureToken()
    {
        if (_tokenInitialized)
        {
            return;
        }

        var tokenName = string.IsNullOrWhiteSpace(_options.TokenEnvironmentVariable)
            ? "EASTMONEY_QUANT_TOKEN"
            : _options.TokenEnvironmentVariable.Trim();
        var token = !string.IsNullOrWhiteSpace(_options.Token)
            ? _options.Token.Trim()
            : Environment.GetEnvironmentVariable(tokenName)
            ?? Environment.GetEnvironmentVariable(tokenName, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(tokenName, EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"EastMoney .NET token is empty. Configure EastMoneyQuantDotNet.Token or environment variable '{tokenName}'.");
        }

        var status = GMApi.SetToken(token);
        if (status != 0)
        {
            throw new InvalidOperationException($"EastMoney .NET SetToken failed. status={status}");
        }

        _tokenInitialized = true;
    }

    private static string ToGmSymbol(string symbol)
    {
        var code = StockSymbolNormalizer.NormalizeCode(symbol);
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            return string.Empty;
        }

        return code.StartsWith('6') ? "SHSE." + code : "SZSE." + code;
    }

    private static string ToFrequency(string period)
    {
        return SimulatedKLineDataProvider.NormalizePeriod(period) switch
        {
            "minute" => "60s",
            "five-day" => "60s",
            "m1" => "60s",
            "m5" => "300s",
            "m15" => "900s",
            "m30" => "1800s",
            "m60" => "3600s",
            "month" => "1m",
            "day" => "1d",
            _ => string.Empty
        };
    }
}

public sealed record EastMoneyQuantDotNetQuote(
    string Symbol,
    string Name,
    decimal Price,
    decimal ChangePercent,
    decimal VolumeRatio,
    decimal TurnoverRate,
    decimal Amount,
    DateTimeOffset QuoteTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Volume);
