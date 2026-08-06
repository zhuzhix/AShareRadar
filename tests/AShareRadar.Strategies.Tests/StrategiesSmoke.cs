using AShareRadar.Application.MarketData;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;
using AShareRadar.Strategies.Intraday;
using AShareRadar.Strategies.Registry;

namespace AShareRadar.Strategies.Tests;

internal static class StrategiesSmoke
{
    public static async Task Main()
    {
        EmptyRegistryShouldReturnNoStrategies();
        await MainSectorResonanceShouldConfirmEarlyIntradayBreakoutAsync();
        await MainSectorResonanceShouldFindGapDownRecoveryAsync();
        await MainSectorResonanceShouldRequireMinuteBarsAsync();
        await MainSectorResonanceShouldRejectDeepIntradayPullbackAsync();
        await PlatformVolumeBreakoutShouldUseDailyStructureAsync();
        await PlatformVolumeBreakoutShouldRejectLongUpperShadowAsync();
        await MovingAveragePullbackShouldUseDailyStructureAsync();
        await MovingAveragePullbackShouldRejectSupportBreakdownAsync();
        await StrongTrendContinuationShouldUseTrendStructureAsync();
        await StrongTrendContinuationShouldRejectOverheatedTrendAsync();
        await CounterTrendStrengthShouldFindRelativeStrengthInWeakMarketAsync();
        await CounterTrendStrengthShouldIgnoreStrongMarketAsync();
        await StrongRepairReboundShouldFindIntradayRepairAsync();
        await DreamerDaAShouldCreateLongTermWatchAsync();
        await ZhongheYingtaiShouldCreateMainriseWatchAsync();
    }

    private static void EmptyRegistryShouldReturnNoStrategies()
    {
        var registry = new StrategyRegistry([]);
        if (registry.GetEnabledStrategies().Count != 0)
        {
            throw new InvalidOperationException("Empty registry should not return strategies.");
        }
    }

    private static async Task MainSectorResonanceShouldConfirmEarlyIntradayBreakoutAsync()
    {
        var strategy = new MainSectorResonanceStrategy();
        var quote = Quote("300000", 10.75m, 3.2m, 2.6m, 200_000_000m);
        var context = BuildMainSectorContext(quote, MainSectorMinuteBars(highOverride: 10.78m));

        var signal = RequireSignal(await strategy.EvaluateAsync(context, CancellationToken.None), "main-sector-resonance", "volume_accel_5m");
        if (signal.Action != StrategySignalAction.Confirm || !signal.Metrics!.ContainsKey("vwap"))
        {
            throw new InvalidOperationException("Main sector resonance should confirm early intraday breakout with VWAP diagnostics.");
        }
    }

    private static async Task MainSectorResonanceShouldRequireMinuteBarsAsync()
    {
        var strategy = new MainSectorResonanceStrategy();
        var quote = Quote("300000", 10.75m, 3.2m, 2.6m, 200_000_000m);
        var context = BuildMainSectorContext(quote, []);

        if ((await strategy.EvaluateAsync(context, CancellationToken.None)).Count != 0)
        {
            throw new InvalidOperationException("Main sector resonance should not signal without minute bars.");
        }
    }

    private static async Task MainSectorResonanceShouldFindGapDownRecoveryAsync()
    {
        var strategy = MainSectorResonanceStrategy.CreateGapRecovery();
        var quote = Quote("300012", 9.85m, -1.5m, 1.6m, 180_000_000m);
        var context = BuildMainSectorContext(quote, MainSectorGapRecoveryMinuteBars());

        var signal = RequireSignal(await strategy.EvaluateAsync(context, CancellationToken.None), "main-sector-gap-recovery", "open_gap_percent");
        if (signal.StrategyName != "主线低开高走" || signal.Action != StrategySignalAction.Candidate)
        {
            throw new InvalidOperationException("Main sector gap recovery should create a low-open-high-recovery candidate.");
        }

        if (signal.Metrics?["main_sector_branch"] != 2m)
        {
            throw new InvalidOperationException("Main sector gap recovery should be marked as branch 2.");
        }
    }

    private static async Task MainSectorResonanceShouldRejectDeepIntradayPullbackAsync()
    {
        var strategy = new MainSectorResonanceStrategy();
        var quote = Quote("300000", 10.75m, 3.2m, 2.6m, 200_000_000m);
        var bars = MainSectorMinuteBars(highOverride: 11.30m);
        var context = BuildMainSectorContext(quote, bars);

        if ((await strategy.EvaluateAsync(context, CancellationToken.None)).Count != 0)
        {
            throw new InvalidOperationException("Main sector resonance should reject deep pullback from intraday high.");
        }
    }

