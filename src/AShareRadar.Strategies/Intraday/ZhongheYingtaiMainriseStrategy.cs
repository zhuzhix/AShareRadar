using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class ZhongheYingtaiMainriseStrategy : ISignalStrategy
{
    private const int RequiredBarCount = 80;
    private const decimal MaxDistanceFromTrendLinePercent = 9m;
    private const decimal MaxRecentFiveDayChangePercent = 13m;
    private const decimal MinVolumeRatio = 0.8m;

    public string Code => "zhonghe-yingtai-mainrise";

    public string Name => "中和应泰主升低吸观察";

    public StrategyType Type => StrategyType.LongTermWatch;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.ReviewOnly,
        StrategySignalAction.PullbackWait,
        new StrategyDataRequirement(true, true, false, false, false, RequiredBarCount),
        new Dictionary<string, string>
        {
            ["trend_line"] = "EMA20 proxy",
            ["life_line"] = "EMA30 proxy",
            ["max_distance_from_trend_line_percent"] = MaxDistanceFromTrendLinePercent.ToString("F0")
        },
        "把中和应泰主升低吸体系先近似为趋势线、生命线、充分回踩、量价改善和不过热的观察标签。");

    public Task<IReadOnlyList<StrategySignal>> EvaluateAsync(StrategyContext context, CancellationToken cancellationToken)
    {
        var signals = context.Snapshot.Quotes
            .Where(item => item.Price > 0 && item.VolumeRatio >= MinVolumeRatio)
            .Select(item => BuildSignal(item, context))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Score)
            .Take(5)
            .ToArray();

        return Task.FromResult<IReadOnlyList<StrategySignal>>(signals);
    }

    private StrategySignal? BuildSignal(StockQuote quote, StrategyContext context)
    {
        if (context.DailyBarsBySymbol is null
            || !context.DailyBarsBySymbol.TryGetValue(quote.Symbol, out var bars)
            || bars.Count < RequiredBarCount)
        {
            return null;
        }

        var historyBars = bars.OrderBy(item => item.TradingTime).TakeLast(RequiredBarCount).ToArray();
        var trendLine = Ema(historyBars.Select(item => item.Close).ToArray(), 20);
        var lifeLine = Ema(historyBars.Select(item => item.Close).ToArray(), 30);
        var heartLine = Ema(historyBars.Select(item => item.High).ToArray(), 20);
        var ma60 = historyBars.TakeLast(60).Average(item => item.Close);
        var recentLow = historyBars.TakeLast(15).Min(item => item.Low);
        var recentFiveDayChangePercent = CalculateWindowChange(historyBars, 5);
        var distanceFromTrendLinePercent = trendLine > 0 ? (quote.Price - trendLine) / trendLine * 100m : 0m;
        var pullbackDepthPercent = trendLine > 0 ? (recentLow - trendLine) / trendLine * 100m : 0m;
        var trendStrengthPercent = ma60 > 0 ? (trendLine - ma60) / ma60 * 100m : 0m;
        var heartLineDistancePercent = heartLine > 0 ? (quote.Price - heartLine) / heartLine * 100m : 0m;

        if (trendStrengthPercent < 0m
            || distanceFromTrendLinePercent < -3m
            || distanceFromTrendLinePercent > MaxDistanceFromTrendLinePercent
            || pullbackDepthPercent > 2.5m
            || recentFiveDayChangePercent > MaxRecentFiveDayChangePercent
            || heartLineDistancePercent > 8m)
        {
            return null;
        }

        var confidence = distanceFromTrendLinePercent <= 5m && quote.VolumeRatio >= 1.0m
            ? StrategySignalConfidence.Medium
            : StrategySignalConfidence.Low;
        var score = 57m
            + Math.Min(trendStrengthPercent * 1.6m, 10m)
            + Math.Max(9m - Math.Abs(distanceFromTrendLinePercent), 0m)
            + Math.Min(quote.VolumeRatio * 3m, 8m)
            + Math.Max(6m - Math.Max(heartLineDistancePercent, 0m), 0m);

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            score,
            quote.Price,
            $"趋势线 {trendLine:F2}、生命线 {lifeLine:F2}，价格距离趋势线 {distanceFromTrendLinePercent:F1}%，近 15 日低点回踩 {pullbackDepthPercent:F1}%，量比 {quote.VolumeRatio:F2}。",
            "这是公式体系的近似观察版，仍需逐条拆解 tn6 指标并人工确认图形。",
            StrategySignalAction.PullbackWait,
            confidence,
            StrategyStage.ReviewOnly,
            new Dictionary<string, decimal>
            {
                ["trend_line"] = trendLine,
                ["life_line"] = lifeLine,
                ["heart_line"] = heartLine,
                ["trend_strength_percent"] = trendStrengthPercent,
                ["distance_from_trend_line_percent"] = distanceFromTrendLinePercent,
                ["pullback_depth_percent"] = pullbackDepthPercent,
                ["heart_line_distance_percent"] = heartLineDistancePercent,
                ["recent_5d_change_percent"] = recentFiveDayChangePercent,
                ["volume_ratio"] = quote.VolumeRatio
            },
            ["主升低吸", "趋势线", "生命线", "观察版"],
            [
                $"趋势线强于 MA60 {trendStrengthPercent:F1}%",
                $"距离趋势线 {distanceFromTrendLinePercent:F1}% <= {MaxDistanceFromTrendLinePercent:F0}%",
                $"近 15 日出现趋势线附近回踩 {pullbackDepthPercent:F1}%",
                $"价格未明显脱离心线 {heartLineDistancePercent:F1}%"
            ],
            ["tn6 公式尚未完整结构化，当前只作为低吸观察标签"],
            Math.Round(lifeLine * 0.97m, 2),
            Math.Round(quote.Price * 1.08m, 2));
    }

    private static decimal Ema(IReadOnlyList<decimal> values, int period)
    {
        if (values.Count == 0)
        {
            return 0m;
        }

        var multiplier = 2m / (period + 1);
        var ema = values[0];
        foreach (var value in values.Skip(1))
        {
            ema = (value - ema) * multiplier + ema;
        }

        return ema;
    }

    private static decimal CalculateWindowChange(IReadOnlyList<KLineBar> bars, int count)
    {
        var start = bars.TakeLast(count + 1).First().Close;
        var end = bars[^1].Close;
        return start > 0 ? (end - start) / start * 100m : 0m;
    }
}
