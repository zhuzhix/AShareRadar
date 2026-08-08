using AShareRadar.Application.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class SimulatedKLineDataProvider : IKLineDataProvider
{
    public string ProviderName => "Simulation";

    public Task<IReadOnlyList<KLineBar>> LoadKLineAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        var normalizedPeriod = NormalizePeriod(period);
        var takeCount = normalizedPeriod == "five-day"
            ? Math.Clamp(count, 30, 1200)
            : Math.Clamp(count, 30, 360);
        var normalizedSymbol = StockSymbolNormalizer.NormalizeCode(symbol);
        IReadOnlyList<KLineBar> bars = Generate(normalizedSymbol, normalizedPeriod, takeCount);
        return Task.FromResult(bars);
    }

    public static string NormalizePeriod(string? period)
    {
        return period?.Trim().ToLowerInvariant() switch
        {
            "minute" or "分时" => "minute",
            "five-day" or "5d" or "5日" => "five-day",
            "m1" or "1m" or "1分钟" or "1分" => "m1",
            "m5" or "5m" or "5分钟" or "5分" => "m5",
            "m15" or "15m" or "15分钟" or "15分" => "m15",
            "m30" or "30m" or "30分钟" or "30分" => "m30",
            "m60" or "60m" or "60分钟" or "60分" => "m60",
            "week" or "weekly" or "周线" => "week",
            "month" or "monthly" or "月线" => "month",
            _ => "day"
        };
    }

    private static KLineBar[] Generate(string symbol, string period, int count)
    {
        var seed = Math.Abs(symbol.Aggregate(17, (current, ch) => current * 31 + ch));
        var random = new Random(seed + period.GetHashCode(StringComparison.Ordinal));
        var close = 24m + seed % 24;
        var bars = new List<KLineBar>(count);
        var step = period switch
        {
            "minute" => TimeSpan.FromMinutes(5),
            "five-day" => TimeSpan.FromMinutes(1),
            "m1" => TimeSpan.FromMinutes(1),
            "m5" => TimeSpan.FromMinutes(5),
            "m15" => TimeSpan.FromMinutes(15),
            "m30" => TimeSpan.FromMinutes(30),
            "m60" => TimeSpan.FromMinutes(60),
            "week" => TimeSpan.FromDays(7),
            "month" => TimeSpan.FromDays(30),
            _ => TimeSpan.FromDays(1)
        };

        var tradingTimes = IsMinuteLike(period)
            ? BuildTradingTimes(period, step, count)
            : BuildCalendarTimes(period, step, count);
        for (var i = 0; i < count; i++)
        {
            var wave = (decimal)Math.Sin((i + seed % 11) / 8.0) * 0.18m;
            var trend = period switch
            {
                "minute" => 0.002m,
                "five-day" => 0.001m,
                "m1" or "m5" or "m15" or "m30" or "m60" => 0.002m,
                "week" => -0.015m,
                "month" => 0.03m,
                _ => -0.005m
            };
            var volatility = period switch
            {
                "minute" => 0.35m,
                "five-day" => 0.32m,
                "m1" => 0.12m,
                "m5" => 0.35m,
                "m15" => 0.52m,
                "m30" => 0.65m,
                "m60" => 0.8m,
                "week" => 1.65m,
                "month" => 2.4m,
                _ => 0.85m
            };

            var open = close + (decimal)(random.NextDouble() - 0.5) * volatility;
            close = open + ((decimal)random.NextDouble() - 0.48m) * volatility * 1.6m + wave + trend;
            close = Math.Max(2m, close);
            var high = Math.Max(open, close) + (decimal)random.NextDouble() * volatility * 0.8m;
            var low = Math.Max(1m, Math.Min(open, close) - (decimal)random.NextDouble() * volatility * 0.8m);
            var volume = random.Next(80_000, 980_000) * (IsMinuteLike(period) ? 0.18m : 1m);

            bars.Add(new KLineBar(
                tradingTimes[i],
                Math.Round(open, 2),
                Math.Round(high, 2),
                Math.Round(low, 2),
                Math.Round(close, 2),
                Math.Round(volume, 0)));
        }

        return bars.ToArray();
    }

    private static DateTime[] BuildCalendarTimes(string period, TimeSpan step, int count)
    {
        var end = period switch
        {
            "week" => DateTime.Today,
            "month" => DateTime.Today,
            _ => DateTime.Today
        };
        var start = end - TimeSpan.FromTicks(step.Ticks * (count - 1));
        return Enumerable.Range(0, count)
            .Select(index => start.AddTicks(step.Ticks * index))
            .ToArray();
    }

    private static DateTime[] BuildTradingTimes(string period, TimeSpan step, int count)
    {
        var times = new List<DateTime>(count);
        var cursor = AlignToTradingTime(DateTime.Now, step);
        while (times.Count < count)
        {
            if (IsTradingMinute(cursor))
            {
                times.Add(cursor);
            }

            cursor = cursor.AddMinutes(-Math.Max(1, (int)step.TotalMinutes));
        }

        times.Reverse();
        return times.ToArray();
    }

    private static bool IsMinuteLike(string period)
    {
        return period is "minute" or "five-day" or "m1" or "m5" or "m15" or "m30" or "m60";
    }

    private static DateTime AlignToStep(DateTime value, TimeSpan step)
    {
        if (step <= TimeSpan.Zero)
        {
            return value;
        }

        var ticks = value.Ticks - value.Ticks % step.Ticks;
        return new DateTime(ticks, value.Kind);
    }

    private static DateTime AlignToTradingTime(DateTime value, TimeSpan step)
    {
        var aligned = AlignToStep(value, step);
        var date = aligned.Date;
        var time = aligned.TimeOfDay;
        if (time < TimeSpan.FromHours(9.5))
        {
            return PreviousTradingDay(date).AddHours(15);
        }

        if (time > TimeSpan.FromHours(15))
        {
            return date.AddHours(15);
        }

        if (time > TimeSpan.FromHours(11.5) && time < TimeSpan.FromHours(13))
        {
            return date.AddHours(11.5);
        }

        return IsTradingMinute(aligned) ? aligned : PreviousTradingMinute(aligned, step);
    }

    private static DateTime PreviousTradingMinute(DateTime value, TimeSpan step)
    {
        var cursor = value;
        while (!IsTradingMinute(cursor))
        {
            cursor = cursor.AddMinutes(-Math.Max(1, (int)step.TotalMinutes));
        }

        return cursor;
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

    private static DateTime PreviousTradingDay(DateTime date)
    {
        var cursor = date.AddDays(-1);
        while (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            cursor = cursor.AddDays(-1);
        }

        return cursor;
    }
}
