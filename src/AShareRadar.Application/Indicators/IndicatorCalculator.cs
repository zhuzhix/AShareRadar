using AShareRadar.Application.MarketData;

namespace AShareRadar.Application.Indicators;

public sealed class IndicatorCalculator : IIndicatorCalculator
{
    public IndicatorSeries Calculate(IReadOnlyList<KLineBar> bars, string indicatorType)
    {
        var type = Normalize(indicatorType);
        return type switch
        {
            IndicatorType.Kdj => new IndicatorSeries(type, CalculateKdj(bars)),
            IndicatorType.Rsi => new IndicatorSeries(type, CalculateRsi(bars)),
            _ => new IndicatorSeries(type, CalculateMacd(bars))
        };
    }

    private static IndicatorType Normalize(string indicatorType)
    {
        return indicatorType.Trim().ToUpperInvariant() switch
        {
            "KDJ" => IndicatorType.Kdj,
            "RSI" => IndicatorType.Rsi,
            _ => IndicatorType.Macd
        };
    }

    private static IReadOnlyList<IndicatorPoint> CalculateMacd(IReadOnlyList<KLineBar> bars)
    {
        var points = new List<IndicatorPoint>(bars.Count);
        decimal? ema12 = null;
        decimal? ema26 = null;
        decimal dea = 0m;

        foreach (var bar in bars)
        {
            ema12 = Ema(ema12, bar.Close, 12);
            ema26 = Ema(ema26, bar.Close, 26);
            var dif = ema12.Value - ema26.Value;
            dea = Ema(dea, dif, 9);
            var macd = 2m * (dif - dea);

            points.Add(new IndicatorPoint(
                bar.TradingTime,
                Math.Round(dif, 4),
                Math.Round(dea, 4),
                null,
                Math.Round(macd, 4)));
        }

        return points;
    }

    private static IReadOnlyList<IndicatorPoint> CalculateKdj(IReadOnlyList<KLineBar> bars)
    {
        var points = new List<IndicatorPoint>(bars.Count);
        var k = 50m;
        var d = 50m;

        for (var i = 0; i < bars.Count; i++)
        {
            var start = Math.Max(0, i - 8);
            var window = bars.Skip(start).Take(i - start + 1).ToArray();
            var high = window.Max(item => item.High);
            var low = window.Min(item => item.Low);
            var rsv = high == low ? 50m : (bars[i].Close - low) / (high - low) * 100m;

            k = k * 2m / 3m + rsv / 3m;
            d = d * 2m / 3m + k / 3m;
            var j = 3m * k - 2m * d;

            points.Add(new IndicatorPoint(
                bars[i].TradingTime,
                Math.Round(k, 4),
                Math.Round(d, 4),
                Math.Round(j, 4),
                null));
        }

        return points;
    }

    private static IReadOnlyList<IndicatorPoint> CalculateRsi(IReadOnlyList<KLineBar> bars)
    {
        var points = new List<IndicatorPoint>(bars.Count);

        for (var i = 0; i < bars.Count; i++)
        {
            points.Add(new IndicatorPoint(
                bars[i].TradingTime,
                CalculateRsiAt(bars, i, 6),
                CalculateRsiAt(bars, i, 12),
                CalculateRsiAt(bars, i, 24),
                null));
        }

        return points;
    }

    private static decimal? CalculateRsiAt(IReadOnlyList<KLineBar> bars, int index, int window)
    {
        if (index <= 0)
        {
            return null;
        }

        var start = Math.Max(1, index - window + 1);
        var gains = 0m;
        var losses = 0m;
        for (var i = start; i <= index; i++)
        {
            var diff = bars[i].Close - bars[i - 1].Close;
            if (diff >= 0)
            {
                gains += diff;
            }
            else
            {
                losses += Math.Abs(diff);
            }
        }

        if (gains == 0m && losses == 0m)
        {
            return 50m;
        }

        var rsi = losses == 0m ? 100m : 100m - 100m / (1m + gains / losses);
        return Math.Round(rsi, 4);
    }

    private static decimal Ema(decimal? previous, decimal current, int period)
    {
        if (previous is null)
        {
            return current;
        }

        var multiplier = 2m / (period + 1m);
        return current * multiplier + previous.Value * (1m - multiplier);
    }

    private static decimal Ema(decimal previous, decimal current, int period)
    {
        var multiplier = 2m / (period + 1m);
        return current * multiplier + previous * (1m - multiplier);
    }
}
