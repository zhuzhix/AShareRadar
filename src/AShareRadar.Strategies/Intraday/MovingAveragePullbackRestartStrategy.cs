using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class MovingAveragePullbackRestartStrategy : ISignalStrategy
{
    private const int RequiredBarCount = 60;
    private const int PullbackLookback = 12;
    private const int SupportLookback = 20;
    private const decimal MaxDistanceFromSupportPercent = 8m;
    private const decimal PullbackToleranceUpperPercent = 3m;
    private const decimal MaxBreakdownPercent = 4.5m;
    private const decimal MinRestartVolumeRatio = 1.0m;
    private const decimal MaxRecentFiveDayChangePercent = 13m;
    private const decimal MaxUpperShadowPercent = 40m;
    private const decimal MinClosePositionPercent = 55m;

    public string Code => "moving-average-pullback-restart";

    public string Name => "均线回踩再启动";

    public StrategyType Type => StrategyType.PullbackWatch;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.PatternValidation,
        StrategySignalAction.PullbackWait,
        new StrategyDataRequirement(
            RequiresRealtimeQuote: true,
            RequiresDailyKLine: true,
            RequiresMinuteKLine: false,
            RequiresSectorData: false,
            RequiresCapitalFlow: false,
            MinDailyBarCount: RequiredBarCount),
        new Dictionary<string, string>
        {
            ["ma_support"] = "20/30",
            ["ma_long"] = "60",
            ["pullback_lookback"] = PullbackLookback.ToString(),
            ["max_distance_from_support_percent"] = MaxDistanceFromSupportPercent.ToString("F0"),
            ["min_restart_volume_ratio"] = MinRestartVolumeRatio.ToString("F1")
        },
        "识别中期趋势未破、回踩 MA20/MA30 支撑后重新放量站回的二买观察机会。");

    public Task<IReadOnlyList<StrategySignal>> EvaluateAsync(
        StrategyContext context,
        CancellationToken cancellationToken)
    {
        var isObservationRun = context.RunMode == StrategyRunMode.Observation;
        var signals = context.Snapshot.Quotes
            .Where(item => item.Price > 0
                && (isObservationRun
                    ? item.Amount >= 30_000_000m && item.ChangePercent <= 5.5m
                    : item.ChangePercent >= 0.3m && item.VolumeRatio >= 0.9m))
            .Select(item => BuildSignal(item, context, isObservationRun))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Score)
            .Take(isObservationRun ? 12 : 5)
            .ToArray();

        return Task.FromResult<IReadOnlyList<StrategySignal>>(signals);
    }

    private StrategySignal? BuildSignal(StockQuote quote, StrategyContext context, bool isObservationRun)
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
        var ma30 = AverageClose(historyBars, 30);
        var ma60 = AverageClose(historyBars, 60);
        if (ma5 <= 0 || ma10 <= 0 || ma20 <= 0 || ma30 <= 0 || ma60 <= 0)
        {
            return null;
        }

        var latestClose = historyBars[^1].Close;
        var previousMa20 = AverageClose(historyBars.Take(historyBars.Length - 1).ToArray(), 20);
        var supportLine = Math.Max(ma20, ma30);
        var trendStrengthPercent = (ma20 - ma60) / ma60 * 100m;
        var priceAboveSupportPercent = (quote.Price - supportLine) / supportLine * 100m;
        var priceAboveMa20Percent = (quote.Price - ma20) / ma20 * 100m;
        var recentPullbackBars = historyBars.TakeLast(PullbackLookback).ToArray();
        var recentPullbackLow = recentPullbackBars.Min(item => item.Low);
        var pullbackDistancePercent = (recentPullbackLow - supportLine) / supportLine * 100m;
        var recentLowestClose = recentPullbackBars.Min(item => item.Close);
        var closeBreakdownPercent = (recentLowestClose - supportLine) / supportLine * 100m;
        var ma20SlopePercent = previousMa20 > 0 ? (ma20 - previousMa20) / previousMa20 * 100m : 0m;
        var recentFiveDayChangePercent = CalculateWindowChange(historyBars, 5);
        var pullbackVolumeRatio = CalculatePullbackVolumeRatio(historyBars);
        var closePositionPercent = CalculateClosePositionPercent(currentBar, quote.Price);
        var upperShadowPercent = CalculateUpperShadowPercent(currentBar, quote.Price);

        var structuralFailedConditions = BuildStructuralFailedConditions(
            ma20,
            ma30,
            ma60,
            trendStrengthPercent,
            pullbackDistancePercent,
            closeBreakdownPercent,
            pullbackVolumeRatio,
            recentFiveDayChangePercent);
        if (structuralFailedConditions.Count > 0)
        {
            return null;
        }

        var failedConditions = BuildFailedConditions(
            ma20,
            ma30,
            ma60,
            trendStrengthPercent,
            pullbackDistancePercent,
            closeBreakdownPercent,
            priceAboveSupportPercent,
            quote.Price,
            latestClose,
            quote.VolumeRatio,
            pullbackVolumeRatio,
            recentFiveDayChangePercent,
            closePositionPercent,
            upperShadowPercent);
        if (failedConditions.Count > 0 && !isObservationRun)
        {
            return null;
        }

        var isRealtimeConfirmed = failedConditions.Count == 0;
        var action = !isRealtimeConfirmed
            ? StrategySignalAction.Watch
            : priceAboveSupportPercent <= 3m
            ? StrategySignalAction.PullbackWait
            : StrategySignalAction.Candidate;
        var confidence = !isRealtimeConfirmed
            ? StrategySignalConfidence.Low
            : trendStrengthPercent >= 4m
            && quote.VolumeRatio >= 1.2m
            && pullbackVolumeRatio <= 1.05m
            && upperShadowPercent <= 25m
            ? StrategySignalConfidence.High
            : StrategySignalConfidence.Medium;
        var score = 64m
            + Math.Min(trendStrengthPercent * 1.6m, 12m)
            + Math.Min(quote.VolumeRatio * 4m, 10m)
            + Math.Max(8m - Math.Abs(pullbackDistancePercent), 0m)
            + Math.Max(7m - priceAboveSupportPercent, 0m)
            + Math.Max(5m - Math.Max(pullbackVolumeRatio - 1m, 0m) * 10m, 0m)
            + Math.Min(quote.ChangePercent * 2m, 8m);

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            Score: score,
            Price: quote.Price,
            Reason: $"MA20 {ma20:F2}、MA30 {ma30:F2} 高于 MA60 {ma60:F2}，近 {PullbackLookback} 日回踩支撑线 {supportLine:F2}，当前高出支撑 {priceAboveSupportPercent:F1}%，量比 {quote.VolumeRatio:F2}。",
            Risk: BuildRisk(priceAboveSupportPercent, ma20SlopePercent, quote.ChangePercent, pullbackVolumeRatio, upperShadowPercent),
            Action: action,
            Confidence: confidence,
            Stage: StrategyStage.PatternValidation,
            Metrics: new Dictionary<string, decimal>
            {
                ["ma5"] = ma5,
                ["ma10"] = ma10,
                ["ma20"] = ma20,
                ["ma30"] = ma30,
                ["ma60"] = ma60,
                ["support_line"] = supportLine,
                ["trend_strength_percent"] = trendStrengthPercent,
                ["pullback_low"] = recentPullbackLow,
                ["pullback_distance_percent"] = pullbackDistancePercent,
                ["close_breakdown_percent"] = closeBreakdownPercent,
                ["price_above_support_percent"] = priceAboveSupportPercent,
                ["price_above_ma20_percent"] = priceAboveMa20Percent,
                ["ma20_slope_percent"] = ma20SlopePercent,
                ["pullback_volume_ratio"] = pullbackVolumeRatio,
                ["recent_5d_change_percent"] = recentFiveDayChangePercent,
                ["close_position_percent"] = closePositionPercent,
                ["upper_shadow_percent"] = upperShadowPercent,
                ["change_percent"] = quote.ChangePercent,
                ["volume_ratio"] = quote.VolumeRatio
            },
            Tags: ["均线回踩", "趋势延续", "缩量回踩", action == StrategySignalAction.PullbackWait ? "等确认" : "再启动候选"],
            PassedConditions:
            [
                $"MA20 {ma20:F2}、MA30 {ma30:F2} > MA60 {ma60:F2}",
                $"近 {PullbackLookback} 日低点回踩到支撑线 {supportLine:F2} 附近",
                $"最低收盘未明显跌破支撑，偏离 {closeBreakdownPercent:F1}%",
                $"当前价重新站上支撑，距离 {priceAboveSupportPercent:F1}%",
                $"回踩量能比 {pullbackVolumeRatio:F2}，未明显放量下跌",
                $"再启动量比 {quote.VolumeRatio:F2} >= {MinRestartVolumeRatio:F1}"
            ],
            FailedConditions: action == StrategySignalAction.PullbackWait
                ? ["离支撑较近，仍需盘中承接和放量确认"]
                : [],
            StopLossPrice: Math.Round(supportLine * 0.97m, 2),
            TakeProfitPrice: Math.Round(quote.Price * 1.06m, 2));
    }

    private static List<string> BuildStructuralFailedConditions(
        decimal ma20,
        decimal ma30,
        decimal ma60,
        decimal trendStrengthPercent,
        decimal pullbackDistancePercent,
        decimal closeBreakdownPercent,
        decimal pullbackVolumeRatio,
        decimal recentFiveDayChangePercent)
    {
        var failed = new List<string>();
        if (ma20 <= ma60 || ma30 <= ma60 || trendStrengthPercent < 1m)
        {
            failed.Add($"中期趋势不足，MA20/MA30 未有效强于 MA60，趋势强度 {trendStrengthPercent:F1}%");
        }

        if (pullbackDistancePercent > PullbackToleranceUpperPercent || pullbackDistancePercent < -MaxBreakdownPercent)
        {
            failed.Add($"回踩距离 {pullbackDistancePercent:F1}% 不在支撑附近");
        }

        if (closeBreakdownPercent < -MaxBreakdownPercent)
        {
            failed.Add($"最低收盘跌破支撑 {Math.Abs(closeBreakdownPercent):F1}%");
        }

        if (pullbackVolumeRatio > 1.25m)
        {
            failed.Add($"回踩阶段放量偏大，量能比 {pullbackVolumeRatio:F2}");
        }

        if (recentFiveDayChangePercent > MaxRecentFiveDayChangePercent)
        {
            failed.Add($"近 5 日涨幅 {recentFiveDayChangePercent:F1}% 过热");
        }

        return failed;
    }

    private static List<string> BuildFailedConditions(
        decimal ma20,
        decimal ma30,
        decimal ma60,
        decimal trendStrengthPercent,
        decimal pullbackDistancePercent,
        decimal closeBreakdownPercent,
        decimal priceAboveSupportPercent,
        decimal currentPrice,
        decimal latestClose,
        decimal volumeRatio,
        decimal pullbackVolumeRatio,
        decimal recentFiveDayChangePercent,
        decimal closePositionPercent,
        decimal upperShadowPercent)
    {
        var failed = new List<string>();
        if (ma20 <= ma60 || ma30 <= ma60 || trendStrengthPercent < 1m)
        {
            failed.Add($"中期趋势不足，MA20/MA30 未有效强于 MA60，趋势强度 {trendStrengthPercent:F1}%");
        }

        if (pullbackDistancePercent > PullbackToleranceUpperPercent || pullbackDistancePercent < -MaxBreakdownPercent)
        {
            failed.Add($"回踩距离 {pullbackDistancePercent:F1}% 不在支撑附近");
        }

        if (closeBreakdownPercent < -MaxBreakdownPercent)
        {
            failed.Add($"最低收盘跌破支撑 {Math.Abs(closeBreakdownPercent):F1}%");
        }

        if (currentPrice <= latestClose || priceAboveSupportPercent < 0m)
        {
            failed.Add("当前价尚未重新站回支撑并强于上一日收盘");
        }

        if (priceAboveSupportPercent > MaxDistanceFromSupportPercent)
        {
            failed.Add($"距离支撑 {priceAboveSupportPercent:F1}% > {MaxDistanceFromSupportPercent:F0}%");
        }

        if (volumeRatio < MinRestartVolumeRatio)
        {
            failed.Add($"再启动量比 {volumeRatio:F2} < {MinRestartVolumeRatio:F1}");
        }

        if (pullbackVolumeRatio > 1.25m)
        {
            failed.Add($"回踩阶段放量偏大，量能比 {pullbackVolumeRatio:F2}");
        }

        if (recentFiveDayChangePercent > MaxRecentFiveDayChangePercent)
        {
            failed.Add($"近 5 日涨幅 {recentFiveDayChangePercent:F1}% 过热");
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

    private static decimal CalculatePullbackVolumeRatio(IReadOnlyList<KLineBar> bars)
    {
        if (bars.Count < SupportLookback + PullbackLookback)
        {
            return 1m;
        }

        var pullbackVolume = bars.TakeLast(PullbackLookback).Average(item => item.Volume);
        var baselineVolume = bars
            .Take(bars.Count - PullbackLookback)
            .TakeLast(SupportLookback)
            .Average(item => item.Volume);
        return baselineVolume > 0 ? pullbackVolume / baselineVolume : 1m;
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
        decimal priceAboveSupportPercent,
        decimal ma20SlopePercent,
        decimal changePercent,
        decimal pullbackVolumeRatio,
        decimal upperShadowPercent)
    {
        var risks = new List<string>();
        if (priceAboveSupportPercent > 5m)
        {
            risks.Add("距离支撑偏远，追高风险增加");
        }

        if (ma20SlopePercent < 0m)
        {
            risks.Add("MA20 斜率转弱，趋势可能不稳");
        }

        if (changePercent > 4m)
        {
            risks.Add("当日涨幅偏高，适合等待回踩确认");
        }

        if (pullbackVolumeRatio > 1.05m)
        {
            risks.Add("回踩量能未明显萎缩，承接质量一般");
        }

        if (upperShadowPercent > 25m)
        {
            risks.Add("存在上影线，需要确认收盘能否守住支撑");
        }

        return risks.Count == 0 ? null : string.Join("；", risks);
    }
}
