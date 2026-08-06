using AShareRadar.Application.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Application.Monitoring;

public static class MarketSentimentSignalAdjuster
{
    public static IReadOnlyList<StrategySignal> Apply(
        IEnumerable<StrategySignal> signals,
        MarketSentimentSnapshot? sentiment,
        MarketSentimentStrategyOptions options)
    {
        var signalArray = signals.ToArray();
        if (!options.Enabled || sentiment is null)
        {
            return signalArray;
        }

        return signalArray
            .Select(signal => ApplyOne(signal, sentiment, options))
            .ToArray();
    }

    private static StrategySignal ApplyOne(
        StrategySignal signal,
        MarketSentimentSnapshot sentiment,
        MarketSentimentStrategyOptions options)
    {
        var adjustment = CalculateAdjustment(signal, sentiment, options);
        var metrics = new Dictionary<string, decimal>(signal.Metrics ?? new Dictionary<string, decimal>(), StringComparer.OrdinalIgnoreCase)
        {
            ["market_sentiment_temperature"] = sentiment.TemperatureScore,
            ["market_sentiment_adjustment"] = adjustment
        };
        var shouldDemote = ShouldDemoteAction(signal, sentiment, options);
        var tags = (signal.Tags ?? [])
            .Concat([$"情绪:{sentiment.Level}"])
            .Concat(shouldDemote ? ["情绪降级观察"] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var reason = shouldDemote
            ? $"{signal.Reason}；市场情绪 {sentiment.Level}({sentiment.TemperatureScore:F1})，进攻信号降级为观察，情绪因子 {adjustment:+0.##;-0.##} 分。"
            : adjustment == 0
            ? $"{signal.Reason}；市场情绪 {sentiment.Level}({sentiment.TemperatureScore:F1})，信号分保持。"
            : $"{signal.Reason}；市场情绪 {sentiment.Level}({sentiment.TemperatureScore:F1})，情绪因子 {adjustment:+0.##;-0.##} 分。";
        var risk = BuildRisk(signal.Risk, sentiment, adjustment, options);

        return signal with
        {
            Score = Math.Max(0m, decimal.Round(signal.Score + adjustment, 2)),
            Action = shouldDemote ? StrategySignalAction.Watch : signal.Action,
            Confidence = shouldDemote ? StrategySignalConfidence.Low : signal.Confidence,
            Reason = reason,
            Risk = risk,
            Metrics = metrics,
            Tags = tags
        };
    }

    private static decimal CalculateAdjustment(
        StrategySignal signal,
        MarketSentimentSnapshot sentiment,
        MarketSentimentStrategyOptions options)
    {
        var rule = sentiment.Level switch
        {
            "冰点" => options.Frozen,
            "偏冷" => options.Cold,
            "中性" => options.Neutral,
            "偏热" => options.Hot,
            "过热" => options.Overheated,
            _ => options.Neutral
        };

        if (IsMainlineOrTrendSignal(signal))
        {
            return rule.MainlineOrTrend;
        }

        return IsAggressiveSignal(signal)
            ? rule.Aggressive
            : rule.Defensive;
    }

    private static bool IsAggressiveSignal(StrategySignal signal)
    {
        return signal.StrategyType == StrategyType.IntradayOpportunity ||
               signal.Action is StrategySignalAction.Candidate or StrategySignalAction.Confirm ||
               ContainsAny(signal.StrategyName, "突破", "强势", "主升", "进攻") ||
               (signal.Tags?.Any(tag => ContainsAny(tag, "突破", "强势", "主升")) ?? false);
    }

    private static bool IsMainlineOrTrendSignal(StrategySignal signal)
    {
        return ContainsAny(signal.StrategyName, "主线", "共振", "强势", "趋势") ||
               (signal.Tags?.Any(tag => ContainsAny(tag, "主线", "共振", "强势", "趋势")) ?? false);
    }

    private static bool ShouldDemoteAction(
        StrategySignal signal,
        MarketSentimentSnapshot sentiment,
        MarketSentimentStrategyOptions options)
    {
        return options.EnableActionDemotion &&
               sentiment.TemperatureScore < options.DemoteAggressiveBelowTemperature &&
               IsAggressiveSignal(signal) &&
               signal.Action is StrategySignalAction.Candidate or StrategySignalAction.Confirm;
    }

    private static bool ContainsAny(string value, params string[] keywords)
    {
        return keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string? BuildRisk(
        string? originalRisk,
        MarketSentimentSnapshot sentiment,
        decimal adjustment,
        MarketSentimentStrategyOptions options)
    {
        var sentimentRisk = sentiment.Level switch
        {
            "冰点" => "情绪冰点，主动进攻信号降权，优先观察修复确认。",
            "偏冷" => "情绪偏冷，信号需要更高确认度，注意弱反弹失败。",
            "偏热" => "情绪偏热，主线信号加权，但需留意拥挤回撤。",
            "过热" when sentiment.TemperatureScore >= options.OverheatedRiskTemperature => "情绪过热且温度高于拥挤阈值，高位进攻信号降权，炸板和一致性回落风险增强。",
            "过热" => "情绪过热，高位信号降权，注意炸板和一致性回落风险。",
            _ => adjustment == 0 ? null : $"市场情绪 {sentiment.Level}，已进行情绪因子修正。"
        };
        if (string.IsNullOrWhiteSpace(sentimentRisk))
        {
            return originalRisk;
        }

        return string.IsNullOrWhiteSpace(originalRisk)
            ? sentimentRisk
            : $"{originalRisk}；{sentimentRisk}";
    }
}
