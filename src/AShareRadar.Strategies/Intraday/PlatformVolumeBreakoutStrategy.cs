using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class PlatformVolumeBreakoutStrategy : ISignalStrategy
{
    private const int WeeklyPlatformLookback = 24;
    private const int WeeklyRecentCompressionLookback = 12;
    private const int AverageDailyVolumeLookback = 20;
    private const decimal MinChangePercent = 1.2m;
    private const decimal MaxChangePercent = 6.5m;
    private const decimal MinVolumeRatio = 1.2m;
    private const decimal MinBreakoutPercent = 0.2m;
    private const decimal MaxBreakoutDistancePercent = 8m;
    private const decimal MaxWeeklyPlatformRangePercent = 35m;
    private const decimal MaxWeeklyRecentRangePercent = 28m;
    private const decimal ResistanceTouchTolerancePercent = 3m;
    private const int MinResistanceTouches = 2;
    private const decimal MinClosePositionPercent = 60m;
    private const decimal MaxUpperShadowPercent = 35m;
    private const decimal MinThirtyMinuteVolumeRatio = 1.5m;
    private const decimal MinThirtyMinuteClosePositionPercent = 70m;
    private const decimal MaxThirtyMinuteUpperShadowPercent = 35m;

    public string Code => "platform-volume-breakout";

    public string Name => "平台放量突破";

    public StrategyType Type => StrategyType.IntradayOpportunity;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.TriggerConfirmation,
        StrategySignalAction.Candidate,
        new StrategyDataRequirement(
            RequiresRealtimeQuote: true,
            RequiresDailyKLine: true,
            RequiresMinuteKLine: false,
            RequiresSectorData: false,
            RequiresCapitalFlow: false,
            MinDailyBarCount: AverageDailyVolumeLookback + 1,
            RequiresWeeklyKLine: true,
            RequiresThirtyMinuteKLine: true),
        new Dictionary<string, string>
        {
            ["weekly_platform_lookback"] = WeeklyPlatformLookback.ToString(),
            ["weekly_recent_compression_lookback"] = WeeklyRecentCompressionLookback.ToString(),
            ["min_change_percent"] = MinChangePercent.ToString("F1"),
            ["max_change_percent"] = MaxChangePercent.ToString("F1"),
            ["min_volume_ratio"] = MinVolumeRatio.ToString("F1"),
            ["max_weekly_platform_range_percent"] = MaxWeeklyPlatformRangePercent.ToString("F0"),
            ["max_upper_shadow_percent"] = MaxUpperShadowPercent.ToString("F0"),
            ["max_result_count"] = "5"
        },
        "以周线平台作为主结构，结合日内放量、突破距离、上影线和当前位置确认中级别平台突破。");

    public Task<IReadOnlyList<StrategySignal>> EvaluateAsync(
        StrategyContext context,
        CancellationToken cancellationToken)
    {
        var isObservationRun = context.RunMode == StrategyRunMode.Observation;
        var signals = context.Snapshot.Quotes
            .Where(item => item.Price > 0
                && (isObservationRun
                    ? item.Amount >= 30_000_000m && item.ChangePercent <= MaxChangePercent
                    : item.ChangePercent >= MinChangePercent
                        && item.ChangePercent <= MaxChangePercent
                        && item.VolumeRatio >= MinVolumeRatio))
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
        if (context.WeeklyBarsBySymbol is null
            || !TryGetBars(context.WeeklyBarsBySymbol, quote.Symbol, out var weeklyBars)
            || weeklyBars.Count < WeeklyPlatformLookback)
        {
            return null;
        }

        var orderedWeeks = weeklyBars
            .OrderBy(item => item.TradingTime)
            .ToArray();
        var currentWeekStart = GetWeekStart(context.TradingDate);
        var historyWeeks = orderedWeeks
            .Where(item => DateOnly.FromDateTime(item.TradingTime) < currentWeekStart)
            .TakeLast(WeeklyPlatformLookback)
            .ToArray();

        if (historyWeeks.Length < WeeklyPlatformLookback)
        {
            return null;
        }

        var weeklyPlatformHigh = historyWeeks.Max(item => item.High);
        var weeklyPlatformLow = historyWeeks.Min(item => item.Low);
        if (weeklyPlatformHigh <= 0 || weeklyPlatformLow <= 0)
        {
            return null;
        }

        var weeklyPlatformRangePercent = Percent(weeklyPlatformHigh, weeklyPlatformLow);
        var recentWeeks = historyWeeks.TakeLast(WeeklyRecentCompressionLookback).ToArray();
        var recentWeeklyHigh = recentWeeks.Max(item => item.High);
        var recentWeeklyLow = recentWeeks.Min(item => item.Low);
        var recentWeeklyRangePercent = recentWeeklyLow > 0 ? Percent(recentWeeklyHigh, recentWeeklyLow) : 0m;
        var resistanceTouchCount = historyWeeks.Count(item =>
            item.High >= weeklyPlatformHigh * (1m - ResistanceTouchTolerancePercent / 100m));
        var breakoutPercent = Percent(quote.Price, weeklyPlatformHigh);

        var thirtyMinuteConfirmed = false;
        var thirtyMinuteVolumeRatio = 0m;
        var thirtyMinuteClosePosition = 0m;
        var thirtyMinuteUpperShadow = 0m;
        var thirtyMinuteBreakoutPrice = 0m;
        if (context.ThirtyMinuteBarsBySymbol is not null
            && TryGetBars(context.ThirtyMinuteBarsBySymbol, quote.Symbol, out var thirtyBars))
        {
            var completedBars = thirtyBars.OrderBy(item => item.TradingTime)
                .Where(item => item.TradingTime <= context.Snapshot.SnapshotTime.LocalDateTime.AddMinutes(-30))
                .ToArray();
            if (completedBars.Length >= 6)
            {
                var confirmationBar = completedBars[^1];
                thirtyMinuteBreakoutPrice = confirmationBar.Close;
                var averageVolume = completedBars[^6..^1].Average(item => item.Volume);
                thirtyMinuteVolumeRatio = averageVolume > 0 ? confirmationBar.Volume / averageVolume : 0m;
                thirtyMinuteClosePosition = confirmationBar.High > confirmationBar.Low
                    ? (confirmationBar.Close - confirmationBar.Low) / (confirmationBar.High - confirmationBar.Low) * 100m : 0m;
                thirtyMinuteUpperShadow = confirmationBar.High > confirmationBar.Low
                    ? (confirmationBar.High - Math.Max(confirmationBar.Open, confirmationBar.Close)) / (confirmationBar.High - confirmationBar.Low) * 100m : 100m;
                thirtyMinuteConfirmed = confirmationBar.Close >= weeklyPlatformHigh * 1.003m
                    && confirmationBar.Close > confirmationBar.Open
                    && thirtyMinuteVolumeRatio >= MinThirtyMinuteVolumeRatio
                    && thirtyMinuteClosePosition >= MinThirtyMinuteClosePositionPercent
                    && thirtyMinuteUpperShadow <= MaxThirtyMinuteUpperShadowPercent;
            }
        }
        if (!isObservationRun && !thirtyMinuteConfirmed)
            return null;

        KLineBar? currentDailyBar = null;
        IReadOnlyList<KLineBar> dailyBars = [];
        if (context.DailyBarsBySymbol is not null
            && TryGetBars(context.DailyBarsBySymbol, quote.Symbol, out var foundDailyBars))
        {
            dailyBars = foundDailyBars.OrderBy(item => item.TradingTime).ToArray();
            currentDailyBar = TryGetCurrentBar(dailyBars, context.TradingDate);
        }

        var averageDailyVolume = dailyBars.Count >= AverageDailyVolumeLookback
            ? dailyBars
                .Where(item => DateOnly.FromDateTime(item.TradingTime) < context.TradingDate)
                .TakeLast(AverageDailyVolumeLookback)
                .DefaultIfEmpty()
                .Average(item => item?.Volume ?? 0m)
            : 0m;
        var currentVolumeRatioByDaily = currentDailyBar is not null && averageDailyVolume > 0
            ? currentDailyBar.Volume / averageDailyVolume
            : Math.Max(quote.VolumeRatio, 0m);
        var closePositionPercent = CalculateClosePositionPercent(currentDailyBar, quote.Price);
        var upperShadowPercent = CalculateUpperShadowPercent(currentDailyBar, quote.Price);

        var structuralFailedConditions = BuildStructuralFailedConditions(
            weeklyPlatformRangePercent,
            recentWeeklyRangePercent,
            resistanceTouchCount);
        if (structuralFailedConditions.Count > 0)
        {
            return null;
        }

        var failedConditions = BuildFailedConditions(
            weeklyPlatformRangePercent,
            recentWeeklyRangePercent,
            resistanceTouchCount,
            breakoutPercent,
            quote.VolumeRatio,
            currentVolumeRatioByDaily,
            closePositionPercent,
            upperShadowPercent,
            quote.ChangePercent);
        if (failedConditions.Count > 0 && !isObservationRun)
        {
            return null;
        }

        var compressionScore = Math.Max(12m - weeklyPlatformRangePercent / 4m, 0m);
        var recentCompressionScore = Math.Max(10m - recentWeeklyRangePercent / 3m, 0m);
        var touchScore = Math.Min(resistanceTouchCount, 5) * 1.8m;
        var breakoutScore = breakoutPercent < 0m ? 0m : Math.Max(10m - breakoutPercent, 0m);
        var volumeScore = Math.Min(quote.VolumeRatio * 4m, 12m) + Math.Min(currentVolumeRatioByDaily * 2m, 6m);
        var closePositionScore = Math.Max(closePositionPercent - MinClosePositionPercent, 0m) / 8m;
        var upperShadowPenalty = Math.Max(upperShadowPercent - 20m, 0m) / 2m;
        var chasePenalty = Math.Max(breakoutPercent - 5m, 0m) * 1.5m;
        var score = Math.Round(
            62m
            + compressionScore
            + recentCompressionScore
            + touchScore
            + breakoutScore
            + volumeScore
            + closePositionScore
            - upperShadowPenalty
            - chasePenalty,
            2);
        var isRealtimeConfirmed = failedConditions.Count == 0;
        var confidence = !isRealtimeConfirmed
            ? StrategySignalConfidence.Low
            : quote.VolumeRatio >= 1.8m
            && currentVolumeRatioByDaily >= 1.2m
            && weeklyPlatformRangePercent <= 28m
            && recentWeeklyRangePercent <= 20m
            && upperShadowPercent <= 20m
            ? StrategySignalConfidence.High
            : StrategySignalConfidence.Medium;
        var action = isRealtimeConfirmed && thirtyMinuteConfirmed ? StrategySignalAction.Candidate : StrategySignalAction.Watch;

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            Score: Math.Min(isObservationRun && !thirtyMinuteConfirmed ? score : score, isObservationRun && !thirtyMinuteConfirmed ? 75m : 100m),
            Price: quote.Price,
            Reason: $"突破近 {WeeklyPlatformLookback} 周平台上沿 {weeklyPlatformHigh:F2}，周线平台振幅 {weeklyPlatformRangePercent:F1}%，近 {WeeklyRecentCompressionLookback} 周振幅 {recentWeeklyRangePercent:F1}%，压力位触碰 {resistanceTouchCount} 次，突破距离 {breakoutPercent:F1}%，量比 {quote.VolumeRatio:F2}。",
            Risk: BuildRisk(quote.ChangePercent, breakoutPercent, weeklyPlatformRangePercent, recentWeeklyRangePercent, upperShadowPercent),
            Action: action,
            Confidence: confidence,
            Stage: StrategyStage.TriggerConfirmation,
            Metrics: new Dictionary<string, decimal>
            {
                ["change_percent"] = quote.ChangePercent,
                ["volume_ratio"] = quote.VolumeRatio,
                ["daily_volume_ratio"] = currentVolumeRatioByDaily,
                ["weekly_platform_high"] = weeklyPlatformHigh,
                ["weekly_platform_low"] = weeklyPlatformLow,
                ["weekly_platform_range_percent"] = weeklyPlatformRangePercent,
                ["weekly_recent_range_percent"] = recentWeeklyRangePercent,
                ["weekly_resistance_touch_count"] = resistanceTouchCount,
                ["breakout_percent"] = breakoutPercent,
                ["close_position_percent"] = closePositionPercent,
                ["upper_shadow_percent"] = upperShadowPercent,
                ["average_daily_volume_20"] = averageDailyVolume
                , ["thirty_minute_confirmed"] = thirtyMinuteConfirmed ? 1m : 0m
                , ["thirty_minute_breakout_price"] = thirtyMinuteBreakoutPrice
                , ["thirty_minute_volume_ratio"] = thirtyMinuteVolumeRatio
                , ["thirty_minute_close_position_percent"] = thirtyMinuteClosePosition
                , ["thirty_minute_upper_shadow_percent"] = thirtyMinuteUpperShadow
            },
            Tags: ["放量", "周线平台突破", "结构验证", confidence == StrategySignalConfidence.High ? "高质量突破" : "候选突破"],
            PassedConditions:
            [
                $"周线平台振幅 {weeklyPlatformRangePercent:F1}% <= {MaxWeeklyPlatformRangePercent:F0}%",
                $"近 {WeeklyRecentCompressionLookback} 周振幅 {recentWeeklyRangePercent:F1}% <= {MaxWeeklyRecentRangePercent:F0}%",
                $"压力位触碰 {resistanceTouchCount} 次 >= {MinResistanceTouches} 次",
                $"突破距离 {breakoutPercent:F1}% 位于 {MinBreakoutPercent:F1}% 至 {MaxBreakoutDistancePercent:F0}%",
                $"量比 {quote.VolumeRatio:F2} >= {MinVolumeRatio:F1}",
                $"收盘/当前价位置 {closePositionPercent:F0}% >= {MinClosePositionPercent:F0}%",
                $"上影线 {upperShadowPercent:F0}% <= {MaxUpperShadowPercent:F0}%"
            ],
            FailedConditions: isRealtimeConfirmed ? [] : failedConditions,
            StopLossPrice: Math.Round(Math.Max(weeklyPlatformHigh * 0.97m, weeklyPlatformLow), 2),
            TakeProfitPrice: Math.Round(quote.Price * 1.08m, 2));
    }

    private static bool TryGetBars(
        IReadOnlyDictionary<string, IReadOnlyList<KLineBar>> barsBySymbol,
        string symbol,
        out IReadOnlyList<KLineBar> bars)
    {
        if (barsBySymbol.TryGetValue(symbol, out bars!))
        {
            return true;
        }

        return barsBySymbol.TryGetValue(StockSymbolNormalizer.NormalizeCode(symbol), out bars!);
    }

    private static DateOnly GetWeekStart(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    private static List<string> BuildStructuralFailedConditions(
        decimal weeklyPlatformRangePercent,
        decimal recentWeeklyRangePercent,
        int resistanceTouchCount)
    {
        var failed = new List<string>();
        if (weeklyPlatformRangePercent > MaxWeeklyPlatformRangePercent)
        {
            failed.Add($"周线平台振幅 {weeklyPlatformRangePercent:F1}% > {MaxWeeklyPlatformRangePercent:F0}%");
        }

        if (recentWeeklyRangePercent > MaxWeeklyRecentRangePercent)
        {
            failed.Add($"近 {WeeklyRecentCompressionLookback} 周振幅 {recentWeeklyRangePercent:F1}% > {MaxWeeklyRecentRangePercent:F0}%");
        }

        if (resistanceTouchCount < MinResistanceTouches)
        {
            failed.Add($"压力位触碰 {resistanceTouchCount} 次 < {MinResistanceTouches} 次");
        }

        return failed;
    }

    private static List<string> BuildFailedConditions(
        decimal weeklyPlatformRangePercent,
        decimal recentWeeklyRangePercent,
        int resistanceTouchCount,
        decimal breakoutPercent,
        decimal volumeRatio,
        decimal currentVolumeRatioByDaily,
        decimal closePositionPercent,
        decimal upperShadowPercent,
        decimal changePercent)
    {
        var failed = new List<string>();
        if (weeklyPlatformRangePercent > MaxWeeklyPlatformRangePercent)
        {
            failed.Add($"周线平台振幅 {weeklyPlatformRangePercent:F1}% > {MaxWeeklyPlatformRangePercent:F0}%");
        }

        if (recentWeeklyRangePercent > MaxWeeklyRecentRangePercent)
        {
            failed.Add($"近 {WeeklyRecentCompressionLookback} 周振幅 {recentWeeklyRangePercent:F1}% > {MaxWeeklyRecentRangePercent:F0}%");
        }

        if (resistanceTouchCount < MinResistanceTouches)
        {
            failed.Add($"压力位触碰 {resistanceTouchCount} 次 < {MinResistanceTouches} 次");
        }

        if (breakoutPercent < MinBreakoutPercent || breakoutPercent > MaxBreakoutDistancePercent)
        {
            failed.Add($"突破距离 {breakoutPercent:F1}% 不在合理区间");
        }

        if (volumeRatio < MinVolumeRatio || currentVolumeRatioByDaily < 1.1m)
        {
            failed.Add($"量能不足，实时量比 {volumeRatio:F2}，日线量比 {currentVolumeRatioByDaily:F2}");
        }

        if (closePositionPercent < MinClosePositionPercent)
        {
            failed.Add($"收盘/当前价位置 {closePositionPercent:F0}% < {MinClosePositionPercent:F0}%");
        }

        if (upperShadowPercent > MaxUpperShadowPercent)
        {
            failed.Add($"上影线 {upperShadowPercent:F0}% > {MaxUpperShadowPercent:F0}%");
        }

        if (changePercent > MaxChangePercent)
        {
            failed.Add($"当日涨幅 {changePercent:F1}% > {MaxChangePercent:F1}%");
        }

        return failed;
    }

    private static KLineBar? TryGetCurrentBar(IReadOnlyList<KLineBar> bars, DateOnly tradingDate)
    {
        return bars.LastOrDefault(item => DateOnly.FromDateTime(item.TradingTime) == tradingDate);
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

    private static decimal Percent(decimal value, decimal baseline)
    {
        return baseline == 0m ? 0m : (value - baseline) * 100m / baseline;
    }

    private static string? BuildRisk(
        decimal changePercent,
        decimal breakoutPercent,
        decimal weeklyPlatformRangePercent,
        decimal recentWeeklyRangePercent,
        decimal upperShadowPercent)
    {
        var risks = new List<string>();
        if (changePercent > 5m)
        {
            risks.Add("涨幅偏高，注意冲高回落");
        }

        if (breakoutPercent > 5.5m)
        {
            risks.Add("距离周线平台上沿偏远，追高风险增加");
        }

        if (weeklyPlatformRangePercent > 30m || recentWeeklyRangePercent > 22m)
        {
            risks.Add("周线平台仍偏宽，突破后可能反复确认");
        }

        if (upperShadowPercent > 20m)
        {
            risks.Add("存在上影线，需观察是否能守住周线平台上沿");
        }

        return risks.Count == 0 ? null : string.Join("；", risks);
    }
}
