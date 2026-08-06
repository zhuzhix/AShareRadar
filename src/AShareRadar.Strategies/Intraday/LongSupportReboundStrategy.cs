using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class LongSupportReboundStrategy : ISignalStrategy
{
    private const int RequiredBarCount = 120;
    private const int PreferredBarCount = 250;
    private const int DrawdownLookback = 60;
    private const decimal MinDrawdown60Percent = 18m;
    private const decimal MaxDrawdown60Percent = 42m;
    private const decimal MaxDistanceFrom60DayLowPercent = 16m;
    private const decimal MaxDistanceFromLongSupportPercent = 8m;
    private const decimal MinRepairFrom3DayLowPercent = 3m;
    private const decimal MinWatchAmount = 30_000_000m;
    private const decimal MinCandidateAmount = 50_000_000m;

    public string Code => "long-support-rebound";

    public string Name => "长线支撑二次探底反弹";

    public StrategyType Type => StrategyType.LongTermWatch;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.PatternValidation,
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
            ["drawdown_60d_range_percent"] = $"{MinDrawdown60Percent:F0}-{MaxDrawdown60Percent:F0}",
            ["max_distance_from_60d_low_percent"] = MaxDistanceFrom60DayLowPercent.ToString("F0"),
            ["max_distance_from_long_support_percent"] = MaxDistanceFromLongSupportPercent.ToString("F0"),
            ["min_repair_from_3d_low_percent"] = MinRepairFrom3DayLowPercent.ToString("F0")
        },
        "识别经历中期回撤后，靠近 MA120/MA250 长线支撑并出现低位修复的波段反弹观察机会。");

    public Task<IReadOnlyList<StrategySignal>> EvaluateAsync(
        StrategyContext context,
        CancellationToken cancellationToken)
    {
        var signals = context.Snapshot.Quotes
            .Where(item => item.Price > 0 && item.Amount >= MinWatchAmount && !IsExcludedStock(item))
            .Select(item => BuildSignal(item, context))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Score)
            .Take(8)
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
            .TakeLast(PreferredBarCount)
            .ToArray();
        if (historyBars.Length < RequiredBarCount)
        {
            historyBars = orderedBars.TakeLast(PreferredBarCount).ToArray();
        }

        if (historyBars.Length < RequiredBarCount)
        {
            return null;
        }

        var ma5 = AverageClose(historyBars, 5);
        var ma20 = AverageClose(historyBars, 20);
        var ma60 = AverageClose(historyBars, 60);
        var ma120 = AverageClose(historyBars, 120);
        var ma250 = AverageClose(historyBars, 250);
        if (ma5 <= 0 || ma20 <= 0 || ma60 <= 0 || ma120 <= 0)
        {
            return null;
        }

        var recent60 = historyBars.TakeLast(DrawdownLookback).ToArray();
        var high60 = recent60.Max(item => item.High);
        var low60 = recent60.Min(item => item.Low);
        if (high60 <= 0 || low60 <= 0)
        {
            return null;
        }

        var drawdown60Percent = (high60 - quote.Price) / high60 * 100m;
        var distanceFrom60DayLowPercent = (quote.Price - low60) / low60 * 100m;
        var distanceFromMa120Percent = (quote.Price - ma120) / ma120 * 100m;
        var distanceFromMa250Percent = ma250 > 0 ? (quote.Price - ma250) / ma250 * 100m : 999m;
        var longSupportDistancePercent = Math.Min(Math.Abs(distanceFromMa120Percent), Math.Abs(distanceFromMa250Percent));
        var repairFrom3DayLowPercent = CalculateRepairFromRecentLow(historyBars, currentBar, quote.Price, 3);
        var repairFrom5DayLowPercent = CalculateRepairFromRecentLow(historyBars, currentBar, quote.Price, 5);
        var lowerShadowPercent = CalculateLowerShadowPercent(currentBar, quote.Price);
        var closePositionPercent = CalculateClosePositionPercent(currentBar, quote.Price);
        var priceAboveMa5Percent = (quote.Price - ma5) / ma5 * 100m;
        var priceAboveMa20Percent = (quote.Price - ma20) / ma20 * 100m;
        var trendRecoveryPercent = (ma20 - ma60) / ma60 * 100m;
        var dailyVolumeRatio = CalculateDailyVolumeRatio(historyBars, currentBar);
        var effectiveVolumeRatio = quote.VolumeRatio > 0 ? quote.VolumeRatio : dailyVolumeRatio;

        var failed = BuildFailedConditions(
            drawdown60Percent,
            distanceFrom60DayLowPercent,
            longSupportDistancePercent,
            repairFrom3DayLowPercent,
            lowerShadowPercent,
            priceAboveMa5Percent,
            quote.Amount);
        if (failed.Count > 0)
        {
            return null;
        }

        var action = ResolveAction(
            drawdown60Percent,
            distanceFrom60DayLowPercent,
            repairFrom3DayLowPercent,
            priceAboveMa5Percent,
            quote.ChangePercent,
            effectiveVolumeRatio,
            quote.Amount,
            closePositionPercent);
        var confidence = ResolveConfidence(action, longSupportDistancePercent, repairFrom3DayLowPercent, effectiveVolumeRatio, closePositionPercent);
        var score = CalculateScore(
            drawdown60Percent,
            distanceFrom60DayLowPercent,
            longSupportDistancePercent,
            repairFrom3DayLowPercent,
            lowerShadowPercent,
            priceAboveMa5Percent,
            effectiveVolumeRatio,
            quote.Amount);

        var supportLabel = Math.Abs(distanceFromMa250Percent) < Math.Abs(distanceFromMa120Percent)
            ? "MA250"
            : "MA120";
        var supportLine = supportLabel == "MA250" && ma250 > 0 ? ma250 : ma120;

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            Score: score,
            Price: quote.Price,
            Reason: $"60日高点回撤 {drawdown60Percent:F1}%，距60日低点 {distanceFrom60DayLowPercent:F1}%，贴近{supportLabel}支撑 {longSupportDistancePercent:F1}%，低位修复 {repairFrom3DayLowPercent:F1}%。",
            Risk: BuildRisk(distanceFrom60DayLowPercent, priceAboveMa20Percent, trendRecoveryPercent, effectiveVolumeRatio, lowerShadowPercent),
            Action: action,
            Confidence: confidence,
            Stage: StrategyStage.PatternValidation,
            Metrics: new Dictionary<string, decimal>
            {
                ["drawdown_60d_percent"] = drawdown60Percent,
                ["distance_from_60d_low_percent"] = distanceFrom60DayLowPercent,
                ["distance_from_long_support_percent"] = longSupportDistancePercent,
                ["distance_from_ma120_percent"] = distanceFromMa120Percent,
                ["distance_from_ma250_percent"] = ma250 > 0 ? distanceFromMa250Percent : 0m,
                ["repair_from_3d_low_percent"] = repairFrom3DayLowPercent,
                ["repair_from_5d_low_percent"] = repairFrom5DayLowPercent,
                ["lower_shadow_percent"] = lowerShadowPercent,
                ["close_position_percent"] = closePositionPercent,
                ["price_above_ma5_percent"] = priceAboveMa5Percent,
                ["price_above_ma20_percent"] = priceAboveMa20Percent,
                ["trend_recovery_percent"] = trendRecoveryPercent,
                ["ma5"] = ma5,
                ["ma20"] = ma20,
                ["ma60"] = ma60,
                ["ma120"] = ma120,
                ["ma250"] = ma250,
                ["support_line"] = supportLine,
                ["change_percent"] = quote.ChangePercent,
                ["volume_ratio"] = effectiveVolumeRatio,
                ["daily_volume_ratio"] = dailyVolumeRatio,
                ["amount"] = quote.Amount
            },
            Tags: ["长线支撑", "二次探底", action == StrategySignalAction.Watch ? "观察" : action == StrategySignalAction.Candidate ? "候选" : "确认"],
            PassedConditions:
            [
                $"60日回撤 {drawdown60Percent:F1}% 位于 {MinDrawdown60Percent:F0}-{MaxDrawdown60Percent:F0}%",
                $"距60日低点 {distanceFrom60DayLowPercent:F1}% <= {MaxDistanceFrom60DayLowPercent:F0}%",
                $"距长线支撑 {longSupportDistancePercent:F1}% <= {MaxDistanceFromLongSupportPercent:F0}%",
                $"低位修复 {repairFrom3DayLowPercent:F1}% / 下影线 {lowerShadowPercent:F1}% 有止跌迹象",
                $"成交额 {quote.Amount / 100_000_000m:F2} 亿 >= {MinWatchAmount / 100_000_000m:F2} 亿"
            ],
            FailedConditions: action == StrategySignalAction.Watch
                ? ["仍处于低位修复观察，等待站回 MA5 或放量承接后再提高等级"]
                : [],
            StopLossPrice: Math.Round(Math.Max(low60 * 0.97m, supportLine * 0.96m), 2),
            TakeProfitPrice: Math.Round(quote.Price * 1.08m, 2));
    }

    private static List<string> BuildFailedConditions(
        decimal drawdown60Percent,
        decimal distanceFrom60DayLowPercent,
        decimal longSupportDistancePercent,
        decimal repairFrom3DayLowPercent,
        decimal lowerShadowPercent,
        decimal priceAboveMa5Percent,
        decimal amount)
    {
        var failed = new List<string>();
        if (drawdown60Percent < MinDrawdown60Percent || drawdown60Percent > MaxDrawdown60Percent)
        {
            failed.Add($"60日回撤 {drawdown60Percent:F1}% 不在目标区间");
        }

        if (distanceFrom60DayLowPercent < -1m || distanceFrom60DayLowPercent > MaxDistanceFrom60DayLowPercent)
        {
            failed.Add($"距60日低点 {distanceFrom60DayLowPercent:F1}% 超出范围");
        }

        if (longSupportDistancePercent > MaxDistanceFromLongSupportPercent)
        {
            failed.Add($"距MA120/MA250长线支撑 {longSupportDistancePercent:F1}% 过远");
        }

        if (repairFrom3DayLowPercent < MinRepairFrom3DayLowPercent
            && lowerShadowPercent < 3m
            && priceAboveMa5Percent < -3m)
        {
            failed.Add("低位修复、下影承接、MA5修复均不足");
        }

        if (amount < MinWatchAmount)
        {
            failed.Add($"成交额不足 {MinWatchAmount / 100_000_000m:F2} 亿");
        }

        return failed;
    }

    private static StrategySignalAction ResolveAction(
        decimal drawdown60Percent,
        decimal distanceFrom60DayLowPercent,
        decimal repairFrom3DayLowPercent,
        decimal priceAboveMa5Percent,
        decimal changePercent,
        decimal volumeRatio,
        decimal amount,
        decimal closePositionPercent)
    {
        var candidate = drawdown60Percent >= 22m
            && drawdown60Percent <= MaxDrawdown60Percent
            && distanceFrom60DayLowPercent <= 12m
            && repairFrom3DayLowPercent >= 4m
            && priceAboveMa5Percent >= 0m
            && amount >= MinCandidateAmount
            && volumeRatio >= 0.8m
            && volumeRatio <= 2.8m;
        if (!candidate)
        {
            return StrategySignalAction.Watch;
        }

        var confirm = changePercent >= 2m
            && volumeRatio >= 1.05m
            && closePositionPercent >= 60m
            && distanceFrom60DayLowPercent <= 10m;
        return confirm ? StrategySignalAction.Confirm : StrategySignalAction.Candidate;
    }

    private static StrategySignalConfidence ResolveConfidence(
        StrategySignalAction action,
        decimal longSupportDistancePercent,
        decimal repairFrom3DayLowPercent,
        decimal volumeRatio,
        decimal closePositionPercent)
    {
        if (action == StrategySignalAction.Confirm
            && longSupportDistancePercent <= 5m
            && repairFrom3DayLowPercent >= 5m
            && volumeRatio >= 1.05m
            && closePositionPercent >= 65m)
        {
            return StrategySignalConfidence.High;
        }

        return action == StrategySignalAction.Watch
            ? StrategySignalConfidence.Low
            : StrategySignalConfidence.Medium;
    }

    private static decimal CalculateScore(
        decimal drawdown60Percent,
        decimal distanceFrom60DayLowPercent,
        decimal longSupportDistancePercent,
        decimal repairFrom3DayLowPercent,
        decimal lowerShadowPercent,
        decimal priceAboveMa5Percent,
        decimal volumeRatio,
        decimal amount)
    {
        var drawdownScore = Math.Max(16m - Math.Abs(drawdown60Percent - 28m) * 0.8m, 0m);
        var lowScore = Math.Max(16m - distanceFrom60DayLowPercent, 0m);
        var supportScore = Math.Max(14m - longSupportDistancePercent * 1.5m, 0m);
        var repairScore = Math.Min(repairFrom3DayLowPercent * 2.5m, 14m);
        var shadowScore = Math.Min(lowerShadowPercent * 1.2m, 8m);
        var ma5Score = Math.Min(Math.Max(priceAboveMa5Percent + 3m, 0m) * 1.5m, 8m);
        var volumeScore = volumeRatio > 0 ? Math.Min(volumeRatio * 4m, 8m) : 2m;
        var amountScore = amount >= MinCandidateAmount ? 4m : 0m;

        return Math.Min(100m, 42m + drawdownScore + lowScore + supportScore + repairScore + shadowScore + ma5Score + volumeScore + amountScore);
    }

    private static bool IsExcludedStock(StockQuote quote)
    {
        var symbol = quote.Symbol.Trim();
        var name = quote.Name.Trim();
        return symbol.StartsWith("8", StringComparison.Ordinal)
            || symbol.StartsWith("4", StringComparison.Ordinal)
            || name.Contains("ST", StringComparison.OrdinalIgnoreCase)
            || name.Contains("*ST", StringComparison.OrdinalIgnoreCase);
    }

    private static KLineBar? TryGetCurrentBar(IReadOnlyList<KLineBar> bars, DateOnly tradingDate)
    {
        return bars.LastOrDefault(item => DateOnly.FromDateTime(item.TradingTime) == tradingDate);
    }

    private static decimal AverageClose(IReadOnlyList<KLineBar> bars, int count)
    {
        return bars.Count < count ? 0m : bars.TakeLast(count).Average(item => item.Close);
    }

    private static decimal CalculateRepairFromRecentLow(
        IReadOnlyList<KLineBar> historyBars,
        KLineBar? currentBar,
        decimal currentPrice,
        int count)
    {
        var lows = historyBars.TakeLast(count).Select(item => item.Low).ToList();
        if (currentBar is not null && currentBar.Low > 0)
        {
            lows.Add(currentBar.Low);
        }

        var recentLow = lows.Count > 0 ? lows.Min() : 0m;
        return recentLow > 0 ? (currentPrice - recentLow) / recentLow * 100m : 0m;
    }

    private static decimal CalculateDailyVolumeRatio(IReadOnlyList<KLineBar> historyBars, KLineBar? currentBar)
    {
        if (historyBars.Count < 20)
        {
            return 0m;
        }

        var averageVolume20 = historyBars.TakeLast(20).Average(item => item.Volume);
        if (averageVolume20 <= 0)
        {
            return 0m;
        }

        var currentVolume = currentBar?.Volume ?? historyBars[^1].Volume;
        return currentVolume / averageVolume20;
    }

    private static decimal CalculateClosePositionPercent(KLineBar? currentBar, decimal currentPrice)
    {
        if (currentBar is null || currentBar.High <= currentBar.Low)
        {
            return 100m;
        }

        return (currentPrice - currentBar.Low) / (currentBar.High - currentBar.Low) * 100m;
    }

    private static decimal CalculateLowerShadowPercent(KLineBar? currentBar, decimal currentPrice)
    {
        if (currentBar is null || currentBar.High <= currentBar.Low)
        {
            return 0m;
        }

        var bodyBottom = Math.Min(currentBar.Open, currentPrice);
        return Math.Max(bodyBottom - currentBar.Low, 0m) / (currentBar.High - currentBar.Low) * 100m;
    }

    private static string? BuildRisk(
        decimal distanceFrom60DayLowPercent,
        decimal priceAboveMa20Percent,
        decimal trendRecoveryPercent,
        decimal volumeRatio,
        decimal lowerShadowPercent)
    {
        var risks = new List<string>();
        if (distanceFrom60DayLowPercent > 10m)
        {
            risks.Add("距低点已有一定修复，追高性价比下降");
        }

        if (priceAboveMa20Percent < -6m)
        {
            risks.Add("仍明显低于MA20，短期趋势尚未修复");
        }

        if (trendRecoveryPercent < -8m)
        {
            risks.Add("MA20低于MA60较多，可能只是弱反抽");
        }

        if (volumeRatio > 2.8m)
        {
            risks.Add("量能放大过快，注意冲高回落");
        }

        if (lowerShadowPercent < 3m)
        {
            risks.Add("下影承接不明显，仍需继续观察确认");
        }

        return risks.Count == 0 ? null : string.Join("；", risks);
    }
}