    private static async Task PlatformVolumeBreakoutShouldUseDailyStructureAsync()
    {
        var strategy = new PlatformVolumeBreakoutStrategy();
        var quote = Quote("300001", 10.20m, 2.1m, 1.9m);
        var historyBars = Bars(45, index => 9.80m, high: 10.00m, low: 9.30m);
        var currentBar = new KLineBar(DateTime.Today, 10.00m, 10.30m, 9.90m, 10.20m, 2_000_000m);
        var context = BuildContext(quote, historyBars.Append(currentBar).ToArray(), WeeklyPlatformBars());

        var signal = RequireSignal(await strategy.EvaluateAsync(context, CancellationToken.None), "platform-volume-breakout", "weekly_platform_high");
        if (signal.Action != StrategySignalAction.Candidate || !signal.StopLossPrice.HasValue || !signal.TakeProfitPrice.HasValue)
        {
            throw new InvalidOperationException("Weekly platform breakout signal should include trade diagnostics.");
        }
    }

    private static async Task PlatformVolumeBreakoutShouldRejectLongUpperShadowAsync()
    {
        var strategy = new PlatformVolumeBreakoutStrategy();
        var quote = Quote("300003", 10.20m, 2.1m, 2.0m);
        var historyBars = Bars(40, _ => 9.80m, high: 10.00m, low: 9.50m);
        var currentBar = new KLineBar(DateTime.Today, 10.00m, 11.30m, 9.80m, 10.20m, 2_000_000m);
        var context = BuildContext(quote, historyBars.Append(currentBar).ToArray(), WeeklyPlatformBars());

        if ((await strategy.EvaluateAsync(context, CancellationToken.None)).Count != 0)
        {
            throw new InvalidOperationException("Platform breakout strategy should reject a long upper-shadow breakout.");
        }
    }

    private static async Task MovingAveragePullbackShouldUseDailyStructureAsync()
    {
        var strategy = new MovingAveragePullbackRestartStrategy();
        var quote = Quote("300002", 13.05m, 1.6m, 1.3m);
        var bars = Bars(60, index => 10.00m + index * 0.05m, lowSelector: (index, close) => index >= 52 ? 12.05m : close - 0.15m);
        var signal = RequireSignal(await strategy.EvaluateAsync(BuildContext(quote, bars), CancellationToken.None), "moving-average-pullback-restart", "support_line");
        if (!signal.Metrics!.ContainsKey("pullback_volume_ratio"))
        {
            throw new InvalidOperationException("Pullback signal should include volume diagnostics.");
        }
    }

    private static async Task MovingAveragePullbackShouldRejectSupportBreakdownAsync()
    {
        var strategy = new MovingAveragePullbackRestartStrategy();
        var quote = Quote("300004", 12.90m, 1.2m, 1.4m);
        var bars = Bars(60, index => index >= 50 ? 11.20m : 10.00m + index * 0.05m, volumeSelector: index => index >= 50 ? 1_400_000m : 1_000_000m);

        if ((await strategy.EvaluateAsync(BuildContext(quote, bars), CancellationToken.None)).Count != 0)
        {
            throw new InvalidOperationException("Moving average pullback strategy should reject a support breakdown.");
        }
    }

    private static async Task StrongTrendContinuationShouldUseTrendStructureAsync()
    {
        var strategy = new StrongTrendContinuationStrategy();
        var quote = Quote("300005", 13.35m, 1.5m, 1.4m);
        var signal = RequireSignal(await strategy.EvaluateAsync(BuildContext(quote, TrendBars(60, 0.05m)), CancellationToken.None), "strong-trend-continuation", "trend_age_days");
        if (!signal.Metrics!.ContainsKey("price_above_ma20_percent"))
        {
            throw new InvalidOperationException("Strong trend signal should include MA20 distance.");
        }
    }

    private static async Task StrongTrendContinuationShouldRejectOverheatedTrendAsync()
    {
        var strategy = new StrongTrendContinuationStrategy();
        var quote = Quote("300006", 16.20m, 4.0m, 1.6m);
        var bars = Bars(60, index =>
        {
            var close = 10.00m + index * 0.05m;
            return index >= 55 ? close + (index - 54) * 0.70m : close;
        });

        if ((await strategy.EvaluateAsync(BuildContext(quote, bars), CancellationToken.None)).Count != 0)
        {
            throw new InvalidOperationException("Strong trend strategy should reject an overheated trend.");
        }
    }

    private static async Task CounterTrendStrengthShouldFindRelativeStrengthInWeakMarketAsync()
    {
        var strategy = new CounterTrendStrengthStrategy();
        var target = Quote("300007", 12.70m, 1.2m, 1.3m);
        var snapshot = new MarketSnapshot(DateTimeOffset.Now, "Test",
        [
            target,
            Quote("300101", 8m, -1.0m, 1.0m),
            Quote("300102", 9m, -0.8m, 1.0m),
            Quote("300103", 7m, -0.6m, 1.0m)
        ]);
        var signal = RequireSignal(await strategy.EvaluateAsync(BuildContext(target, snapshot, TrendBars(60, 0.035m)), CancellationToken.None), "counter-trend-strength", "relative_strength_percent");
        if (!signal.Metrics!.ContainsKey("market_average_change"))
        {
            throw new InvalidOperationException("Counter-trend signal should include market average.");
        }
    }

