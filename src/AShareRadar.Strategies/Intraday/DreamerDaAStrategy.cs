using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class DreamerDaAStrategy : ISignalStrategy
{
    private const int RequiredBarCount = 80;
    private const decimal MaxDistanceFromSupportPercent = 12m;
    private const decimal MaxRecentFiveDayChangePercent = 14m;
    private const decimal MaxDrawdown20Percent = -22m;
    private const decimal MinVolumeRatio = 0.8m;

    public string Code => "dreamer-da-a";

    public string Name => "大A梦想家长期观察";

    public StrategyType Type => StrategyType.LongTermWatch;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.ReviewOnly,
        StrategySignalAction.Watch,
        new StrategyDataRequirement(true, true, false, false, false, RequiredBarCount),
        new Dictionary<string, string>
        {
            ["max_distance_from_support_percent"] = MaxDistanceFromSupportPercent.ToString("F0"),
            ["max_recent_5d_change_percent"] = MaxRecentFiveDayChangePercent.ToString("F0")
        },
        "长期观察策略，关注趋势结构、支撑距离、不过热和量价配合，只进入观察池，不作为强买入信号。");

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
        var ma20 = AverageClose(historyBars, 20);
        var ma60 = AverageClose(historyBars, 60);
        var supportLine = ma20;
        var trendStrengthPercent = ma60 > 0 ? (ma20 - ma60) / ma60 * 100m : 0m;
        var distanceFromSupportPercent = supportLine > 0 ? (quote.Price - supportLine) / supportLine * 100m : 0m;
        var recentFiveDayChangePercent = CalculateWindowChange(historyBars, 5);
        var drawdown20Percent = CalculateDrawdown(historyBars, 20, quote.Price);
        var high20 = historyBars.TakeLast(20).Max(item => item.High);
        var breakoutRoomPercent = quote.Price > 0 ? (high20 - quote.Price) / quote.Price * 100m : 0m;

        if (trendStrengthPercent < -3m
            || distanceFromSupportPercent < -4m
            || distanceFromSupportPercent > MaxDistanceFromSupportPercent
            || recentFiveDayChangePercent > MaxRecentFiveDayChangePercent
            || drawdown20Percent < MaxDrawdown20Percent)
        {
            return null;
        }

        var confidence = trendStrengthPercent >= 2m && distanceFromSupportPercent <= 7m && quote.VolumeRatio >= 1.0m
            ? StrategySignalConfidence.Medium
            : StrategySignalConfidence.Low;
        var score = 58m
            + Math.Min(Math.Max(trendStrengthPercent, 0m) * 1.5m, 10m)
            + Math.Max(10m - Math.Abs(distanceFromSupportPercent), 0m)
            + Math.Min(quote.VolumeRatio * 3m, 8m)
            + Math.Max(8m + drawdown20Percent / 3m, 0m);

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            score,
            quote.Price,
            $"长期观察形态：MA20 较 MA60 {trendStrengthPercent:F1}%，距离支撑 MA20 {distanceFromSupportPercent:F1}%，近 5 日涨幅 {recentFiveDayChangePercent:F1}%，20 日回撤 {drawdown20Percent:F1}%。",
            "长期观察策略仍需人工确认图形、分时承接和题材持续性。",
            StrategySignalAction.Watch,
            confidence,
            StrategyStage.ReviewOnly,
            new Dictionary<string, decimal>
            {
                ["ma20"] = ma20,
                ["ma60"] = ma60,
                ["trend_strength_percent"] = trendStrengthPercent,
                ["distance_from_support_percent"] = distanceFromSupportPercent,
                ["recent_5d_change_percent"] = recentFiveDayChangePercent,
                ["drawdown_20d_percent"] = drawdown20Percent,
                ["breakout_room_percent"] = breakoutRoomPercent,
                ["volume_ratio"] = quote.VolumeRatio
            },
            ["长期观察", "形态位置", "人工确认"],
            [
                $"距离支撑 {distanceFromSupportPercent:F1}% <= {MaxDistanceFromSupportPercent:F0}%",
                $"近 5 日涨幅 {recentFiveDayChangePercent:F1}% 未过热",
                $"20 日回撤 {drawdown20Percent:F1}% 未明显破坏结构"
            ],
            ["缺少完整分时承接和盘口验证，只能进入观察"],
            Math.Round(ma20 * 0.96m, 2),
            Math.Round(quote.Price * 1.08m, 2));
    }

    private static decimal AverageClose(IReadOnlyList<KLineBar> bars, int count) =>
        bars.Count < count ? 0m : bars.TakeLast(count).Average(item => item.Close);

    private static decimal CalculateWindowChange(IReadOnlyList<KLineBar> bars, int count)
    {
        var start = bars.TakeLast(count + 1).First().Close;
        var end = bars[^1].Close;
        return start > 0 ? (end - start) / start * 100m : 0m;
    }

    private static decimal CalculateDrawdown(IReadOnlyList<KLineBar> bars, int count, decimal currentPrice)
    {
        var high = bars.TakeLast(count).Max(item => item.High);
        return high > 0 ? (currentPrice - high) / high * 100m : 0m;
    }
}
