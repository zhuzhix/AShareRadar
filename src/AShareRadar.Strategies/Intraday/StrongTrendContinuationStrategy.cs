using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class StrongTrendContinuationStrategy : ISignalStrategy
{
    private const int RequiredBarCount = 60;
    private const int TrendAgeLookback = 20;
    private const int RecentLookback = 5;
    private const decimal MinChangePercent = 0.6m;
    private const decimal MaxChangePercent = 4.8m;
    private const decimal MinVolumeRatio = 1.2m;
    private const decimal MinTrendStrengthPercent = 5m;
    private const decimal MaxDistanceFromMa20Percent = 8m;
    private const decimal MaxRecentFiveDayChangePercent = 10m;
    private const decimal MaxUpperShadowPercent = 25m;
    private const decimal MinClosePositionPercent = 70m;

    public string Code => "strong-trend-continuation";

    public string Name => "强势趋势延续";

    public StrategyType Type => StrategyType.IntradayOpportunity;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.PatternValidation,
        StrategySignalAction.Candidate,
        new StrategyDataRequirement(
            RequiresRealtimeQuote: true,
            RequiresDailyKLine: true,
            RequiresMinuteKLine: false,
            RequiresSectorData: false,
            RequiresCapitalFlow: false,
            MinDailyBarCount: RequiredBarCount),
        new Dictionary<string, string>
        {
            ["ma_stack"] = "5/10/20/60",
            ["min_trend_strength_percent"] = MinTrendStrengthPercent.ToString("F0"),
            ["max_distance_from_ma20_percent"] = MaxDistanceFromMa20Percent.ToString("F0"),
            ["max_recent_5d_change_percent"] = MaxRecentFiveDayChangePercent.ToString("F0"),
            ["min_volume_ratio"] = MinVolumeRatio.ToString("F1")
        },
        "寻找 MA5/MA10/MA20/MA60 多头排列、趋势未明显过热、当日放量且收盘位置健康的趋势延续候选。");

    public Task<IReadOnlyList<StrategySignal>> EvaluateAsync(
        StrategyContext context,
        CancellationToken cancellationToken)
    {
        var signals = context.Snapshot.Quotes
            .Where(item => item.Price > 0
                && item.ChangePercent >= MinChangePercent
                && item.ChangePercent <= MaxChangePercent
                && item.VolumeRatio >= MinVolumeRatio)
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

        var orderedBars = bars.OrderBy(item => item.TradingTime).ToArray();
        var currentBar = TryGetCurrentBar(orderedBars, context.TradingDate);
        var historyBars = orderedBars
            .Where(item => DateOnly.FromDateTime(item.TradingTime) < context.TradingDate)
            .TakeLast(RequiredBarCount)
            .ToArray();
        if (historyBars.Length < RequiredBarCount)
        {
            historyBars = orderedBars.TakeLast(RequiredBarCount).ToArray();
        }

        var ma5 = AverageClose(historyBars, 5);
        var ma10 = AverageClose(historyBars, 10);
        var ma20 = AverageClose(historyBars, 20);
        var ma60 = AverageClose(historyBars, 60);
        if (ma5 <= 0 || ma10 <= 0 || ma20 <= 0 || ma60 <= 0)
        {
            return null;
        }

        var previousMa20 = AverageClose(historyBars.Take(historyBars.Length - 1).ToArray(), 20);
        var trendStrengthPercent = (ma20 - ma60) / ma60 * 100m;
        var ma20SlopePercent = previousMa20 > 0 ? (ma20 - previousMa20) / previousMa20 * 100m : 0m;
        var priceAboveMa20Percent = (quote.Price - ma20) / ma20 * 100m;
        var recentFiveDayChangePercent = CalculateWindowChange(historyBars, RecentLookback);
        var recentHighBreakPercent = CalculateRecentHighBreakPercent(historyBars, quote.Price);
        var trendAgeDays = CountTrendAge(historyBars);
        var closePositionPercent = CalculateClosePositionPercent(currentBar, quote.Price);
        var upperShadowPercent = CalculateUpperShadowPercent(currentBar, quote.Price);
        var latestClose = historyBars[^1].Close;
        var aboveLatestClosePercent = latestClose > 0 ? (quote.Price - latestClose) / latestClose * 100m : 0m;

        var failedConditions = BuildFailedConditions(
            ma5,
            ma10,
            ma20,
            ma60,
            trendStrengthPercent,
            ma20SlopePercent,
            priceAboveMa20Percent,
            recentFiveDayChangePercent,
            quote.VolumeRatio,
            closePositionPercent,
            upperShadowPercent,
            quote.Price,
            latestClose);
        if (failedConditions.Count > 0)
        {
            return null;
        }

        var confidence = trendStrengthPercent >= 8m
            && ma20SlopePercent > 0m
            && priceAboveMa20Percent <= 5m
            && quote.VolumeRatio >= 1.5m
            && upperShadowPercent <= 15m
            ? StrategySignalConfidence.High
            : StrategySignalConfidence.Medium;
        var score = 72m
            + Math.Min(trendStrengthPercent * 1.2m, 14m)
            + Math.Min(quote.VolumeRatio * 4m, 12m)
            + Math.Max(8m - priceAboveMa20Percent, 0m)
            + Math.Max(8m - Math.Max(recentFiveDayChangePercent - 4m, 0m), 0m)
            + Math.Min(trendAgeDays, 10) * 0.4m
            + Math.Min(Math.Max(recentHighBreakPercent, 0m), 4m);

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            Score: score,
            Price: quote.Price,
            Reason: $"MA5 {ma5:F2}、MA10 {ma10:F2}、MA20 {ma20:F2}、MA60 {ma60:F2} 多头排列，MA20 高于 MA60 {trendStrengthPercent:F1}%，距离 MA20 {priceAboveMa20Percent:F1}%，量比 {quote.VolumeRatio:F2}。",
            Risk: BuildRisk(priceAboveMa20Percent, recentFiveDayChangePercent, upperShadowPercent, quote.ChangePercent),
            Action: StrategySignalAction.Candidate,
            Confidence: confidence,
            Stage: StrategyStage.PatternValidation,
            Metrics: new Dictionary<string, decimal>
            {
                ["ma5"] = ma5,
                ["ma10"] = ma10,
                ["ma20"] = ma20,
                ["ma60"] = ma60,
                ["trend_strength_percent"] = trendStrengthPercent,
                ["ma20_slope_percent"] = ma20SlopePercent,
                ["price_above_ma20_percent"] = priceAboveMa20Percent,
                ["recent_5d_change_percent"] = recentFiveDayChangePercent,
                ["recent_high_break_percent"] = recentHighBreakPercent,
                ["trend_age_days"] = trendAgeDays,
                ["above_latest_close_percent"] = aboveLatestClosePercent,
                ["close_position_percent"] = closePositionPercent,
                ["upper_shadow_percent"] = upperShadowPercent,
                ["change_percent"] = quote.ChangePercent,
                ["volume_ratio"] = quote.VolumeRatio
            },
            Tags: ["强趋势", "多头排列", "趋势延续", confidence == StrategySignalConfidence.High ? "高质量趋势" : "延续候选"],
            PassedConditions:
            [
                $"MA5 {ma5:F2} > MA10 {ma10:F2} > MA20 {ma20:F2} > MA60 {ma60:F2}",
                $"MA20 趋势强度 {trendStrengthPercent:F1}% >= {MinTrendStrengthPercent:F0}%，斜率 {ma20SlopePercent:F2}%",
                $"距离 MA20 {priceAboveMa20Percent:F1}% <= {MaxDistanceFromMa20Percent:F0}%",
                $"近 {RecentLookback} 日涨幅 {recentFiveDayChangePercent:F1}% <= {MaxRecentFiveDayChangePercent:F0}%",
                $"量比 {quote.VolumeRatio:F2} >= {MinVolumeRatio:F1}",
                $"收盘/当前价位置 {closePositionPercent:F0}% >= {MinClosePositionPercent:F0}%"
            ],
            FailedConditions: [],
            StopLossPrice: Math.Round(ma20 * 0.97m, 2),
            TakeProfitPrice: Math.Round(quote.Price * 1.06m, 2));
    }

    private static List<string> BuildFailedConditions(
        decimal ma5,
        decimal ma10,
        decimal ma20,
        decimal ma60,
        decimal trendStrengthPercent,
        decimal ma20SlopePercent,
        decimal priceAboveMa20Percent,
        decimal recentFiveDayChangePercent,
        decimal volumeRatio,
        decimal closePositionPercent,
        decimal upperShadowPercent,
        decimal currentPrice,
        decimal latestClose)
    {
        var failed = new List<string>();
        if (!(ma5 > ma10 && ma10 > ma20 && ma20 > ma60))
        {
            failed.Add("均线未形成 MA5 > MA10 > MA20 > MA60 多头排列");
        }

        if (trendStrengthPercent < MinTrendStrengthPercent || ma20SlopePercent <= 0m)
        {
            failed.Add($"趋势强度不足，MA20 高于 MA60 {trendStrengthPercent:F1}%，斜率 {ma20SlopePercent:F2}%");
        }

        if (currentPrice <= latestClose)
        {
            failed.Add("当前价未强于上一日收盘");
        }

        if (priceAboveMa20Percent < 0m || priceAboveMa20Percent > MaxDistanceFromMa20Percent)
        {
            failed.Add($"距离 MA20 {priceAboveMa20Percent:F1}% 不在合理区间");
        }

        if (recentFiveDayChangePercent > MaxRecentFiveDayChangePercent)
        {
            failed.Add($"近 {RecentLookback} 日涨幅 {recentFiveDayChangePercent:F1}% 过热");
        }

        if (volumeRatio < MinVolumeRatio)
        {
            failed.Add($"量比 {volumeRatio:F2} < {MinVolumeRatio:F1}");
        }

        if (closePositionPercent < MinClosePositionPercent)
        {
            failed.Add($"收盘/当前价位置 {closePositionPercent:F0}% < {MinClosePositionPercent:F0}%");
        }

        if (upperShadowPercent > MaxUpperShadowPercent)
        {
            failed.Add($"上影线 {upperShadowPercent:F0}% > {MaxUpperShadowPercent:F0}%");
        }

        return failed;
    }

    private static KLineBar? TryGetCurrentBar(IReadOnlyList<KLineBar> bars, DateOnly tradingDate)
    {
        return bars.LastOrDefault(item => DateOnly.FromDateTime(item.TradingTime) == tradingDate);
    }

    private static decimal AverageClose(IReadOnlyList<KLineBar> bars, int count)
    {
        return bars.Count < count ? 0m : bars.TakeLast(count).Average(item => item.Close);
    }

    private static decimal CalculateWindowChange(IReadOnlyList<KLineBar> bars, int count)
    {
        if (bars.Count < count + 1)
        {
            return 0m;
        }

        var start = bars.TakeLast(count + 1).First().Close;
        var end = bars[^1].Close;
        return start > 0 ? (end - start) / start * 100m : 0m;
    }

    private static decimal CalculateRecentHighBreakPercent(IReadOnlyList<KLineBar> bars, decimal currentPrice)
    {
        var recentHigh = bars.TakeLast(TrendAgeLookback).Max(item => item.High);
        return recentHigh > 0 ? (currentPrice - recentHigh) / recentHigh * 100m : 0m;
    }

    private static int CountTrendAge(IReadOnlyList<KLineBar> bars)
    {
        var age = 0;
        foreach (var windowEnd in Enumerable.Range(20, Math.Max(0, bars.Count - 19)).Reverse())
        {
            var slice = bars.Take(windowEnd).ToArray();
            var ma20 = AverageClose(slice, 20);
            var ma60 = AverageClose(slice, 60);
            if (ma20 <= ma60)
            {
                break;
            }

            age++;
        }

        return age;
    }

    private static decimal CalculateClosePositionPercent(KLineBar? currentBar, decimal currentPrice)
    {
        if (currentBar is null || currentBar.High <= currentBar.Low)
        {
            return 100m;
        }

        return (currentPrice - currentBar.Low) / (currentBar.High - currentBar.Low) * 100m;
    }

    private static decimal CalculateUpperShadowPercent(KLineBar? currentBar, decimal currentPrice)
    {
        if (currentBar is null || currentBar.High <= currentBar.Low)
        {
            return 0m;
        }

        var bodyTop = Math.Max(currentBar.Open, currentPrice);
        return Math.Max(currentBar.High - bodyTop, 0m) / (currentBar.High - currentBar.Low) * 100m;
    }

    private static string? BuildRisk(
        decimal priceAboveMa20Percent,
        decimal recentFiveDayChangePercent,
        decimal upperShadowPercent,
        decimal changePercent)
    {
        var risks = new List<string>();
        if (priceAboveMa20Percent > 5m)
        {
            risks.Add("距离 MA20 偏远，回撤空间变大");
        }

        if (recentFiveDayChangePercent > 7m)
        {
            risks.Add("近 5 日涨幅偏高，趋势延续可能进入加速末段");
        }

        if (changePercent > 4m)
        {
            risks.Add("当日涨幅偏高，注意高位分歧");
        }

        if (upperShadowPercent > 15m)
        {
            risks.Add("存在上影线，需要确认趋势承接");
        }

        return risks.Count == 0 ? null : string.Join("；", risks);
    }
}