    private static async Task CounterTrendStrengthShouldIgnoreStrongMarketAsync()
    {
        var strategy = new CounterTrendStrengthStrategy();
        var target = Quote("300008", 12.70m, 1.8m, 1.3m);
        var snapshot = new MarketSnapshot(DateTimeOffset.Now, "Test",
        [
            target,
            Quote("300201", 8m, 1.2m, 1.0m),
            Quote("300202", 9m, 1.0m, 1.0m),
            Quote("300203", 7m, 0.9m, 1.0m)
        ]);

        if ((await strategy.EvaluateAsync(BuildContext(target, snapshot, TrendBars(60, 0.035m)), CancellationToken.None)).Count != 0)
        {
            throw new InvalidOperationException("Counter-trend strategy should ignore a strong market.");
        }
    }

    private static async Task StrongRepairReboundShouldFindIntradayRepairAsync()
    {
        var strategy = new StrongRepairReboundStrategy();
        var quote = Quote("300009", 12.80m, 1.1m, 1.4m);
        var history = TrendBars(60, 0.04m);
        var currentBar = new KLineBar(DateTime.Today, 12.30m, 13.00m, 12.05m, 12.80m, 1_500_000m);
        var signal = RequireSignal(await strategy.EvaluateAsync(BuildContext(quote, history.Append(currentBar).ToArray()), CancellationToken.None), "strong-repair-rebound", "repair_from_low_percent");
        if (!signal.Metrics!.ContainsKey("intraday_drawdown_percent"))
        {
            throw new InvalidOperationException("Repair rebound signal should include low repair diagnostics.");
        }
    }

    private static async Task DreamerDaAShouldCreateLongTermWatchAsync()
    {
        var strategy = new DreamerDaAStrategy();
        var quote = Quote("300010", 13.40m, 0.8m, 1.0m);
        var signal = RequireSignal(await strategy.EvaluateAsync(BuildContext(quote, TrendBars(80, 0.035m)), CancellationToken.None), "dreamer-da-a", "distance_from_support_percent");
        if (signal.Stage != StrategyStage.ReviewOnly || signal.Action != StrategySignalAction.Watch)
        {
            throw new InvalidOperationException("Dreamer strategy should stay review/watch only.");
        }
    }

    private static async Task ZhongheYingtaiShouldCreateMainriseWatchAsync()
    {
        var strategy = new ZhongheYingtaiMainriseStrategy();
        var quote = Quote("300011", 13.45m, 0.9m, 1.0m);
        var signal = RequireSignal(await strategy.EvaluateAsync(BuildContext(quote, TrendBars(80, 0.035m)), CancellationToken.None), "zhonghe-yingtai-mainrise", "trend_line");
        if (signal.Stage != StrategyStage.ReviewOnly || signal.Action != StrategySignalAction.PullbackWait)
        {
            throw new InvalidOperationException("Zhonghe Yingtai strategy should stay review/pullback wait.");
        }
    }

    private static StrategySignal RequireSignal(IReadOnlyList<StrategySignal> signals, string code, string metric)
    {
        var signal = signals.SingleOrDefault(item => item.StrategyCode == code)
            ?? throw new InvalidOperationException($"{code} should create a signal.");
        if (signal.Metrics is null || !signal.Metrics.ContainsKey(metric) || signal.PassedConditions is null || signal.PassedConditions.Count == 0)
        {
            throw new InvalidOperationException($"{code} should include explainable diagnostics.");
        }

        return signal;
    }

    private static StrategyContext BuildContext(StockQuote quote, IReadOnlyList<KLineBar> bars, IReadOnlyList<KLineBar>? weeklyBars = null)
    {
        return BuildContext(quote, new MarketSnapshot(DateTimeOffset.Now, "Test", [quote]), bars, weeklyBars);
    }

