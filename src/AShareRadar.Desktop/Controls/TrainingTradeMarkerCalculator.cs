namespace AShareRadar.Desktop.Controls;

public static class TrainingTradeMarkerCalculator
{
    private const int AtrWindow = 14;
    private const int MinimumDistanceBars = 5;
    private const int MaximumMarkerCount = 40;

    public static IReadOnlyList<TrainingTradeMarker> Calculate(IReadOnlyList<KLineCandle> candles)
    {
        if (candles.Count < 24)
        {
            return [];
        }

        var threshold = ResolveReversalThreshold(candles);
        var markers = new List<TrainingTradeMarker>();
        var trend = 0;
        var candidateHighIndex = 0;
        var candidateLowIndex = 0;

        for (var index = 1; index < candles.Count; index++)
        {
            if (candles[index].High >= candles[candidateHighIndex].High)
            {
                candidateHighIndex = index;
            }

            if (candles[index].Low <= candles[candidateLowIndex].Low)
            {
                candidateLowIndex = index;
            }

            if (trend == 0)
            {
                var rebound = ChangePercent(candles[index].High, candles[candidateLowIndex].Low);
                var drawdown = ChangePercent(candles[candidateHighIndex].High, candles[index].Low);
                if (rebound >= threshold)
                {
                    AddMarker(markers, candles, candidateLowIndex, "B", rebound, threshold);
                    trend = 1;
                    candidateHighIndex = index;
                }
                else if (drawdown >= threshold)
                {
                    AddMarker(markers, candles, candidateHighIndex, "S", drawdown, threshold);
                    trend = -1;
                    candidateLowIndex = index;
                }

                continue;
            }

            if (trend > 0)
            {
                var drawdown = ChangePercent(candles[candidateHighIndex].High, candles[index].Low);
                if (drawdown >= threshold)
                {
                    AddMarker(markers, candles, candidateHighIndex, "S", drawdown, threshold);
                    trend = -1;
                    candidateLowIndex = index;
                }
            }
            else
            {
                var rebound = ChangePercent(candles[index].High, candles[candidateLowIndex].Low);
                if (rebound >= threshold)
                {
                    AddMarker(markers, candles, candidateLowIndex, "B", rebound, threshold);
                    trend = 1;
                    candidateHighIndex = index;
                }
            }
        }

        return markers
            .OrderBy(item => item.Index)
            .TakeLast(MaximumMarkerCount)
            .ToArray();
    }

    private static void AddMarker(
        List<TrainingTradeMarker> markers,
        IReadOnlyList<KLineCandle> candles,
        int index,
        string markerType,
        decimal movePercent,
        decimal threshold)
    {
        if (index < 0 || index >= candles.Count)
        {
            return;
        }

        if (markers.Count > 0 && index - markers[^1].Index < MinimumDistanceBars)
        {
            if (markers[^1].MarkerType == markerType)
            {
                var replace = markerType == "B"
                    ? candles[index].Low < candles[markers[^1].Index].Low
                    : candles[index].High > candles[markers[^1].Index].High;
                if (replace)
                {
                    markers[^1] = CreateMarker(candles, index, markerType, movePercent, threshold);
                }
            }

            return;
        }

        if (markers.Any(item => item.Index == index && item.MarkerType == markerType))
        {
            return;
        }

        markers.Add(CreateMarker(candles, index, markerType, movePercent, threshold));
    }

    private static TrainingTradeMarker CreateMarker(
        IReadOnlyList<KLineCandle> candles,
        int index,
        string markerType,
        decimal movePercent,
        decimal threshold)
    {
        var candle = candles[index];
        var price = markerType == "B" ? candle.Low : candle.High;
        var strength = Math.Clamp(movePercent / Math.Max(threshold, 0.0001m), 1m, 3m);
        var reason = markerType == "B"
            ? $"ZigZag确认波段低点，后续反弹 {movePercent:P2}"
            : $"ZigZag确认波段高点，后续回落 {movePercent:P2}";
        return new TrainingTradeMarker(index, candle.TradingTime, price, markerType, strength, reason);
    }

    private static decimal ResolveReversalThreshold(IReadOnlyList<KLineCandle> candles)
    {
        var fixedThreshold = ResolveFixedThreshold(candles);
        var atr = CalculateAtr(candles, AtrWindow);
        var latestClose = candles[^1].Close;
        var atrThreshold = latestClose <= 0 ? 0m : atr / latestClose * ResolveAtrMultiplier(candles);
        return Math.Clamp(Math.Max(fixedThreshold, atrThreshold), 0.004m, 0.08m);
    }

    private static decimal ResolveFixedThreshold(IReadOnlyList<KLineCandle> candles)
    {
        var medianMinutes = ResolveMedianIntervalMinutes(candles);
        if (medianMinutes <= 1.5) return 0.006m;
        if (medianMinutes <= 5.5) return 0.008m;
        if (medianMinutes <= 30.5) return 0.012m;
        if (medianMinutes <= 65) return 0.015m;
        return 0.030m;
    }

    private static decimal ResolveAtrMultiplier(IReadOnlyList<KLineCandle> candles)
    {
        var medianMinutes = ResolveMedianIntervalMinutes(candles);
        if (medianMinutes <= 1.5) return 1.2m;
        if (medianMinutes <= 5.5) return 1.3m;
        if (medianMinutes <= 30.5) return 1.5m;
        if (medianMinutes <= 65) return 1.6m;
        return 1.8m;
    }

    private static decimal CalculateAtr(IReadOnlyList<KLineCandle> candles, int window)
    {
        if (candles.Count < 2)
        {
            return 0m;
        }

        var start = Math.Max(1, candles.Count - window);
        var values = new List<decimal>(candles.Count - start);
        for (var index = start; index < candles.Count; index++)
        {
            var highLow = candles[index].High - candles[index].Low;
            var highPreviousClose = Math.Abs(candles[index].High - candles[index - 1].Close);
            var lowPreviousClose = Math.Abs(candles[index].Low - candles[index - 1].Close);
            values.Add(Math.Max(highLow, Math.Max(highPreviousClose, lowPreviousClose)));
        }

        return values.Count == 0 ? 0m : values.Average();
    }

    private static decimal ChangePercent(decimal high, decimal low)
    {
        if (high <= 0 || low <= 0 || high <= low)
        {
            return 0m;
        }

        return (high - low) / low;
    }

    private static double ResolveMedianIntervalMinutes(IReadOnlyList<KLineCandle> candles)
    {
        var intervals = candles
            .Zip(candles.Skip(1), (left, right) => (right.TradingTime - left.TradingTime).TotalMinutes)
            .Where(item => item > 0 && item < 7 * 24 * 60)
            .OrderBy(item => item)
            .ToArray();
        return intervals.Length == 0 ? 1440d : intervals[intervals.Length / 2];
    }
}
