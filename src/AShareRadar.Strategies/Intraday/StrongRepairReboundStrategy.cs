using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Intraday;

public sealed class StrongRepairReboundStrategy : ISignalStrategy
{
    private const int RequiredBarCount = 60;
    private const decimal MinRepairFromLowPercent = 2.2m;
    private const decimal MinVolumeRatio = 1.0m;
    private const decimal MaxDistanceBelowMa20Percent = -4m;
    private const decimal MaxChangePercent = 6.5m;
    private const decimal MinClosePositionPercent = 65m;

    public string Code => "strong-repair-rebound";

    public string Name => "强修复反弹";

    public StrategyType Type => StrategyType.Experimental;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.TriggerConfirmation,
        StrategySignalAction.Watch,
        new StrategyDataRequirement(true, true, false, false, false, RequiredBarCount),
        new Dictionary<string, string>
        {
            ["min_repair_from_low_percent"] = MinRepairFromLowPercent.ToString("F1"),
            ["min_volume_ratio"] = MinVolumeRatio.ToString("F1"),
            ["min_close_position_percent"] = MinClosePositionPercent.ToString("F0")
        },
        "捕捉盘中下探后明显回拉、重新站稳短均线或关键支撑的修复型观察机会。");

    public Task<IReadOnlyList<StrategySignal>> EvaluateAsync(StrategyContext context, CancellationToken cancellationToken)
    {
        var signals = context.Snapshot.Quotes
            .Where(item => item.Price > 0 && item.ChangePercent <= MaxChangePercent && item.VolumeRatio >= MinVolumeRatio)
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
        var currentBar = orderedBars.LastOrDefault(item => DateOnly.FromDateTime(item.TradingTime) == context.TradingDate);
        if (currentBar is null || currentBar.High <= currentBar.Low)
        {
            return null;
        }

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
        var latestClose = historyBars[^1].Close;
        var repairFromLowPercent = currentBar.Low > 0 ? (quote.Price - currentBar.Low) / currentBar.Low * 100m : 0m;
        var intradayDrawdownPercent = latestClose > 0 ? (currentBar.Low - latestClose) / latestClose * 100m : 0m;
        var priceAboveOpenPercent = currentBar.Open > 0 ? (quote.Price - currentBar.Open) / currentBar.Open * 100m : 0m;
        var priceAboveMa5Percent = ma5 > 0 ? (quote.Price - ma5) / ma5 * 100m : 0m;
        var priceAboveMa20Percent = ma20 > 0 ? (quote.Price - ma20) / ma20 * 100m : 0m;
        var trendStrengthPercent = ma60 > 0 ? (ma20 - ma60) / ma60 * 100m : 0m;
        var closePositionPercent = (quote.Price - currentBar.Low) / (currentBar.High - currentBar.Low) * 100m;
        var upperShadowPercent = CalculateUpperShadowPercent(currentBar, quote.Price);

        var failed = new List<string>();
        if (intradayDrawdownPercent >= -0.8m)
        {
            failed.Add($"盘中下探不足，低点较昨收 {intradayDrawdownPercent:F1}%");
        }

        if (repairFromLowPercent < MinRepairFromLowPercent)
        {
            failed.Add($"低点修复 {repairFromLowPercent:F1}% < {MinRepairFromLowPercent:F1}%");
        }

        if (priceAboveOpenPercent < 0m || quote.Price <= latestClose)
        {
            failed.Add("当前价未重新站上开盘价和上一日收盘");
        }

        if (priceAboveMa20Percent < MaxDistanceBelowMa20Percent)
        {
            failed.Add($"距离 MA20 {priceAboveMa20Percent:F1}% 过弱");
        }

        if (closePositionPercent < MinClosePositionPercent)
        {
            failed.Add($"收盘/当前价位置 {closePositionPercent:F0}% < {MinClosePositionPercent:F0}%");
        }

        if (failed.Count > 0)
        {
            return null;
        }

        var confidence = quote.VolumeRatio >= 1.3m && priceAboveMa5Percent >= 0m && upperShadowPercent <= 25m
            ? StrategySignalConfidence.High
            : StrategySignalConfidence.Medium;
        var score = 62m
            + Math.Min(repairFromLowPercent * 3m, 15m)
            + Math.Min(quote.VolumeRatio * 4m, 10m)
            + Math.Max(8m - Math.Abs(priceAboveMa20Percent), 0m)
            + Math.Max(closePositionPercent - 65m, 0m) / 6m;

        return new StrategySignal(
            quote.Symbol,
            quote.Name,
            Code,
            Name,
            Type,
            Math.Clamp(score, 0m, 100m),
            quote.Price,
            $"盘中低点较昨收 {intradayDrawdownPercent:F1}%，当前自低点修复 {repairFromLowPercent:F1}%，重新站上开盘价 {priceAboveOpenPercent:F1}%，量比 {quote.VolumeRatio:F2}。",
            BuildRisk(trendStrengthPercent, priceAboveMa20Percent, upperShadowPercent),
            StrategySignalAction.Watch,
            confidence,
            StrategyStage.TriggerConfirmation,
            new Dictionary<string, decimal>
            {
                ["repair_from_low_percent"] = repairFromLowPercent,
                ["intraday_drawdown_percent"] = intradayDrawdownPercent,
                ["price_above_open_percent"] = priceAboveOpenPercent,
                ["price_above_ma5_percent"] = priceAboveMa5Percent,
                ["price_above_ma20_percent"] = priceAboveMa20Percent,
                ["trend_strength_percent"] = trendStrengthPercent,
                ["close_position_percent"] = closePositionPercent,
                ["upper_shadow_percent"] = upperShadowPercent,
                ["volume_ratio"] = quote.VolumeRatio
            },
            ["强修复", "低点回拉", confidence == StrategySignalConfidence.High ? "承接较强" : "观察修复"],
            [
                $"盘中下探后修复 {repairFromLowPercent:F1}% >= {MinRepairFromLowPercent:F1}%",
                $"当前价站上开盘价 {priceAboveOpenPercent:F1}%",
                $"距离 MA20 {priceAboveMa20Percent:F1}% 未明显破位",
                $"收盘/当前价位置 {closePositionPercent:F0}% >= {MinClosePositionPercent:F0}%"
            ],
            confidence == StrategySignalConfidence.High ? [] : ["修复策略波动较大，仍需观察承接持续性"],
            Math.Round(Math.Max(currentBar.Low, ma20 * 0.97m), 2),
            Math.Round(quote.Price * 1.05m, 2));
    }

    private static decimal AverageClose(IReadOnlyList<KLineBar> bars, int count)
    {
        return bars.Count < count ? 0m : bars.TakeLast(count).Average(item => item.Close);
    }

    private static decimal CalculateUpperShadowPercent(KLineBar currentBar, decimal currentPrice)
    {
        var bodyTop = Math.Max(currentBar.Open, currentPrice);
        return Math.Max(currentBar.High - bodyTop, 0m) / (currentBar.High - currentBar.Low) * 100m;
    }

    private static string? BuildRisk(decimal trendStrengthPercent, decimal priceAboveMa20Percent, decimal upperShadowPercent)
    {
        var risks = new List<string>();
        if (trendStrengthPercent < 0m)
        {
            risks.Add("中期趋势偏弱，可能只是弱反抽");
        }

        if (priceAboveMa20Percent < 0m)
        {
            risks.Add("尚未站稳 MA20，需要确认支撑");
        }

        if (upperShadowPercent > 25m)
        {
            risks.Add("上影线偏长，注意冲高回落");
        }

        return risks.Count == 0 ? null : string.Join("；", risks);
    }
}