    private static StrategyContext BuildContext(
        StockQuote quote,
        MarketSnapshot snapshot,
        IReadOnlyList<KLineBar> bars,
        IReadOnlyList<KLineBar>? weeklyBars = null)
    {
        return new StrategyContext(
            Guid.NewGuid(),
            Today(),
            snapshot,
            DailyBarsBySymbol: new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase)
            {
                [quote.Symbol] = bars
            },
            WeeklyBarsBySymbol: weeklyBars is null
                ? null
                : new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase)
                {
                    [quote.Symbol] = weeklyBars
                });
    }

    private static StrategyContext BuildMainSectorContext(StockQuote quote, IReadOnlyList<KLineBar> minuteBars)
    {
        var snapshot = new MarketSnapshot(DateTimeOffset.Now, "Test",
        [
            quote,
            Quote("300901", 8m, -0.5m, 1m, 50_000_000m),
            Quote("300902", 9m, 0.1m, 1m, 50_000_000m)
        ]);
        var leader = new HeatLeader(1, quote.Symbol, quote.Name, quote.ChangePercent, quote.Amount, quote.VolumeRatio);
        var heat = new SectorHeat(
            "BK001",
            "测试主线",
            3,
            2,
            1.2m,
            66.7m,
            350_000_000m,
            72m,
            [leader],
            [quote.Symbol]);
        var sectorSnapshot = new SectorHeatSnapshot(
            DateTimeOffset.Now,
            new Dictionary<string, SectorHeat>(StringComparer.OrdinalIgnoreCase)
            {
                ["BK001"] = heat
            },
            new Dictionary<string, SectorMembership>(StringComparer.OrdinalIgnoreCase)
            {
                [quote.Symbol] = new SectorMembership(quote.Symbol, "BK001", "测试主线")
            },
            new Dictionary<string, SectorHeat>(StringComparer.OrdinalIgnoreCase)
            {
                [quote.Symbol] = heat
            });

        return new StrategyContext(
            Guid.NewGuid(),
            Today(),
            snapshot,
            MinuteBarsBySymbol: new Dictionary<string, IReadOnlyList<KLineBar>>(StringComparer.OrdinalIgnoreCase)
            {
                [quote.Symbol] = minuteBars
            },
            SectorHeatSnapshot: sectorSnapshot,
            MarketStats: new StrategyMarketStats(0.8m, 60m, 30m, 400_000_000m));
    }

    private static StockQuote Quote(string symbol, decimal price, decimal change, decimal volumeRatio, decimal amount = 100_000_000m)
    {
        return new StockQuote(symbol, $"Stock{symbol}", price, change, volumeRatio, 4m, amount, DateTimeOffset.Now);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);

    private static KLineBar[] TrendBars(int count, decimal step)
    {
        return Bars(count, index => 10.00m + index * step);
    }

    private static KLineBar[] WeeklyPlatformBars()
    {
        return Enumerable.Range(0, 24)
            .Select(index =>
            {
                var close = index % 4 == 0 ? 9.92m : 9.55m + index % 3 * 0.04m;
                return new KLineBar(
                    DateTime.Today.AddDays(-(24 - index) * 7),
                    Open: close - 0.10m,
                    High: index % 5 == 0 ? 10.00m : 9.86m,
                    Low: 8.85m,
                    Close: close,
                    Volume: 8_000_000m);
            })
            .ToArray();
    }

    private static KLineBar[] MainSectorMinuteBars(decimal? highOverride = null)
    {
        return Enumerable.Range(0, 30)
            .Select(index =>
            {
                var close = index < 25
                    ? 10.00m + index * 0.015m
                    : 10.50m + (index - 25) * 0.055m;
                var volume = index < 25 ? 500_000m : 3_000_000m;
                return new KLineBar(
                    DateTime.Today.AddHours(9).AddMinutes(30 + index),
                    Open: close - 0.03m,
                    High: index == 29 && highOverride.HasValue ? highOverride.Value : close + 0.04m,
                    Low: close - 0.08m,
                    Close: close,
                    Volume: volume);
            })
            .ToArray();
    }

    private static KLineBar[] MainSectorGapRecoveryMinuteBars()
    {
        return Enumerable.Range(0, 30)
            .Select(index =>
            {
                var close = index < 24
                    ? 9.70m + index * 0.003m
                    : 9.80m + (index - 24) * 0.010m;
                var volume = index < 25 ? 400_000m : 2_400_000m;
                return new KLineBar(
                    DateTime.Today.AddHours(9).AddMinutes(30 + index),
                    Open: index == 0 ? 9.70m : close - 0.01m,
                    High: close + 0.04m,
                    Low: close - 0.05m,
                    Close: close,
                    Volume: volume);
            })
            .ToArray();
    }

    private static KLineBar[] Bars(
        int count,
        Func<int, decimal> closeSelector,
        decimal? high = null,
        decimal? low = null,
        Func<int, decimal>? volumeSelector = null,
        Func<int, decimal, decimal>? lowSelector = null)
    {
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var close = closeSelector(index);
                var itemLow = lowSelector?.Invoke(index, close) ?? low ?? close - 0.12m;
                return new KLineBar(
                    DateTime.Today.AddDays(index - count),
                    Open: close - 0.04m,
                    High: high ?? close + 0.16m,
                    Low: itemLow,
                    Close: close,
                    Volume: volumeSelector?.Invoke(index) ?? 1_000_000m);
            })
            .ToArray();
    }
}
