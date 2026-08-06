using System.Globalization;
using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class MainSectorResonanceStrategy : ISignalStrategy
{
    private const string CodeValue = "main-sector-resonance";
    private const string GapRecoveryCodeValue = "main-sector-gap-recovery";
    private const decimal MinAmount = 80_000_000m;
    private const decimal MinHeatScore = 50m;
    private const decimal MaxChangePercent = 6.5m;
    private const decimal CandidateHighPositionLimit = 99.2m;
    private const decimal ConfirmHighPositionLimit = 99.8m;
    private const decimal MaxDrawdownFromHigh = 1.2m;
    private const decimal CandidateVolumeAccel = 1.35m;
    private const decimal ConfirmVolumeAccel = 1.5m;
    private const decimal MinFiveMinuteReturn = 0.3m;
    private const decimal PlatformBreakoutPercent = 0.3m;
    private const decimal GapRecoveryMinOpenGapDown = -1.0m;
    private const decimal GapRecoveryMinReturnFromOpen = 0.8m;
    private const decimal GapRecoveryMaxChangePercent = 3.5m;
    private const decimal GapRecoveryMinVwapRatio = 99.5m;
    private const decimal GapRecoveryMaxDrawdownFromHigh = 1.8m;
    private const decimal GapRecoveryMinVolumeAccel = 1.2m;
    private const decimal GapRecoveryMinHeatScore = 45m;
    private const int MaxResultCount = 20;
    private const int ConceptLeaderRankThreshold = 5;
    private readonly string _code;
    private readonly string _name;
    private readonly bool _gapRecoveryOnly;

    public MainSectorResonanceStrategy()
        : this(CodeValue, "主线板块共振", gapRecoveryOnly: false)
    {
    }

    private MainSectorResonanceStrategy(string code, string name, bool gapRecoveryOnly)
    {
        _code = code;
        _name = name;
        _gapRecoveryOnly = gapRecoveryOnly;
    }

    public static MainSectorResonanceStrategy CreateGapRecovery()
    {
        return new MainSectorResonanceStrategy(GapRecoveryCodeValue, "主线低开高走", gapRecoveryOnly: true);
    }

    public string Code => _code;

    public string Name => _name;

    public StrategyType Type => StrategyType.IntradayOpportunity;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.CandidateRanking,
        StrategySignalAction.Watch,
        new StrategyDataRequirement(
            RequiresRealtimeQuote: true,
            RequiresDailyKLine: false,
            RequiresMinuteKLine: true,
            RequiresSectorData: true,
            RequiresCapitalFlow: false,
            MinDailyBarCount: 0),
        new Dictionary<string, string>
        {
            ["min_amount"] = MinAmount.ToString("F0", CultureInfo.InvariantCulture),
            ["min_heat_score"] = MinHeatScore.ToString("F1", CultureInfo.InvariantCulture),
            ["max_change_percent"] = MaxChangePercent.ToString("F1", CultureInfo.InvariantCulture),
            ["candidate_high_position_limit"] = CandidateHighPositionLimit.ToString("F1", CultureInfo.InvariantCulture),
            ["confirm_high_position_limit"] = ConfirmHighPositionLimit.ToString("F1", CultureInfo.InvariantCulture),
            ["max_drawdown_from_high"] = MaxDrawdownFromHigh.ToString("F1", CultureInfo.InvariantCulture),
            ["candidate_volume_accel"] = CandidateVolumeAccel.ToString("F2", CultureInfo.InvariantCulture),
            ["confirm_volume_accel"] = ConfirmVolumeAccel.ToString("F2", CultureInfo.InvariantCulture),
            ["min_5m_return"] = MinFiveMinuteReturn.ToString("F1", CultureInfo.InvariantCulture),
            ["platform_breakout_percent"] = PlatformBreakoutPercent.ToString("F1", CultureInfo.InvariantCulture),
            ["gap_recovery_min_open_gap_down"] = GapRecoveryMinOpenGapDown.ToString("F1", CultureInfo.InvariantCulture),
            ["gap_recovery_min_return_from_open"] = GapRecoveryMinReturnFromOpen.ToString("F1", CultureInfo.InvariantCulture),
            ["gap_recovery_max_change_percent"] = GapRecoveryMaxChangePercent.ToString("F1", CultureInfo.InvariantCulture),
            ["gap_recovery_min_vwap_ratio"] = GapRecoveryMinVwapRatio.ToString("F1", CultureInfo.InvariantCulture),
            ["gap_recovery_max_drawdown_from_high"] = GapRecoveryMaxDrawdownFromHigh.ToString("F1", CultureInfo.InvariantCulture),
            ["gap_recovery_min_volume_accel"] = GapRecoveryMinVolumeAccel.ToString("F2", CultureInfo.InvariantCulture),
            ["gap_recovery_min_heat_score"] = GapRecoveryMinHeatScore.ToString("F1", CultureInfo.InvariantCulture),
            ["max_result_count"] = MaxResultCount.ToString(CultureInfo.InvariantCulture)
        },
        _gapRecoveryOnly
            ? "在主线板块或强概念共振下，捕捉低开后有承接、分时修复、接近或站回 VWAP 的低开高走机会。"
            : "在主线板块或强概念共振下，优先捕捉分时贴近高位但尚未涨停、量能加速、站上 VWAP、突破短平台的早期机会。");

    public Task<IReadOnlyList<StrategySignal>> EvaluateAsync(
        StrategyContext context,
        CancellationToken cancellationToken)
    {
        if (context.Snapshot.Quotes.Count == 0 || context.MinuteBarsBySymbol is null || context.MinuteBarsBySymbol.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<StrategySignal>>([]);
        }

        var minAmount = GetDecimalParameter(context, "min_amount", MinAmount);
        var maxResultCount = GetIntParameter(context, "max_result_count", MaxResultCount);
        var marketAverageChange = context.MarketStats?.AverageChangePercent
            ?? context.Snapshot.Quotes.Average(item => item.ChangePercent);

        var signals = context.Snapshot.Quotes
            .Where(item => item.Price > 0 && item.Amount >= minAmount)
            .Select(item => BuildSignal(item, context, marketAverageChange, minAmount, _code, _name, _gapRecoveryOnly))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Score)
            .Take(Math.Clamp(maxResultCount, 1, 200))
            .ToArray();

        return Task.FromResult<IReadOnlyList<StrategySignal>>(signals);
    }

    private static StrategySignal? BuildSignal(
        StockQuote quote,
        StrategyContext context,
        decimal marketAverageChange,
        decimal minAmount,
        string strategyCode,
        string strategyName,
        bool gapRecoveryOnly)
    {
        if (!TryGetSectorHeat(context, quote.Symbol, out var sectorHeat))
        {
            return null;
        }

        if (!TryGetMinuteBars(context, quote.Symbol, out var minuteBars))
        {
            return null;
        }

        var todayBars = minuteBars
            .Where(item => DateOnly.FromDateTime(item.TradingTime) == context.TradingDate)
            .OrderBy(item => item.TradingTime)
            .ToArray();
        if (todayBars.Length < 12)
        {
            return null;
        }

        var lastBar = todayBars[^1];
        var currentPrice = quote.Price > 0 ? quote.Price : lastBar.Close;
        var firstOpenBar = todayBars.FirstOrDefault(item => item.Open > 0);
        var dayOpen = firstOpenBar?.Open ?? 0m;
        var dayHigh = Math.Max(todayBars.Max(item => item.High), currentPrice);
        if (dayOpen <= 0 || dayHigh <= 0)
        {
            return null;
        }

        var bestConceptHeat = GetBestConceptHeat(context, quote.Symbol);
        var effectiveHeatScore = Math.Max(sectorHeat.HeatScore, bestConceptHeat?.HeatScore ?? 0m);
        var minHeatScore = GetDecimalParameter(context, "min_heat_score", MinHeatScore);
        var returnFromOpen = Percent(currentPrice, dayOpen);
        var maxChangePercent = GetDecimalParameter(context, "max_change_percent", MaxChangePercent);
        var vwap = CalculateVwap(todayBars);
        if (vwap <= 0)
        {
            return null;
        }

        var highPosition = currentPrice * 100m / dayHigh;
        var drawdownFromHigh = (dayHigh - currentPrice) * 100m / dayHigh;
        var maxDrawdownFromHigh = GetDecimalParameter(context, "max_drawdown_from_high", MaxDrawdownFromHigh);

        var volumeAccel = CalculateVolumeAccel(todayBars);
        var fiveMinuteReturn = CalculateRecentReturn(todayBars, currentPrice, 5);
        var platformBreakout = CalculatePlatformBreakout(todayBars, currentPrice);
        var candidateHighLimit = GetDecimalParameter(context, "candidate_high_position_limit", CandidateHighPositionLimit);
        var confirmHighLimit = GetDecimalParameter(context, "confirm_high_position_limit", ConfirmHighPositionLimit);
        var candidateVolumeAccel = GetDecimalParameter(context, "candidate_volume_accel", CandidateVolumeAccel);
        var confirmVolumeAccel = GetDecimalParameter(context, "confirm_volume_accel", ConfirmVolumeAccel);
        var minFiveMinuteReturn = GetDecimalParameter(context, "min_5m_return", MinFiveMinuteReturn);
        var platformBreakoutPercent = GetDecimalParameter(context, "platform_breakout_percent", PlatformBreakoutPercent);
        var gapRecoveryMinOpenGapDown = GetDecimalParameter(context, "gap_recovery_min_open_gap_down", GapRecoveryMinOpenGapDown);
        var gapRecoveryMinReturnFromOpen = GetDecimalParameter(context, "gap_recovery_min_return_from_open", GapRecoveryMinReturnFromOpen);
        var gapRecoveryMaxChangePercent = GetDecimalParameter(context, "gap_recovery_max_change_percent", GapRecoveryMaxChangePercent);
        var gapRecoveryMinVwapRatio = GetDecimalParameter(context, "gap_recovery_min_vwap_ratio", GapRecoveryMinVwapRatio);
        var gapRecoveryMaxDrawdownFromHigh = GetDecimalParameter(context, "gap_recovery_max_drawdown_from_high", GapRecoveryMaxDrawdownFromHigh);
        var gapRecoveryMinVolumeAccel = GetDecimalParameter(context, "gap_recovery_min_volume_accel", GapRecoveryMinVolumeAccel);
        var gapRecoveryMinHeatScore = GetDecimalParameter(context, "gap_recovery_min_heat_score", GapRecoveryMinHeatScore);
        var previousClose = EstimatePreviousClose(currentPrice, quote.ChangePercent);
        var openGapPercent = previousClose > 0 ? Percent(dayOpen, previousClose) : 0m;
        var vwapRatio = currentPrice * 100m / vwap;
        var hasRegularHeat = effectiveHeatScore >= minHeatScore || quote.ChangePercent >= marketAverageChange + 0.6m;
        var canUseRegularBranch = hasRegularHeat
            && returnFromOpen >= minFiveMinuteReturn
            && quote.ChangePercent <= maxChangePercent
            && currentPrice >= vwap
            && drawdownFromHigh <= maxDrawdownFromHigh;
        var isCandidate = canUseRegularBranch
            && highPosition <= candidateHighLimit
            && volumeAccel >= candidateVolumeAccel
            && fiveMinuteReturn >= minFiveMinuteReturn;
        var isConfirm = canUseRegularBranch
            && highPosition <= confirmHighLimit
            && volumeAccel >= confirmVolumeAccel
            && platformBreakout >= platformBreakoutPercent
            && fiveMinuteReturn >= minFiveMinuteReturn;
        var isGapRecovery = openGapPercent <= gapRecoveryMinOpenGapDown
            && returnFromOpen >= gapRecoveryMinReturnFromOpen
            && quote.ChangePercent <= gapRecoveryMaxChangePercent
            && vwapRatio >= gapRecoveryMinVwapRatio
            && drawdownFromHigh <= gapRecoveryMaxDrawdownFromHigh
            && volumeAccel >= gapRecoveryMinVolumeAccel
            && effectiveHeatScore >= gapRecoveryMinHeatScore;
        if (gapRecoveryOnly)
        {
            isCandidate = false;
            isConfirm = false;
        }

        if ((!gapRecoveryOnly && !isCandidate && !isConfirm) || (gapRecoveryOnly && !isGapRecovery))
        {
            return null;
        }

        var conceptLeader = GetLeader(bestConceptHeat, quote.Symbol);
        var sectorLeader = GetLeader(sectorHeat, quote.Symbol);
        var sectorBonus = Math.Clamp((sectorHeat.HeatScore - 50m) * 0.25m, 0m, 12m);
        var conceptBonus = bestConceptHeat is null ? 0m : Math.Clamp((bestConceptHeat.HeatScore - 50m) * 0.18m, 0m, 9m);
        var breadthBonus = Math.Clamp((Math.Max(sectorHeat.RisingRatioPercent, bestConceptHeat?.RisingRatioPercent ?? 0m) - 50m) * 0.08m, 0m, 8m);
        var leaderBonus = (sectorLeader?.Rank <= ConceptLeaderRankThreshold ? 3m : 0m)
            + (conceptLeader?.Rank <= ConceptLeaderRankThreshold ? 4m : 0m);
        var highPenalty = Math.Clamp((highPosition - 96m) * 1.5m, 0m, 8m);
        var drawdownPenalty = Math.Clamp(drawdownFromHigh * 2m, 0m, 5m);
        var gapRecoveryBonus = isGapRecovery
            ? Math.Clamp((returnFromOpen - gapRecoveryMinReturnFromOpen) * 2.2m, 0m, 8m)
                + Math.Clamp((vwapRatio - gapRecoveryMinVwapRatio) * 0.8m, 0m, 4m)
            : 0m;

        var score = Math.Round(
            58m
            + Math.Max(returnFromOpen, 0m) * 2.0m
            + Math.Min(volumeAccel * 4m, 12m)
            + Math.Max(fiveMinuteReturn, 0m) * 1.8m
            + Math.Max(platformBreakout, 0m) * 3m
            + sectorBonus
            + conceptBonus
            + breadthBonus
            + leaderBonus
            + gapRecoveryBonus
            - highPenalty
            - drawdownPenalty,
            2);
        if (isGapRecovery && !isCandidate && !isConfirm)
        {
            score = Math.Min(score, 82m);
        }

        var action = isConfirm ? StrategySignalAction.Confirm : StrategySignalAction.Candidate;
        var confidence = isConfirm ? StrategySignalConfidence.High : StrategySignalConfidence.Medium;
        var vwapDistance = Percent(currentPrice, vwap);
        var stopLoss = ResolveStopLoss(currentPrice, vwap, todayBars);
        decimal? takeProfit = currentPrice > 0 ? Math.Round(currentPrice * 1.02m, 2) : null;
        var reason = isGapRecovery
            ? bestConceptHeat is null
                ? $"低开 {openGapPercent:F2}% 后修复，当前较开盘推高 {returnFromOpen:F2}%，价格相对 VWAP {vwapRatio:F1}%，量能加速 {volumeAccel:F2} 倍；行业 {sectorHeat.SectorName} 热度 {sectorHeat.HeatScore:F1}。"
                : $"低开 {openGapPercent:F2}% 后修复，当前较开盘推高 {returnFromOpen:F2}%，价格相对 VWAP {vwapRatio:F1}%，量能加速 {volumeAccel:F2} 倍；行业 {sectorHeat.SectorName} 热度 {sectorHeat.HeatScore:F1}，概念 {bestConceptHeat.ConceptName} 热度 {bestConceptHeat.HeatScore:F1}。"
            : bestConceptHeat is null
            ? $"分时站上 VWAP {vwap:F2}，5分钟涨幅 {fiveMinuteReturn:F2}%，量能加速 {volumeAccel:F2} 倍；行业 {sectorHeat.SectorName} 热度 {sectorHeat.HeatScore:F1}，高位占比 {highPosition:F1}%。"
            : $"分时站上 VWAP {vwap:F2}，5分钟涨幅 {fiveMinuteReturn:F2}%，量能加速 {volumeAccel:F2} 倍；行业 {sectorHeat.SectorName} 热度 {sectorHeat.HeatScore:F1}，概念 {bestConceptHeat.ConceptName} 热度 {bestConceptHeat.HeatScore:F1}。";

        var risk = isGapRecovery
            ? "低开修复仍需确认承接，若再次跌回 VWAP 下方或分时高点回落扩大，应降低级别。"
            : drawdownFromHigh > 0.8m
            ? "价格距离分时高点已有回落，若跌破 VWAP 或放量回落，应降低确认级别。"
            : "早发现信号仍需后续承接验证，若平台突破失败或跌回 VWAP 下方，应放弃追高。";

        var metrics = new Dictionary<string, decimal>
        {
            ["main_sector_version"] = 2m,
            ["main_sector_branch"] = isGapRecovery ? 2m : 1m,
            ["change_percent"] = quote.ChangePercent,
            ["market_average_change"] = marketAverageChange,
            ["amount"] = quote.Amount,
            ["min_amount"] = minAmount,
            ["sector_heat_score"] = sectorHeat.HeatScore,
            ["sector_average_change"] = sectorHeat.AverageChangePercent,
            ["sector_rising_ratio"] = sectorHeat.RisingRatioPercent,
            ["sector_total_amount"] = sectorHeat.TotalAmount,
            ["effective_heat_score"] = effectiveHeatScore,
            ["vwap"] = vwap,
            ["vwap_distance_percent"] = vwapDistance,
            ["vwap_ratio"] = vwapRatio,
            ["open_gap_percent"] = openGapPercent,
            ["intraday_return_from_open"] = returnFromOpen,
            ["intraday_high_position"] = highPosition,
            ["drawdown_from_intraday_high"] = drawdownFromHigh,
            ["volume_accel_5m"] = volumeAccel,
            ["return_5m"] = fiveMinuteReturn,
            ["platform_breakout_percent"] = platformBreakout,
            ["gap_recovery_min_open_gap_down"] = gapRecoveryMinOpenGapDown,
            ["gap_recovery_min_return_from_open"] = gapRecoveryMinReturnFromOpen,
            ["gap_recovery_min_vwap_ratio"] = gapRecoveryMinVwapRatio
        };

        if (sectorLeader is not null)
        {
            metrics["sector_leader_rank"] = sectorLeader.Rank;
        }

        if (bestConceptHeat is not null)
        {
            metrics["concept_heat_score"] = bestConceptHeat.HeatScore;
            metrics["concept_average_change"] = bestConceptHeat.AverageChangePercent;
            metrics["concept_rising_ratio"] = bestConceptHeat.RisingRatioPercent;
            metrics["concept_total_amount"] = bestConceptHeat.TotalAmount;
        }

        if (conceptLeader is not null)
        {
            metrics["concept_leader_rank"] = conceptLeader.Rank;
        }

        var tags = new List<string>
        {
            "主线早发现",
            "板块共振",
            "分时强势",
            sectorHeat.SectorName
        };
        if (bestConceptHeat is not null)
        {
            tags.Add(bestConceptHeat.ConceptName);
        }

        if (isConfirm)
        {
            tags.Add("平台突破");
        }

        if (isGapRecovery)
        {
            tags.Add("低开高走");
            tags.Add("承接修复");
        }

        var passedConditions = new List<string>
        {
            $"成交额 {quote.Amount / 100_000_000m:F1} 亿 >= {minAmount / 100_000_000m:F1} 亿",
            isGapRecovery ? $"价格/VWAP {vwapRatio:F1}% >= {gapRecoveryMinVwapRatio:F1}%" : $"分时价 {currentPrice:F2} >= VWAP {vwap:F2}",
            $"量能加速 {volumeAccel:F2} >= {(isGapRecovery && !isCandidate && !isConfirm ? gapRecoveryMinVolumeAccel : isConfirm ? confirmVolumeAccel : candidateVolumeAccel):F2}",
            isGapRecovery ? $"低开 {openGapPercent:F2}% <= {gapRecoveryMinOpenGapDown:F2}%" : $"分时高位占比 {highPosition:F1}% <= {(isConfirm ? confirmHighLimit : candidateHighLimit):F1}%",
            $"高点回落 {drawdownFromHigh:F2}% <= {(isGapRecovery && !isCandidate && !isConfirm ? gapRecoveryMaxDrawdownFromHigh : maxDrawdownFromHigh):F2}%",
            $"板块/概念有效热度 {effectiveHeatScore:F1}"
        };
        if (isGapRecovery)
        {
            passedConditions.Add($"开盘修复 {returnFromOpen:F2}% >= {gapRecoveryMinReturnFromOpen:F2}%");
        }
        else
        {
            passedConditions.Add($"5分钟涨幅 {fiveMinuteReturn:F2}% >= {minFiveMinuteReturn:F2}%");
        }

        if (isConfirm)
        {
            passedConditions.Add($"短平台突破 {platformBreakout:F2}% >= {platformBreakoutPercent:F2}%");
        }

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            StrategyCode: strategyCode,
            StrategyName: strategyName,
            StrategyType.IntradayOpportunity,
            Score: score,
            Price: currentPrice,
            Reason: reason,
            Risk: risk,
            Action: action,
            Confidence: confidence,
            Stage: StrategyStage.CandidateRanking,
            Metrics: metrics,
            Tags: tags,
            PassedConditions: passedConditions,
            FailedConditions: [],
            StopLossPrice: stopLoss,
            TakeProfitPrice: takeProfit);
    }

    private static bool TryGetSectorHeat(StrategyContext context, string symbol, out SectorHeat sectorHeat)
    {
        sectorHeat = null!;
        var normalized = StockSymbolNormalizer.NormalizeCode(symbol);
        if (context.SectorHeatSnapshot?.HeatBySymbol.TryGetValue(symbol, out sectorHeat!) == true)
        {
            return true;
        }

        return context.SectorHeatSnapshot?.HeatBySymbol.TryGetValue(normalized, out sectorHeat!) == true;
    }

    private static bool TryGetMinuteBars(StrategyContext context, string symbol, out IReadOnlyList<KLineBar> bars)
    {
        bars = Array.Empty<KLineBar>();
        var normalized = StockSymbolNormalizer.NormalizeCode(symbol);
        if (context.MinuteBarsBySymbol?.TryGetValue(symbol, out bars!) == true)
        {
            return true;
        }

        return context.MinuteBarsBySymbol?.TryGetValue(normalized, out bars!) == true;
    }

    private static decimal CalculateVwap(IReadOnlyList<KLineBar> bars)
    {
        var totalVolume = bars.Sum(item => item.Volume);
        if (totalVolume <= 0)
        {
            return 0m;
        }

        var turnover = bars.Sum(item => ((item.High + item.Low + item.Close) / 3m) * item.Volume);
        return turnover / totalVolume;
    }

    private static decimal CalculateVolumeAccel(IReadOnlyList<KLineBar> bars)
    {
        if (bars.Count < 10)
        {
            return 0m;
        }

        var recent = bars.TakeLast(5).Sum(item => item.Volume);
        var previous = bars.Take(Math.Max(0, bars.Count - 5)).TakeLast(20).Sum(item => item.Volume);
        if (previous <= 0)
        {
            return recent > 0 ? 9.99m : 0m;
        }

        return recent / (previous / 4m);
    }

    private static decimal CalculateRecentReturn(IReadOnlyList<KLineBar> bars, decimal currentPrice, int count)
    {
        if (bars.Count < count)
        {
            return 0m;
        }

        var anchor = bars.TakeLast(count).First().Open;
        return anchor > 0 ? Percent(currentPrice, anchor) : 0m;
    }

    private static decimal CalculatePlatformBreakout(IReadOnlyList<KLineBar> bars, decimal currentPrice)
    {
        if (bars.Count < 18)
        {
            return 0m;
        }

        var platformHigh = bars.Take(Math.Max(0, bars.Count - 1)).TakeLast(15).Max(item => item.High);
        return platformHigh > 0 ? Percent(currentPrice, platformHigh) : 0m;
    }

    private static decimal? ResolveStopLoss(decimal currentPrice, decimal vwap, IReadOnlyList<KLineBar> bars)
    {
        if (currentPrice <= 0)
        {
            return null;
        }

        var stop = currentPrice * 0.985m;
        if (vwap > 0 && vwap < currentPrice)
        {
            stop = Math.Max(stop, vwap * 0.995m);
        }

        if (bars.Count >= 18)
        {
            var platformLow = bars.Take(Math.Max(0, bars.Count - 1)).TakeLast(15).Min(item => item.Low);
            if (platformLow > 0 && platformLow < currentPrice)
            {
                stop = Math.Max(stop, platformLow * 0.995m);
            }
        }

        return Math.Round(Math.Min(stop, currentPrice * 0.999m), 2);
    }

    private static decimal Percent(decimal value, decimal basis)
    {
        return basis > 0 ? (value - basis) * 100m / basis : 0m;
    }

    private static decimal EstimatePreviousClose(decimal currentPrice, decimal changePercent)
    {
        var ratio = 1m + changePercent / 100m;
        return currentPrice > 0 && ratio > 0 ? currentPrice / ratio : 0m;
    }

    private static ConceptHeat? GetBestConceptHeat(StrategyContext context, string symbol)
    {
        var normalized = StockSymbolNormalizer.NormalizeCode(symbol);
        IReadOnlyList<ConceptHeat>? conceptHeats;
        if (context.ConceptHeatSnapshot?.HeatBySymbol.TryGetValue(symbol, out conceptHeats) != true
            && context.ConceptHeatSnapshot?.HeatBySymbol.TryGetValue(normalized, out conceptHeats) != true)
        {
            return null;
        }

        return conceptHeats?
            .OrderByDescending(item => item.HeatScore)
            .ThenByDescending(item => item.TotalAmount)
            .FirstOrDefault();
    }

    private static HeatLeader? GetLeader(ConceptHeat? heat, string symbol)
    {
        var normalized = StockSymbolNormalizer.NormalizeCode(symbol);
        return heat?.Leaders.FirstOrDefault(item =>
            string.Equals(StockSymbolNormalizer.NormalizeCode(item.Symbol), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static HeatLeader? GetLeader(SectorHeat heat, string symbol)
    {
        var normalized = StockSymbolNormalizer.NormalizeCode(symbol);
        return heat.Leaders.FirstOrDefault(item =>
            string.Equals(StockSymbolNormalizer.NormalizeCode(item.Symbol), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static decimal GetDecimalParameter(StrategyContext context, string key, decimal fallback)
    {
        return context.Parameters is not null
            && context.Parameters.TryGetValue(key, out var value)
            && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static int GetIntParameter(StrategyContext context, string key, int fallback)
    {
        return context.Parameters is not null
            && context.Parameters.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
