using System.Globalization;
using System.Text.Json;
using AShareRadar.Application.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class TencentKLineDataProvider : IKLineDataProvider
{
    private readonly HttpClient _httpClient;

    public TencentKLineDataProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string ProviderName => "TencentKLine";

    public async Task<IReadOnlyList<KLineBar>> LoadKLineAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var normalizedPeriod = SimulatedKLineDataProvider.NormalizePeriod(period);
        if (!IsIntradayPeriod(normalizedPeriod))
        {
            return [];
        }

        var providerSymbol = StockSymbolNormalizer.ToPrefixedCode(symbol);
        if (providerSymbol.Length != 8)
        {
            return [];
        }

        var takeCount = Math.Clamp(count, 1, 720);
        if (normalizedPeriod is "m30" or "m60")
        {
            var aggregatedBars = await LoadAggregatedBarsAsync(providerSymbol, normalizedPeriod, takeCount, cancellationToken);
            if (aggregatedBars.Count > 0)
            {
                return aggregatedBars;
            }
        }

        var directBars = await LoadProviderBarsAsync(providerSymbol, normalizedPeriod, takeCount, cancellationToken);
        return directBars;
    }

    private async Task<IReadOnlyList<KLineBar>> LoadProviderBarsAsync(
        string providerSymbol,
        string normalizedPeriod,
        int takeCount,
        CancellationToken cancellationToken)
    {
        foreach (var url in BuildUrls(providerSymbol, normalizedPeriod, takeCount))
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var bars = ParseBars(document.RootElement, providerSymbol, normalizedPeriod)
                    .OrderBy(item => item.TradingTime)
                    .TakeLast(takeCount)
                    .ToArray();
                if (bars.Length > 0)
                {
                    return bars;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
            {
                LogFallback(ex);
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<KLineBar>> LoadAggregatedBarsAsync(
        string providerSymbol,
        string normalizedPeriod,
        int takeCount,
        CancellationToken cancellationToken)
    {
        var minutesPerBar = normalizedPeriod == "m30" ? 30 : 60;
        var sourceCount = Math.Clamp(takeCount * minutesPerBar + 120, 1, 720);
        var minuteBars = await LoadProviderBarsAsync(providerSymbol, "m1", sourceCount, cancellationToken);
        if (minuteBars.Count == 0)
        {
            return [];
        }

        return AggregateMinuteBars(minuteBars, minutesPerBar)
            .OrderBy(item => item.TradingTime)
            .TakeLast(takeCount)
            .ToArray();
    }

    private static IEnumerable<KLineBar> AggregateMinuteBars(IReadOnlyList<KLineBar> minuteBars, int minutesPerBar)
    {
        return minuteBars
            .Where(item => IsTradingMinute(item.TradingTime))
            .OrderBy(item => item.TradingTime)
            .GroupBy(item => GetIntradayBucketEnd(item.TradingTime, minutesPerBar))
            .Select(group =>
            {
                var items = group.OrderBy(item => item.TradingTime).ToArray();
                return new KLineBar(
                    group.Key,
                    items[0].Open,
                    items.Max(item => item.High),
                    items.Min(item => item.Low),
                    items[^1].Close,
                    items.Sum(item => item.Volume),
                    items.Sum(item => item.Amount));
            });
    }

    private static DateTime GetIntradayBucketEnd(DateTime tradingTime, int minutesPerBar)
    {
        var date = tradingTime.Date;
        var time = tradingTime.TimeOfDay;
        var sessionStart = time < TimeSpan.FromHours(12)
            ? date.AddHours(9.5)
            : date.AddHours(13);
        var sessionEnd = time < TimeSpan.FromHours(12)
            ? date.AddHours(11.5)
            : date.AddHours(15);

        var elapsedMinutes = Math.Max(0, (tradingTime - sessionStart).TotalMinutes);
        var bucketIndex = (int)Math.Ceiling(Math.Max(1d, elapsedMinutes) / minutesPerBar);
        var bucketEnd = sessionStart.AddMinutes(bucketIndex * minutesPerBar);
        return bucketEnd > sessionEnd ? sessionEnd : bucketEnd;
    }

    private static IEnumerable<string> BuildUrls(string providerSymbol, string period, int count)
    {
        var tencentPeriod = period switch
        {
            "minute" => "m1",
            "five-day" => "m1",
            "m1" => "m1",
            "m5" => "m5",
            "m15" => "m15",
            "m30" => "m30",
            "m60" => "m60",
            _ => "m5"
        };

        var path = $"web.ifzq.gtimg.cn/appstock/app/kline/mkline?param={providerSymbol},{tencentPeriod},,{count}";
        yield return $"https://{path}";
        yield return $"http://{path}";
    }

    private static IEnumerable<KLineBar> ParseBars(JsonElement root, string providerSymbol, string period)
    {
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty(providerSymbol, out var symbolData))
        {
            yield break;
        }

        foreach (var propertyName in CandidatePropertyNames(period))
        {
            if (!symbolData.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var row in values.EnumerateArray())
            {
                var bar = TryParseBar(row);
                if (bar is not null)
                {
                    if (IsTradingMinute(bar.TradingTime))
                    {
                        yield return bar;
                    }
                }
            }

            yield break;
        }
    }

    private static string[] CandidatePropertyNames(string period)
    {
        return period switch
        {
            "minute" => ["m1", "data"],
            "five-day" => ["m1", "data"],
            "m1" => ["m1", "data"],
            "m5" => ["m5", "data"],
            "m15" => ["m15", "data"],
            "m30" => ["m30", "data"],
            "m60" => ["m60", "data"],
            _ => ["data"]
        };
    }

    private static KLineBar? TryParseBar(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6)
        {
            return null;
        }

        var timeText = ReadString(row[0]);
        if (!TryParseTime(timeText, out var tradingTime))
        {
            return null;
        }

        var open = ParseDecimal(row[1]);
        var close = ParseDecimal(row[2]);
        var high = ParseDecimal(row[3]);
        var low = ParseDecimal(row[4]);
        var volume = ParseDecimal(row[5]);
        if (open <= 0 || close <= 0)
        {
            return null;
        }

        high = high > 0 ? high : Math.Max(open, close);
        low = low > 0 ? low : Math.Min(open, close);

        return new KLineBar(tradingTime, open, high, low, close, volume);
    }

    private static bool TryParseTime(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return DateTime.TryParseExact(
                   value,
                   ["yyyyMMddHHmm", "yyyy-MM-dd HH:mm", "yyyyMMdd"],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out result) ||
               DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static string? ReadString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static decimal ParseDecimal(JsonElement value)
    {
        var text = ReadString(value);
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }

    private static bool IsIntradayPeriod(string period)
    {
        return period is "minute" or "five-day" or "m1" or "m5" or "m15" or "m30" or "m60";
    }

    private static bool IsTradingMinute(DateTime value)
    {
        if (value.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var time = value.TimeOfDay;
        return time >= TimeSpan.FromHours(9.5) && time <= TimeSpan.FromHours(11.5) ||
               time >= TimeSpan.FromHours(13) && time <= TimeSpan.FromHours(15);
    }

    private static void LogFallback(Exception exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "tencent-kline-fallback.log"),
                $"{DateTimeOffset.Now:O} Tencent K-line provider failed. {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
        }
        catch
        {
            // K-line fallback diagnostics must not break chart loading.
        }
    }
}
