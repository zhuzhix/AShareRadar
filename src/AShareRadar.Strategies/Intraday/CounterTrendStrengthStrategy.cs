using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class CounterTrendStrengthStrategy : ISignalStrategy
{
    private const int RequiredBarCount = 60;
    private const int RecentLookback = 5;
    private const int DrawdownLookback = 20;
    private const decimal MaxMarketAverageChangePercent = 0.5m;
    private const decimal MinRelativeStrengthPercent = 1.5m;
    private const decimal MinChangePercent = 0.4m;
    private const decimal MinVolumeRatio = 0.9m;
    private const decimal MaxDistanceFromMa20Percent = 10m;
    private const decimal MaxRecentFiveDayChangePercent = 12m;
    private const decimal MaxUpperShadowPercent = 35m;
    private const decimal MinClosePositionPercent = 55m;

    public string Code => "counter-trend-strength";

    public string Name => "逆势走强";

    public StrategyType Type => StrategyType.IntradayOpportunity;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.CandidateRanking,
        StrategySignalAction.Watch,
        new StrategyDataRequirement(
            RequiresRealtimeQuote: true,
            RequiresDailyKLine: true,
            RequiresMinuteKLine: false,
            RequiresSectorData: false,
            RequiresCapitalFlow: false,
            MinDailyBarCount: RequiredBarCount),
        new Dictionary<string, string>
        {
            ["max_market_average_change_percent"] = MaxMarketAverageChangePercent.ToString("F1"),
            ["min_relative_strength_percent"] = MinRelativeStrengthPercent.ToString("F1"),
            ["max_distance_from_ma20_percent"] = MaxDistanceFromMa20Percent.ToString("F0"),
            ["max_recent_5d_change_percent"] = MaxRecentFiveDayChangePercent.ToString("F0")
        },
        "在市场平均表现偏弱时，寻找仍能红盘承接、相对强度突出且趋势未破位的观察候选。");

    public Task<IReadOnlyList<StrategySignal>> EvaluateAsync(
        StrategyContext context,
        CancellationToken cancellationToken)
    {
        var isObservationRun = context.RunMode == StrategyRunMode.Observation;
        if (context.Snapshot.Quotes.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<StrategySignal>>([]);
        }

        var marketAverageChange = context.MarketStats?.AverageChangePercent
            ?? context.Snapshot.Quotes.Average(item => item.ChangePercent);
        if (marketAverageChange > MaxMarketAverageChangePercent)
        {
            return Task.FromResult<IReadOnlyList<StrategySignal>>([]);
        }

        var signals = context.Snapshot.Quotes
            .Where(item => item.Price > 0
                && (isObservationRun || item.ChangePercent >= MinChangePercent)
                && item.ChangePercent - marketAverageChange >= MinRelativeStrengthPercent
                && (isObservationRun || item.VolumeRatio >= MinVolumeRatio))
            .Select(item => BuildSignal(item, context, marketAverageChange, isObservationRun))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Score)
            .Take(isObservationRun ? 12 : 5)
            .ToArray();

        return Task.FromResult<IReadOnlyList<StrategySignal>>(signals);
    }

    private StrategySignal? BuildSignal(StockQuote quote, StrategyContext context, decimal marketAverageChange, bool isObservationRun)
    {
        if (context.DailyBarsBySymbol is null
            || !context.DailyBarsBySymbol.TryGetValue(quote.Symbol, out var bars)
            || bars.Count < RequiredBarCount)
        {
            return BuildRealtimeOnlySignal(quote, marketAverageChange);
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
        var ma20 = AverageClose(historyBars, 20);
        var ma60 = AverageClose(historyBars, 60);
        if (ma5 <= 0 || ma20 <= 0 || ma60 <= 0)
        {
            return null;
        }

        var latestClose = historyBars[^1].Close;
        var relativeStrengthPercent = quote.ChangePercent - marketAverageChange;
        var priceAboveMa20Percent = (quote.Price - ma20) / ma20 * 100m;
        var priceAboveMa5Percent = (quote.Price - ma5) / ma5 * 100m;
        var trendStrengthPercent = (ma20 - ma60) / ma60 * 100m;
        var recentFiveDayChangePercent = CalculateWindowChange(historyBars, RecentLookback);
        var drawdown20Percent = CalculateDrawdown(historyBars, DrawdownLookback, quote.Price);
        var closePositionPercent = CalculateClosePositionPercent(currentBar, quote.Price);
        var upperShadowPercent = CalculateUpperShadowPercent(currentBar, quote.Price);

        var structuralFailedConditions = BuildStructuralFailedConditions(
            relativeStrengthPercent,
            priceAboveMa20Percent,
            trendStrengthPercent,
            recentFiveDayChangePercent);
        if (structuralFailedConditions.Count > 0)
        {
            return null;
        }

        var failedConditions = BuildFailedConditions(
            relativeStrengthPercent,
            priceAboveMa20Percent,
            priceAboveMa5Percent,
            trendStrengthPercent,
            recentFiveDayChangePercent,
            quote.VolumeRatio,
            closePositionPercent,
            upperShadowPercent,
            quote.Price,
            latestClose);
        if (failedConditions.Count > 0 && !isObservationRun)
        {
            return null;
        }

        var isRealtimeConfirmed = failedConditions.Count == 0;
        var confidence = !isRealtimeConfirmed
            ? StrategySignalConfidence.Low
            : relativeStrengthPercent >= 2.5m
            && trendStrengthPercent >= 0m
            && quote.VolumeRatio >= 1.2m
            && upperShadowPercent <= 20m
            ? StrategySignalConfidence.High
            : StrategySignalConfidence.Medium;
        var score = 64m
            + Math.Min(relativeStrengthPercent * 4m, 18m)
            + Math.Min(Math.Max(quote.VolumeRatio, 0m) * 4m, 10m)
            + Math.Max(6m - Math.Abs(priceAboveMa20Percent), 0m)
            + Math.Max(8m - Math.Max(recentFiveDayChangePercent - 4m, 0m), 0m)
            + Math.Max(6m - Math.Abs(drawdown20Percent), 0m);

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            Score: score,
            Price: quote.Price,
            Reason: $"市场均值 {marketAverageChange:F2}%，个股涨幅 {quote.ChangePercent:F2}%，相对强度 {relativeStrengthPercent:F2}%，当前距离 MA20 {priceAboveMa20Percent:F1}%，量比 {quote.VolumeRatio:F2}。",
            Risk: BuildRisk(priceAboveMa20Percent, recentFiveDayChangePercent, upperShadowPercent, trendStrengthPercent),
            Action: confidence == StrategySignalConfidence.High && isRealtimeConfirmed ? StrategySignalAction.Candidate : StrategySignalAction.Watch,
            Confidence: confidence,
            Stage: StrategyStage.CandidateRanking,
            Metrics: new Dictionary<string, decimal>
            {
                ["market_average_change"] = marketAverageChange,
                ["relative_strength_percent"] = relativeStrengthPercent,
                ["ma5"] = ma5,
                ["ma20"] = ma20,
                ["ma60"] = ma60,
                ["trend_strength_percent"] = trendStrengthPercent,
                ["price_above_ma5_percent"] = priceAboveMa5Percent,
                ["price_above_ma20_percent"] = priceAboveMa20Percent,
                ["recent_5d_change_percent"] = recentFiveDayChangePercent,
                ["drawdown_20d_percent"] = drawdown20Percent,
                ["close_position_percent"] = closePositionPercent,
                ["upper_shadow_percent"] = upperShadowPercent,
                ["change_percent"] = quote.ChangePercent,
                ["volume_ratio"] = quote.VolumeRatio
            },
            Tags: ["逆势走强", "相对强度", confidence == StrategySignalConfidence.High ? "弱市候选" : "弱市观察"],
            PassedConditions:
            [
                $"市场均值 {marketAverageChange:F2}% <= {MaxMarketAverageChangePercent:F1}%",
                $"相对强度 {relativeStrengthPercent:F2}% >= {MinRelativeStrengthPercent:F1}%",
                $"当前价站上 MA5，距离 {priceAboveMa5Percent:F1}%",
                $"距离 MA20 {priceAboveMa20Percent:F1}% <= {MaxDistanceFromMa20Percent:F0}%",
                $"近 {RecentLookback} 日涨幅 {recentFiveDayChangePercent:F1}% <= {MaxRecentFiveDayChangePercent:F0}%",
                $"量比 {quote.VolumeRatio:F2} >= {MinVolumeRatio:F1}"
            ],
            FailedConditions: confidence == StrategySignalConfidence.High
                ? []
                : ["市场仍偏弱，优先观察持续承接，不宜直接强确认"],
            StopLossPrice: Math.Round(Math.Max(ma20 * 0.97m, quote.Price * 0.95m), 2),
            TakeProfitPrice: Math.Round(quote.Price * 1.05m, 2));
    }

    private StrategySignal BuildRealtimeOnlySignal(StockQuote quote, decimal marketAverageChange)
    {
        var relativeStrengthPercent = quote.ChangePercent - marketAverageChange;
        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            Score: 60m + Math.Min(relativeStrengthPercent * 4m, 16m) + Math.Min(quote.VolumeRatio * 3m, 8m),
            Price: quote.Price,
            Reason: $"市场均值 {marketAverageChange:F2}%，个股涨幅 {quote.ChangePercent:F2}%，相对强度 {relativeStrengthPercent:F2}%，历史日线不足，暂作为逆势观察。",
            Risk: "缺少趋势位置和支撑验证，只能作为观察候选。",
            Action: StrategySignalAction.Watch,
            Confidence: StrategySignalConfidence.Low,
            Stage: StrategyStage.CandidateRanking,
            Metrics: new Dictionary<string, decimal>
            {
                ["market_average_change"] = marketAverageChange,
                ["relative_strength_percent"] = relativeStrengthPercent,
                ["change_percent"] = quote.ChangePercent,
                ["volume_ratio"] = quote.VolumeRatio
            },
            Tags: ["逆势走强", "待结构验证"],
            PassedConditions:
            [
                $"市场均值 {marketAverageChange:F2}% <= {MaxMarketAverageChangePercent:F1}%",
                $"相对强度 {relativeStrengthPercent:F2}% >= {MinRelativeStrengthPercent:F1}"
            ],
            FailedConditions: ["历史日 K 数量不足，未完成趋势位置验证"],
            StopLossPrice: Math.Round(quote.Price * 0.95m, 2),
            TakeProfitPrice: Math.Round(quote.Price * 1.04m, 2));
    }

    private static List<string> BuildStructuralFailedConditions(
        decimal relativeStrengthPercent,
        decimal priceAboveMa20Percent,
        decimal trendStrengthPercent,
        decimal recentFiveDayChangePercent)
    {
        var failed = new List<string>();
        if (relativeStrengthPercent < MinRelativeStrengthPercent)
        {
            failed.Add($"相对强度 {relativeStrengthPercent:F2}% < {MinRelativeStrengthPercent:F1}%");
        }

        if (priceAboveMa20Percent < -3m || priceAboveMa20Percent > MaxDistanceFromMa20Percent)
        {
            failed.Add($"距离 MA20 {priceAboveMa20Percent:F1}% 不在合理区间");
        }

        if (trendStrengthPercent < -5m)
        {
            failed.Add($"中期趋势偏弱，MA20 低于 MA60 {Math.Abs(trendStrengthPercent):F1}%");
        }

        if (recentFiveDayChangePercent > MaxRecentFiveDayChangePercent)
        {
            failed.Add($"近 {RecentLookback} 日涨幅 {recentFiveDayChangePercent:F1}% 过热");
        }

        return failed;
    }

    private static List<string> BuildFailedConditions(
        decimal relativeStrengthPercent,
        decimal priceAboveMa20Percent,
        decimal priceAboveMa5Percent,
        decimal trendStrengthPercent,
        decimal recentFiveDayChangePercent,
        decimal volumeRatio,
        decimal closePositionPercent,
        decimal upperShadowPercent,
        decimal currentPrice,
        decimal latestClose)
    {
        var failed = new List<string>();
        if (relativeStrengthPercent < MinRelativeStrengthPercent)
        {
            failed.Add($"相对强度 {relativeStrengthPercent:F2}% < {MinRelativeStrengthPercent:F1}%");
        }

        if (currentPrice <= latestClose || priceAboveMa5Percent < 0m)
        {
            failed.Add("当前价尚未强于上一日收盘并站上 MA5");
        }

        if (priceAboveMa20Percent < -3m || priceAboveMa20Percent > MaxDistanceFromMa20Percent)
        {
            failed.Add($"距离 MA20 {priceAboveMa20Percent:F1}% 不在合理区间");
        }

        if (trendStrengthPercent < -5m)
        {
            failed.Add($"中期趋势偏弱，MA20 低于 MA60 {Math.Abs(trendStrengthPercent):F1}%");
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

    private static decimal CalculateDrawdown(IReadOnlyList<KLineBar> bars, int count, decimal currentPrice)
    {
        var high = bars.TakeLast(count).Max(item => item.High);
        return high > 0 ? (currentPrice - high) / high * 100m : 0m;
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
        decimal trendStrengthPercent)
    {
        var risks = new List<string>();
        if (trendStrengthPercent < 0m)
        {
            risks.Add("中期趋势尚未完全转强，逆势股次日波动可能较大");
        }

        if (priceAboveMa20Percent > 7m)
        {
            risks.Add("距离 MA20 偏远，追高风险增加");
        }

        if (recentFiveDayChangePercent > 8m)
        {
            risks.Add("近 5 日涨幅偏高，可能已经提前表现");
        }

        if (upperShadowPercent > 20m)
        {
            risks.Add("存在上影线，需要确认承接持续性");
        }

        return risks.Count == 0 ? null : string.Join("；", risks);
    }
}
