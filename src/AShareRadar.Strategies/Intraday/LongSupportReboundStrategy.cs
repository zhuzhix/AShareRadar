using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class LongSupportReboundStrategy : ISignalStrategy
{
    private const int RequiredDailyBarCount = 120;
    private const int PreferredDailyBarCount = 180;
    private const int RequiredMinuteBarCount = 120;
    private const int DailyWaveLookback = 140;
    private const int IntradayLookback = 80;
    private const decimal MinWatchAmount = 30_000_000m;
    private const decimal MinCandidateAmount = 50_000_000m;
    private const decimal MinWaveDrawdownPercent = 22m;
    private const decimal MinReboundFromFinalLowPercent = 7m;
    private const decimal HotEnvironmentScore = 65m;
    private const decimal WarmEnvironmentScore = 55m;
    private const decimal WeakEnvironmentScore = 45m;

    public string Code => "long-support-rebound";

    public string Name => "下跌浪二次探底反弹";

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
            MinDailyBarCount: PreferredDailyBarCount,
            RequiresThirtyMinuteKLine: true),
        new Dictionary<string, string>
        {
            ["daily_wave_lookback"] = DailyWaveLookback.ToString(),
            ["min_wave_drawdown_percent"] = MinWaveDrawdownPercent.ToString("F0"),
            ["min_rebound_from_final_low_percent"] = MinReboundFromFinalLowPercent.ToString("F0"),
            ["intraday_period"] = "30m",
            ["intraday_lookback"] = IntradayLookback.ToString()
        },
        "识别日线下跌浪末端的二次探底反弹结构，并用30分钟K线提示买点。");

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
            || bars.Count < RequiredDailyBarCount)
        {
            return null;
        }

        var dailyBars = BuildDailySeries(bars, quote, context.TradingDate);
        if (dailyBars.Length < RequiredDailyBarCount)
        {
            return null;
        }

        var daily = AnalyzeDailyWave(dailyBars, quote.Price);
        if (daily is null)
        {
            return null;
        }

        var amountRecoveryRatio = CalculateAmountRecoveryRatio(dailyBars, quote.Amount);
        var dailyVolumeRatio = CalculateDailyVolumeRatio(dailyBars);
        var effectiveVolumeRatio = quote.VolumeRatio > 0 ? quote.VolumeRatio : dailyVolumeRatio;
        var environment = BuildEnvironmentSnapshot(quote.Symbol, context, amountRecoveryRatio, effectiveVolumeRatio);
        var intraday = AnalyzeIntradayTrigger(context, quote.Symbol, quote.Price);

        var action = ResolveAction(daily, intraday, quote, environment);
        var confidence = ResolveConfidence(action, daily, intraday, environment);
        var score = CalculateScore(daily, intraday, environment, action);
        var stopLoss = intraday?.TriggerLow > 0
            ? Math.Min(intraday.TriggerLow, daily.SecondBottomLow) * 0.98m
            : daily.SecondBottomLow * 0.97m;

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            Score: score,
            Price: quote.Price,
            Reason: BuildReason(daily, intraday, environment),
            Risk: BuildRisk(daily, intraday, environment),
            Action: action,
            Confidence: confidence,
            Stage: intraday?.IsBuyPoint == true ? StrategyStage.TriggerConfirmation : StrategyStage.PatternValidation,
            Metrics: BuildMetrics(quote, daily, intraday, environment, amountRecoveryRatio, dailyVolumeRatio, effectiveVolumeRatio),
            Tags: BuildTags(action, intraday),
            PassedConditions: BuildPassedConditions(daily, intraday, environment),
            FailedConditions: BuildFailedConditions(action, daily, intraday, environment),
            StopLossPrice: Math.Round(stopLoss, 2),
            TakeProfitPrice: Math.Round(quote.Price * 1.10m, 2));
    }

    private static KLineBar[] BuildDailySeries(IReadOnlyList<KLineBar> bars, StockQuote quote, DateOnly tradingDate)
    {
        var ordered = bars
            .OrderBy(item => item.TradingTime)
            .TakeLast(PreferredDailyBarCount)
            .ToList();
        var currentIndex = ordered.FindIndex(item => DateOnly.FromDateTime(item.TradingTime) == tradingDate);
        if (currentIndex >= 0)
        {
            var current = ordered[currentIndex];
            ordered[currentIndex] = current with
            {
                High = Math.Max(current.High, quote.Price),
                Low = current.Low > 0 ? Math.Min(current.Low, quote.Price) : quote.Price,
                Close = quote.Price,
                Volume = current.Volume > 0 ? current.Volume : quote.Volume,
                Amount = current.Amount > 0 ? current.Amount : quote.Amount
            };
        }
        else
        {
            ordered.Add(new KLineBar(
                tradingDate.ToDateTime(TimeOnly.MinValue),
                quote.Open > 0 ? quote.Open : quote.Price,
                quote.High > 0 ? Math.Max(quote.High, quote.Price) : quote.Price,
                quote.Low > 0 ? Math.Min(quote.Low, quote.Price) : quote.Price,
                quote.Price,
                quote.Volume,
                quote.Amount,
                quote.TurnoverRate > 0 ? quote.TurnoverRate : null));
        }

        return ordered.TakeLast(PreferredDailyBarCount).ToArray();
    }

    private static DailyWaveContext? AnalyzeDailyWave(IReadOnlyList<KLineBar> bars, decimal currentPrice)
    {
        var recent = bars.TakeLast(Math.Min(DailyWaveLookback, bars.Count)).ToArray();
        if (recent.Length < RequiredDailyBarCount)
        {
            return null;
        }

        var finalLowIndex = IndexOfMin(recent, item => item.Low);
        var majorHighIndex = IndexOfMax(recent.Take(finalLowIndex + 1).ToArray(), item => item.High);
        var majorHigh = recent[majorHighIndex].High;
        var finalLow = recent[finalLowIndex].Low;
        if (majorHigh <= 0 || finalLow <= 0 || finalLowIndex < recent.Length * 0.35)
        {
            return null;
        }

        var drawdownPercent = (majorHigh - finalLow) / majorHigh * 100m;
        if (drawdownPercent < MinWaveDrawdownPercent)
        {
            return null;
        }

        var swingHighs = FindSwingPoints(recent, high: true, 3);
        var swingLows = FindSwingPoints(recent, high: false, 3);
        var trendLine = ResolveDescendingTrendLine(swingHighs, recent.Length - 1);
        var trendLinePrice = trendLine?.PriceAt(recent.Length - 1) ?? recent.TakeLast(35).Max(item => item.High);
        var trendBreakPercent = trendLinePrice > 0 ? (currentPrice - trendLinePrice) / trendLinePrice * 100m : 0m;
        var hasTrendBreak = trendBreakPercent >= 0.8m;

        var finalLowSwing = new SwingPoint(finalLowIndex, finalLow);
        var previousLow = swingLows
            .Where(item => item.Index < finalLowIndex - 8)
            .OrderByDescending(item => item.Index)
            .FirstOrDefault();
        var terminalLowChangePercent = previousLow.Price > 0
            ? (finalLowSwing.Price - previousLow.Price) / previousLow.Price * 100m
            : 0m;

        var afterLow = recent.Skip(finalLowIndex + 1).ToArray();
        if (afterLow.Length < 8)
        {
            return null;
        }

        var reboundHighRelativeIndex = IndexOfMax(afterLow, item => item.High);
        var reboundHighIndex = finalLowIndex + 1 + reboundHighRelativeIndex;
        var reboundHigh = recent[reboundHighIndex].High;
        var reboundFromFinalLowPercent = (Math.Max(reboundHigh, currentPrice) - finalLow) / finalLow * 100m;
        if (reboundFromFinalLowPercent < MinReboundFromFinalLowPercent)
        {
            return null;
        }

        var pullbackStart = Math.Min(reboundHighIndex + 1, recent.Length - 1);
        var pullbackRange = recent.Skip(pullbackStart).ToArray();
        var secondBottomIndex = pullbackRange.Length >= 3
            ? pullbackStart + IndexOfMin(pullbackRange, item => item.Low)
            : finalLowIndex;
        var secondBottomLow = recent[secondBottomIndex].Low;
        var hasSecondBottom = secondBottomIndex > reboundHighIndex
            && secondBottomLow >= finalLow * 0.98m
            && secondBottomLow <= reboundHigh * 0.97m
            && currentPrice > secondBottomLow * 1.025m;
        var secondBottomHoldPercent = finalLow > 0 ? (secondBottomLow - finalLow) / finalLow * 100m : 0m;
        var repairFromSecondBottomPercent = secondBottomLow > 0
            ? (currentPrice - secondBottomLow) / secondBottomLow * 100m
            : 0m;

        var ma5 = AverageClose(recent, 5);
        var ma10 = AverageClose(recent, 10);
        var ma20 = AverageClose(recent, 20);
        var ma5Previous = AverageClose(recent.Take(recent.Length - 3).ToArray(), 5);
        var ma5SlopePercent = ma5Previous > 0 ? (ma5 - ma5Previous) / ma5Previous * 100m : 0m;
        var currentAboveMa10Percent = ma10 > 0 ? (currentPrice - ma10) / ma10 * 100m : 0m;

        var descendingHighScore = trendLine is not null ? 10m : 4m;
        var drawdownScore = Math.Clamp((drawdownPercent - MinWaveDrawdownPercent) * 0.7m, 0m, 13m);
        var terminalScore = terminalLowChangePercent >= -6m ? 8m : 2m;
        var reboundScore = Math.Clamp(reboundFromFinalLowPercent * 0.6m, 0m, 10m);
        var maScore = ma5SlopePercent > 0 ? 6m : 0m;
        var dailyWaveScore = Math.Clamp(8m + descendingHighScore + drawdownScore + terminalScore + reboundScore + maScore, 0m, 45m);
        var trendBreakScore = hasTrendBreak ? Math.Clamp(8m + trendBreakPercent * 1.2m, 0m, 15m) : 0m;
        var secondBottomScore = hasSecondBottom
            ? Math.Clamp(8m + repairFromSecondBottomPercent, 0m, 15m)
            : Math.Clamp(Math.Max(repairFromSecondBottomPercent, 0m) * 0.8m, 0m, 7m);
        var dailyCandidate = hasTrendBreak && hasSecondBottom && currentAboveMa10Percent >= -1m && ma5SlopePercent >= -0.5m;

        return new DailyWaveContext(
            drawdownPercent,
            terminalLowChangePercent,
            reboundFromFinalLowPercent,
            secondBottomHoldPercent,
            repairFromSecondBottomPercent,
            trendBreakPercent,
            currentAboveMa10Percent,
            ma5SlopePercent,
            dailyWaveScore,
            trendBreakScore,
            secondBottomScore,
            dailyCandidate,
            hasTrendBreak,
            hasSecondBottom,
            majorHigh,
            finalLow,
            reboundHigh,
            secondBottomLow,
            trendLinePrice);
    }

    private static IntradayTriggerContext? AnalyzeIntradayTrigger(StrategyContext context, string symbol, decimal currentPrice)
    {
        if (context.ThirtyMinuteBarsBySymbol is null
            || !context.ThirtyMinuteBarsBySymbol.TryGetValue(symbol, out var directThirtyMinuteBars)
            || directThirtyMinuteBars.Count < 36)
        {
            return null;
        }

        var bars30 = directThirtyMinuteBars
            .OrderBy(item => item.TradingTime)
            .TakeLast(IntradayLookback)
            .ToArray();
        if (bars30.Length < 36)
        {
            return null;
        }

        var modeA = TryBuildIntradaySecondBottom(bars30, currentPrice);
        var modeB = TryBuildMaPullbackTrigger(bars30, currentPrice);
        var modeC = TryBuildIntradayTrendBreak(bars30, currentPrice);
        return new[] { modeA, modeB, modeC }
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
    }

    private static IntradayTriggerContext? TryBuildIntradaySecondBottom(IReadOnlyList<KLineBar> bars, decimal currentPrice)
    {
        var lows = FindSwingPoints(bars, high: false, 2);
        var latestLow = lows.Where(item => item.Index >= bars.Count - 18).OrderByDescending(item => item.Index).FirstOrDefault();
        if (latestLow.Price <= 0)
        {
            return null;
        }

        var previousLow = lows.Where(item => item.Index < latestLow.Index - 5).OrderByDescending(item => item.Index).FirstOrDefault();
        if (previousLow.Price <= 0)
        {
            return null;
        }

        var highBetween = bars.Skip(previousLow.Index).Take(latestLow.Index - previousLow.Index + 1).Max(item => item.High);
        var firstReboundPercent = (highBetween - previousLow.Price) / previousLow.Price * 100m;
        var lowHoldPercent = (latestLow.Price - previousLow.Price) / previousLow.Price * 100m;
        var repairPercent = (currentPrice - latestLow.Price) / latestLow.Price * 100m;
        var volumeRatio = LastVolumeRatio(bars, 20);
        var ma10 = AverageClose(bars, 10);
        var ma20 = AverageClose(bars, 20);
        var closePosition = ClosePositionPercent(bars[^1], currentPrice);
        var pass = firstReboundPercent >= 2.5m
            && lowHoldPercent >= -1.5m
            && repairPercent >= 1.2m
            && currentPrice >= Math.Min(ma10, ma20)
            && volumeRatio >= 1.05m;
        if (!pass)
        {
            return null;
        }

        var score = Math.Clamp(7m + repairPercent * 1.2m + Math.Min(volumeRatio * 2m, 4m) + closePosition / 25m, 0m, 15m);
        return new IntradayTriggerContext("30分钟二次探底", score, true, latestLow.Price, repairPercent, volumeRatio, closePosition, currentPrice >= ma10, currentPrice >= ma20);
    }

    private static IntradayTriggerContext? TryBuildMaPullbackTrigger(IReadOnlyList<KLineBar> bars, decimal currentPrice)
    {
        if (bars.Count < 24)
        {
            return null;
        }

        var ma5 = AverageClose(bars, 5);
        var ma10 = AverageClose(bars, 10);
        var ma20 = AverageClose(bars, 20);
        var previousMa20 = AverageClose(bars.Take(bars.Count - 5).ToArray(), 20);
        var recentLow = bars.TakeLast(8).Min(item => item.Low);
        if (ma20 <= 0 || previousMa20 <= 0)
        {
            return null;
        }

        var pullbackDistancePercent = Math.Abs(recentLow - ma20) / ma20 * 100m;
        var repairPercent = (currentPrice - recentLow) / recentLow * 100m;
        var closePosition = ClosePositionPercent(bars[^1], currentPrice);
        var volumeRatio = LastVolumeRatio(bars, 20);
        var pass = ma20 >= previousMa20
            && pullbackDistancePercent <= 1.8m
            && currentPrice >= Math.Min(ma5, ma10)
            && closePosition >= 58m
            && repairPercent >= 0.8m;
        if (!pass)
        {
            return null;
        }

        var score = Math.Clamp(6m + repairPercent * 1.5m + Math.Min(volumeRatio * 2m, 4m) + closePosition / 28m, 0m, 15m);
        return new IntradayTriggerContext("30分钟回踩均线再上攻", score, true, recentLow, repairPercent, volumeRatio, closePosition, currentPrice >= ma10, currentPrice >= ma20);
    }

    private static IntradayTriggerContext? TryBuildIntradayTrendBreak(IReadOnlyList<KLineBar> bars, decimal currentPrice)
    {
        var recent = bars.TakeLast(Math.Min(40, bars.Count)).ToArray();
        var highs = FindSwingPoints(recent, high: true, 2);
        var line = ResolveDescendingTrendLine(highs, recent.Length - 1);
        if (line is null)
        {
            return null;
        }

        var linePrice = line.Value.PriceAt(recent.Length - 1);
        var recentLow = recent.TakeLast(20).Min(item => item.Low);
        var distanceFromLow = recentLow > 0 ? (currentPrice - recentLow) / recentLow * 100m : 999m;
        var volumeRatio = LastVolumeRatio(bars, 20);
        var closePosition = ClosePositionPercent(bars[^1], currentPrice);
        var pass = currentPrice > linePrice * 1.005m
            && volumeRatio >= 1.05m
            && distanceFromLow <= 6m;
        if (!pass)
        {
            return null;
        }

        var repairPercent = recentLow > 0 ? (currentPrice - recentLow) / recentLow * 100m : 0m;
        var score = Math.Clamp(7m + Math.Min((currentPrice - linePrice) / linePrice * 100m * 1.8m, 4m) + Math.Min(volumeRatio * 2m, 4m), 0m, 15m);
        return new IntradayTriggerContext("30分钟下降趋势线突破", score, true, recentLow, repairPercent, volumeRatio, closePosition, currentPrice >= AverageClose(bars, 10), currentPrice >= AverageClose(bars, 20));
    }

    private static StrategySignalAction ResolveAction(
        DailyWaveContext daily,
        IntradayTriggerContext? intraday,
        StockQuote quote,
        EnvironmentSnapshot environment)
    {
        if (!daily.IsCandidate || quote.Amount < MinCandidateAmount || environment.IsVeryWeak)
        {
            return StrategySignalAction.Watch;
        }

        if (intraday?.IsBuyPoint == true && environment.HasVolumeRecovery && !environment.IsWeak)
        {
            return StrategySignalAction.Confirm;
        }

        return StrategySignalAction.Candidate;
    }

    private static StrategySignalConfidence ResolveConfidence(
        StrategySignalAction action,
        DailyWaveContext daily,
        IntradayTriggerContext? intraday,
        EnvironmentSnapshot environment)
    {
        if (action == StrategySignalAction.Confirm
            && daily.TrendBreakPercent >= 1.5m
            && daily.RepairFromSecondBottomPercent >= 3m
            && intraday is { Score: >= 11m }
            && environment.HasPositiveBreadth)
        {
            return StrategySignalConfidence.High;
        }

        return action == StrategySignalAction.Watch
            ? StrategySignalConfidence.Low
            : StrategySignalConfidence.Medium;
    }

    private static decimal CalculateScore(
        DailyWaveContext daily,
        IntradayTriggerContext? intraday,
        EnvironmentSnapshot environment,
        StrategySignalAction action)
    {
        var environmentScore = Math.Clamp(environment.EnvironmentScore, -10m, 10m);
        var intradayScore = intraday?.Score ?? 0m;
        var score = daily.DailyWaveScore
            + daily.TrendBreakScore
            + daily.SecondBottomScore
            + intradayScore
            + environmentScore;

        if (action != StrategySignalAction.Confirm)
        {
            score = Math.Min(score, 80m);
        }

        return Math.Round(Math.Clamp(score, 0m, 100m), 2);
    }

    private static Dictionary<string, decimal> BuildMetrics(
        StockQuote quote,
        DailyWaveContext daily,
        IntradayTriggerContext? intraday,
        EnvironmentSnapshot environment,
        decimal amountRecoveryRatio,
        decimal dailyVolumeRatio,
        decimal effectiveVolumeRatio)
    {
        var metrics = new Dictionary<string, decimal>
        {
            ["wave_drawdown_percent"] = daily.DrawdownPercent,
            ["terminal_low_change_percent"] = daily.TerminalLowChangePercent,
            ["rebound_from_final_low_percent"] = daily.ReboundFromFinalLowPercent,
            ["second_bottom_hold_percent"] = daily.SecondBottomHoldPercent,
            ["repair_from_second_bottom_percent"] = daily.RepairFromSecondBottomPercent,
            ["daily_trendline_break_percent"] = daily.TrendBreakPercent,
            ["current_above_ma10_percent"] = daily.CurrentAboveMa10Percent,
            ["ma5_slope_percent"] = daily.Ma5SlopePercent,
            ["daily_wave_score"] = daily.DailyWaveScore,
            ["trend_break_score"] = daily.TrendBreakScore,
            ["second_bottom_score"] = daily.SecondBottomScore,
            ["major_wave_high"] = daily.MajorHigh,
            ["final_wave_low"] = daily.FinalLow,
            ["rebound_high"] = daily.ReboundHigh,
            ["second_bottom_low"] = daily.SecondBottomLow,
            ["daily_trendline_price"] = daily.TrendLinePrice,
            ["intraday_buy_point_score"] = intraday?.Score ?? 0m,
            ["intraday_repair_percent"] = intraday?.RepairFromTriggerLowPercent ?? 0m,
            ["intraday_volume_ratio"] = intraday?.VolumeRatio ?? 0m,
            ["intraday_close_position_percent"] = intraday?.ClosePositionPercent ?? 0m,
            ["intraday_above_ma10"] = intraday?.AboveMa10 == true ? 1m : 0m,
            ["intraday_above_ma20"] = intraday?.AboveMa20 == true ? 1m : 0m,
            ["amount_recovery_ratio"] = amountRecoveryRatio,
            ["daily_volume_ratio"] = dailyVolumeRatio,
            ["volume_ratio"] = effectiveVolumeRatio,
            ["sector_heat_score"] = environment.SectorHeatScore,
            ["concept_heat_score"] = environment.ConceptHeatScore,
            ["max_heat_score"] = environment.MaxHeatScore,
            ["sector_rising_ratio"] = environment.SectorRisingRatio,
            ["concept_rising_ratio"] = environment.ConceptRisingRatio,
            ["sentiment_temperature"] = environment.SentimentTemperature,
            ["sentiment_breadth_score"] = environment.SentimentBreadthScore,
            ["market_rising_ratio"] = environment.MarketRisingRatio,
            ["market_falling_ratio"] = environment.MarketFallingRatio,
            ["environment_score"] = environment.EnvironmentScore,
            ["amount"] = quote.Amount
        };

        return metrics;
    }

    private static string BuildReason(DailyWaveContext daily, IntradayTriggerContext? intraday, EnvironmentSnapshot environment)
    {
        var intradayText = intraday is null
            ? "30分钟暂未出现买点"
            : $"{intraday.Mode}，自触发低点修复 {intraday.RepairFromTriggerLowPercent:F1}%";
        return $"日线下跌浪回撤 {daily.DrawdownPercent:F1}%，末端低点变化 {daily.TerminalLowChangePercent:F1}%，反弹 {daily.ReboundFromFinalLowPercent:F1}%，趋势线突破 {daily.TrendBreakPercent:F1}%；{intradayText}；环境分 {environment.EnvironmentScore:F1}。";
    }

    private static string? BuildRisk(DailyWaveContext daily, IntradayTriggerContext? intraday, EnvironmentSnapshot environment)
    {
        var risks = new List<string>();
        if (!daily.HasTrendBreak)
        {
            risks.Add("日线下降趋势线尚未有效突破");
        }

        if (!daily.HasSecondBottom)
        {
            risks.Add("日线二次回踩结构仍未确认");
        }

        if (intraday is null)
        {
            risks.Add("缺少30分钟买点确认");
        }
        else if (intraday.VolumeRatio < 1.05m)
        {
            risks.Add("30分钟量能确认偏弱");
        }

        if (environment.IsWeak)
        {
            risks.Add("板块热度或市场赚钱效应偏弱");
        }

        return risks.Count == 0 ? null : string.Join("；", risks);
    }

    private static IReadOnlyList<string> BuildTags(StrategySignalAction action, IntradayTriggerContext? intraday)
    {
        var actionTag = action == StrategySignalAction.Confirm
            ? "买点"
            : action == StrategySignalAction.Candidate
                ? "候选"
                : "观察";
        return intraday is null
            ? ["下跌浪", "二次探底", actionTag]
            : ["下跌浪", "二次探底", "30分钟", actionTag];
    }

    private static IReadOnlyList<string> BuildPassedConditions(DailyWaveContext daily, IntradayTriggerContext? intraday, EnvironmentSnapshot environment)
    {
        var result = new List<string>
        {
            $"日线下跌浪回撤 {daily.DrawdownPercent:F1}% >= {MinWaveDrawdownPercent:F0}%",
            $"末端低点变化 {daily.TerminalLowChangePercent:F1}%，反弹 {daily.ReboundFromFinalLowPercent:F1}%",
            $"下降趋势线突破 {daily.TrendBreakPercent:F1}%",
            $"二次回踩守住前低 {daily.SecondBottomHoldPercent:F1}%",
            $"环境分 {environment.EnvironmentScore:F1}，热度 {environment.MaxHeatScore:F1}，赚钱效应 {environment.SentimentBreadthScore:F1}"
        };
        if (intraday is not null)
        {
            result.Add($"{intraday.Mode}，量能 {intraday.VolumeRatio:F2}x，收盘位置 {intraday.ClosePositionPercent:F1}%");
        }

        return result;
    }

    private static IReadOnlyList<string> BuildFailedConditions(
        StrategySignalAction action,
        DailyWaveContext daily,
        IntradayTriggerContext? intraday,
        EnvironmentSnapshot environment)
    {
        var failed = new List<string>();
        if (action == StrategySignalAction.Watch)
        {
            if (!daily.HasTrendBreak)
            {
                failed.Add("等待日线下降趋势线突破");
            }

            if (!daily.HasSecondBottom)
            {
                failed.Add("等待日线二次探底回踩确认");
            }

            if (environment.IsVeryWeak)
            {
                failed.Add("市场赚钱效应极弱，禁止确认");
            }
        }

        if (action != StrategySignalAction.Confirm && intraday is null)
        {
            failed.Add("缺少30分钟买点确认");
        }

        return failed;
    }

    private static EnvironmentSnapshot BuildEnvironmentSnapshot(
        string symbol,
        StrategyContext context,
        decimal amountRecoveryRatio,
        decimal volumeRatio)
    {
        var sectorHeat = ResolveSectorHeat(symbol, context.SectorHeatSnapshot);
        var conceptHeat = ResolveConceptHeat(symbol, context.ConceptHeatSnapshot);
        var maxHeatScore = Math.Max(sectorHeat?.HeatScore ?? 0m, conceptHeat?.HeatScore ?? 0m);
        var maxRisingRatio = Math.Max(sectorHeat?.RisingRatioPercent ?? 0m, conceptHeat?.RisingRatioPercent ?? 0m);
        var sentimentBreadthScore = ResolveSentimentBreadthScore(context);
        var sentimentTemperature = context.MarketSentiment?.TemperatureScore ?? 50m;
        var marketRisingRatio = context.MarketStats?.RisingRatioPercent ?? 50m;
        var marketFallingRatio = context.MarketStats?.FallingRatioPercent ?? 50m;
        var hasHeatData = sectorHeat is not null || conceptHeat is not null;
        var hasSentimentData = context.MarketSentiment is not null || context.MarketStats is not null;

        var heatScore = !hasHeatData
            ? 0m
            : maxHeatScore >= HotEnvironmentScore && maxRisingRatio >= 55m
                ? 8m
                : maxHeatScore >= WarmEnvironmentScore
                    ? 4m
                    : maxHeatScore < WeakEnvironmentScore
                        ? -5m
                        : 0m;
        var volumeScore = amountRecoveryRatio >= 1.15m || volumeRatio >= 1.15m
            ? 5m
            : amountRecoveryRatio >= 1m || volumeRatio >= 1m
                ? 3m
                : amountRecoveryRatio > 0m && amountRecoveryRatio < 0.75m && volumeRatio < 0.8m
                    ? -4m
                    : 0m;
        var sentimentScore = !hasSentimentData
            ? 0m
            : sentimentBreadthScore >= 60m || marketRisingRatio >= 45m
                ? 6m
                : sentimentBreadthScore < 35m || marketFallingRatio > 70m
                    ? -10m
                    : sentimentBreadthScore < 45m || marketRisingRatio < 35m
                        ? -6m
                        : 0m;

        return new EnvironmentSnapshot(
            sectorHeat?.HeatScore ?? 0m,
            conceptHeat?.HeatScore ?? 0m,
            maxHeatScore,
            sectorHeat?.RisingRatioPercent ?? 0m,
            conceptHeat?.RisingRatioPercent ?? 0m,
            sentimentTemperature,
            sentimentBreadthScore,
            marketRisingRatio,
            marketFallingRatio,
            amountRecoveryRatio,
            volumeRatio,
            Math.Clamp(heatScore + volumeScore + sentimentScore, -10m, 10m),
            hasHeatData,
            hasSentimentData);
    }

    private static SectorHeat? ResolveSectorHeat(string symbol, SectorHeatSnapshot? snapshot)
    {
        return snapshot is not null && snapshot.HeatBySymbol.TryGetValue(symbol, out var heat) ? heat : null;
    }

    private static ConceptHeat? ResolveConceptHeat(string symbol, ConceptHeatSnapshot? snapshot)
    {
        return snapshot is not null
            && snapshot.HeatBySymbol.TryGetValue(symbol, out var heats)
            && heats.Count > 0
            ? heats.OrderByDescending(item => item.HeatScore).First()
            : null;
    }

    private static decimal ResolveSentimentBreadthScore(StrategyContext context)
    {
        var breadth = context.MarketSentiment?.Categories
            .FirstOrDefault(item => string.Equals(item.Code, "breadth", StringComparison.OrdinalIgnoreCase));
        return breadth?.Score ?? context.MarketStats?.RisingRatioPercent ?? 50m;
    }

    private static KLineBar[] AggregateToThirtyMinuteBars(IReadOnlyList<KLineBar> minuteBars)
    {
        return minuteBars
            .Where(item => IsTradingMinute(item.TradingTime))
            .OrderBy(item => item.TradingTime)
            .GroupBy(item => GetThirtyMinuteBucketEnd(item.TradingTime))
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
            })
            .Where(item => item.Open > 0 && item.Close > 0)
            .ToArray();
    }

    private static bool IsTradingMinute(DateTime tradingTime)
    {
        var time = tradingTime.TimeOfDay;
        return time >= new TimeSpan(9, 30, 0) && time <= new TimeSpan(11, 30, 0)
            || time >= new TimeSpan(13, 0, 0) && time <= new TimeSpan(15, 0, 0);
    }

    private static DateTime GetThirtyMinuteBucketEnd(DateTime tradingTime)
    {
        var sessionStart = tradingTime.TimeOfDay < new TimeSpan(12, 0, 0)
            ? tradingTime.Date.AddHours(9).AddMinutes(30)
            : tradingTime.Date.AddHours(13);
        var elapsedMinutes = Math.Max(1d, (tradingTime - sessionStart).TotalMinutes);
        var bucketIndex = (int)Math.Ceiling(elapsedMinutes / 30d);
        return sessionStart.AddMinutes(bucketIndex * 30);
    }

    private static decimal CalculateDailyVolumeRatio(IReadOnlyList<KLineBar> bars)
    {
        if (bars.Count < 21)
        {
            return 0m;
        }

        var history = bars.Take(bars.Count - 1).TakeLast(20).ToArray();
        var averageVolume20 = history.Average(item => item.Volume);
        return averageVolume20 > 0 ? bars[^1].Volume / averageVolume20 : 0m;
    }

    private static decimal CalculateAmountRecoveryRatio(IReadOnlyList<KLineBar> bars, decimal quoteAmount)
    {
        if (bars.Count < 21)
        {
            return 0m;
        }

        var samples = bars.Take(bars.Count - 1)
            .TakeLast(20)
            .Where(item => item.Amount > 0m)
            .Select(item => item.Amount)
            .ToArray();
        if (samples.Length < 5)
        {
            return 0m;
        }

        var averageAmount20 = samples.Average();
        var currentAmount = bars[^1].Amount > 0 ? bars[^1].Amount : quoteAmount;
        return averageAmount20 > 0 && currentAmount > 0 ? currentAmount / averageAmount20 : 0m;
    }

    private static decimal AverageClose(IEnumerable<KLineBar> bars, int count)
    {
        var items = bars.TakeLast(count).ToArray();
        return items.Length < count ? 0m : items.Average(item => item.Close);
    }

    private static decimal LastVolumeRatio(IReadOnlyList<KLineBar> bars, int lookback)
    {
        if (bars.Count < lookback + 1)
        {
            return 0m;
        }

        var average = bars.Take(bars.Count - 1).TakeLast(lookback).Average(item => item.Volume);
        return average > 0 ? bars[^1].Volume / average : 0m;
    }

    private static decimal ClosePositionPercent(KLineBar bar, decimal currentPrice)
    {
        return bar.High <= bar.Low ? 100m : (currentPrice - bar.Low) / (bar.High - bar.Low) * 100m;
    }

    private static IReadOnlyList<SwingPoint> FindSwingPoints(IReadOnlyList<KLineBar> bars, bool high, int radius)
    {
        var points = new List<SwingPoint>();
        for (var i = radius; i < bars.Count - radius; i++)
        {
            var value = high ? bars[i].High : bars[i].Low;
            var isSwing = true;
            for (var j = i - radius; j <= i + radius; j++)
            {
                if (j == i)
                {
                    continue;
                }

                var other = high ? bars[j].High : bars[j].Low;
                if (high ? other > value : other < value)
                {
                    isSwing = false;
                    break;
                }
            }

            if (isSwing && value > 0)
            {
                points.Add(new SwingPoint(i, value));
            }
        }

        return points;
    }

    private static TrendLine? ResolveDescendingTrendLine(IReadOnlyList<SwingPoint> highs, int targetIndex)
    {
        TrendLine? best = null;
        foreach (var left in highs)
        {
            foreach (var right in highs.Where(item => item.Index > left.Index + 8))
            {
                if (right.Price >= left.Price * 0.985m || right.Index >= targetIndex)
                {
                    continue;
                }

                best = new TrendLine(left, right);
            }
        }

        return best;
    }

    private static int IndexOfMax(IReadOnlyList<KLineBar> bars, Func<KLineBar, decimal> selector)
    {
        var index = 0;
        var max = selector(bars[0]);
        for (var i = 1; i < bars.Count; i++)
        {
            var value = selector(bars[i]);
            if (value > max)
            {
                max = value;
                index = i;
            }
        }

        return index;
    }

    private static int IndexOfMin(IReadOnlyList<KLineBar> bars, Func<KLineBar, decimal> selector)
    {
        var index = 0;
        var min = selector(bars[0]);
        for (var i = 1; i < bars.Count; i++)
        {
            var value = selector(bars[i]);
            if (value < min)
            {
                min = value;
                index = i;
            }
        }

        return index;
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

    private sealed record DailyWaveContext(
        decimal DrawdownPercent,
        decimal TerminalLowChangePercent,
        decimal ReboundFromFinalLowPercent,
        decimal SecondBottomHoldPercent,
        decimal RepairFromSecondBottomPercent,
        decimal TrendBreakPercent,
        decimal CurrentAboveMa10Percent,
        decimal Ma5SlopePercent,
        decimal DailyWaveScore,
        decimal TrendBreakScore,
        decimal SecondBottomScore,
        bool IsCandidate,
        bool HasTrendBreak,
        bool HasSecondBottom,
        decimal MajorHigh,
        decimal FinalLow,
        decimal ReboundHigh,
        decimal SecondBottomLow,
        decimal TrendLinePrice);

    private sealed record IntradayTriggerContext(
        string Mode,
        decimal Score,
        bool IsBuyPoint,
        decimal TriggerLow,
        decimal RepairFromTriggerLowPercent,
        decimal VolumeRatio,
        decimal ClosePositionPercent,
        bool AboveMa10,
        bool AboveMa20);

    private sealed record EnvironmentSnapshot(
        decimal SectorHeatScore,
        decimal ConceptHeatScore,
        decimal MaxHeatScore,
        decimal SectorRisingRatio,
        decimal ConceptRisingRatio,
        decimal SentimentTemperature,
        decimal SentimentBreadthScore,
        decimal MarketRisingRatio,
        decimal MarketFallingRatio,
        decimal AmountRecoveryRatio,
        decimal VolumeRatio,
        decimal EnvironmentScore,
        bool HasHeatData,
        bool HasSentimentData)
    {
        public bool HasWarmHeat => !HasHeatData || MaxHeatScore >= WarmEnvironmentScore;

        public bool HasPositiveBreadth => !HasSentimentData || SentimentBreadthScore >= 55m || MarketRisingRatio >= 45m;

        public bool HasVolumeRecovery => AmountRecoveryRatio >= 1.0m || VolumeRatio >= 1.0m;

        public bool IsWeak => (HasHeatData && MaxHeatScore < WeakEnvironmentScore) ||
            (HasSentimentData && (SentimentBreadthScore < 45m || MarketRisingRatio < 35m));

        public bool IsVeryWeak => HasSentimentData && (SentimentBreadthScore < 35m || MarketFallingRatio > 70m);
    }

    private readonly record struct SwingPoint(int Index, decimal Price);

    private readonly record struct TrendLine(SwingPoint Left, SwingPoint Right)
    {
        public decimal PriceAt(int index)
        {
            var slope = (Right.Price - Left.Price) / Math.Max(1, Right.Index - Left.Index);
            return Left.Price + slope * (index - Left.Index);
        }
    }
}
