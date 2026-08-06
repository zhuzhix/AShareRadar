using AShareRadar.Application.MarketData;
using AShareRadar.Application.Monitoring;
using AShareRadar.Application.Opportunities;
using AShareRadar.Application.Opportunities.Storage;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Monitoring;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Application.Tests;

internal static class ApplicationSmoke
{
    public static void Main()
    {
        var status = new MonitorAppService(new MonitorRuntimeState()).GetStatus();
        if (status.MonitorStatus != "NotStarted")
        {
            throw new InvalidOperationException("Unexpected monitor status.");
        }

        AssertLimitStatus("600000", "SPD Bank", 11.00m, 10.00m, LimitStatus.LimitUp);
        AssertLimitStatus("300750", "CATL", 110.00m, 10.00m, LimitStatus.None);
        AssertLimitStatus("300750", "CATL", 120.00m, 20.00m, LimitStatus.LimitUp);
        AssertLimitStatus("688001", "STAR Stock", 80.00m, -20.00m, LimitStatus.LimitDown);
        AssertLimitStatus("002001", "*ST Test", 5.25m, 5.00m, LimitStatus.LimitUp);
        AssertTradingSessionSchedule();
        AssertOpportunityEventsAreStored();
        AssertMarketSentimentUsesLimitPoolWhenAvailable().GetAwaiter().GetResult();
        AssertMarketSentimentFallsBackWhenLimitPoolFails().GetAwaiter().GetResult();
    }

    private static void AssertLimitStatus(
        string symbol,
        string name,
        decimal price,
        decimal changePercent,
        LimitStatus expected)
    {
        var quote = new StockQuote(
            symbol,
            name,
            price,
            changePercent,
            0,
            0,
            0,
            DateTimeOffset.Now);
        var actual = LimitStatusCalculator.Calculate(quote);
        if (actual != expected)
        {
            throw new InvalidOperationException($"Unexpected limit status for {symbol}: {actual}, expected {expected}.");
        }
    }

    private static void AssertOpportunityEventsAreStored()
    {
        var service = new OpportunityAppService(new NoopOpportunityStateStore());
        var events = service.ApplyStrategySignals(
            Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.Today),
            DateTimeOffset.Now,
            [
                new StrategySignal(
                    "605179",
                    "Test Stock",
                    "limit-breakout",
                    "Limit Breakout",
                    StrategyType.IntradayOpportunity,
                    100m,
                    12.34m,
                    "Test reason.",
                    null)
            ]);

        if (events.Count != 1)
        {
            throw new InvalidOperationException($"Unexpected signal event count: {events.Count}.");
        }

        var storedEvents = service.GetEventsForOpportunity(events[0].OpportunityId, 1);
        if (storedEvents.Count != 1 || storedEvents[0].StrategyHits.Count != 1)
        {
            throw new InvalidOperationException("Opportunity signal event was not stored.");
        }
    }

    private static async Task AssertMarketSentimentUsesLimitPoolWhenAvailable()
    {
        var provider = new FakeLimitPoolProvider(new LimitPoolSnapshot(DateOnly.FromDateTime(DateTime.Today), 7, 2, "FakeLimitPool"));
        var sentiment = await CreateMarketSentimentService(provider).GetSnapshotAsync(CancellationToken.None);

        AssertMetric(sentiment, "limit_up_count", 7, "FakeLimitPool");
        AssertMetric(sentiment, "limit_down_count", 2, "FakeLimitPool");
        if (sentiment.Warnings.Any(item => item.Contains("回退到本地价格规则", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Successful limit pool path should not emit fallback warning.");
        }
    }

    private static async Task AssertMarketSentimentFallsBackWhenLimitPoolFails()
    {
        var provider = new FakeLimitPoolProvider(null);
        var sentiment = await CreateMarketSentimentService(provider).GetSnapshotAsync(CancellationToken.None);

        AssertMetric(sentiment, "limit_up_count", 2, "LocalLimitPriceRule");
        AssertMetric(sentiment, "limit_down_count", 1, "LocalLimitPriceRule");
        if (!sentiment.Warnings.Any(item => item.Contains("回退到本地价格规则", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Failed limit pool path should emit fallback warning.");
        }
    }

    private static MarketSentimentService CreateMarketSentimentService(ILimitPoolProvider limitPoolProvider)
    {
        return new MarketSentimentService(
            new FakeMarketDataProvider(),
            new FakeKLineDataProvider(),
            new FakeSectorHeatService(),
            new OpportunityAppService(new NoopOpportunityStateStore()),
            new FakeMarketSentimentStore(),
            new FakeExternalDataProvider(),
            new TradingCalendarService(new TradingCalendarOptions()),
            limitPoolProvider);
    }

    private static void AssertMetric(MarketSentimentSnapshot sentiment, string code, decimal expectedValue, string expectedSource)
    {
        var metric = sentiment.Metrics.FirstOrDefault(item => item.Code == code);
        if (metric is null)
        {
            throw new InvalidOperationException($"Metric {code} was not emitted.");
        }

        if (metric.Value != expectedValue || metric.SourceStatus != expectedSource)
        {
            throw new InvalidOperationException($"Unexpected metric {code}: {metric.Value}/{metric.SourceStatus}, expected {expectedValue}/{expectedSource}.");
        }
    }

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        public string ProviderName => "FakeRealtime";

        public Task<MarketSnapshot> LoadMarketSnapshotAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.Now;
            return Task.FromResult(new MarketSnapshot(
                now,
                ProviderName,
                [
                    Quote("600000", "Main Up", 11.00m, 10.00m),
                    Quote("600001", "Main Down", 9.00m, -10.00m),
                    Quote("300750", "Growth", 120.00m, 20.00m),
                    Quote("000001", "Flat", 10.01m, 0.10m)
                ]));
        }

        private static StockQuote Quote(string symbol, string name, decimal price, decimal changePercent)
        {
            return new StockQuote(
                symbol,
                name,
                price,
                changePercent,
                1.2m,
                2.4m,
                300_000_000m,
                DateTimeOffset.Now);
        }
    }

    private sealed class FakeKLineDataProvider : IKLineDataProvider
    {
        public string ProviderName => "FakeKLine";

        public Task<IReadOnlyList<KLineBar>> LoadKLineAsync(string symbol, string period, int count, CancellationToken cancellationToken)
        {
            IReadOnlyList<KLineBar> bars =
            [
                new(DateTime.Today.AddDays(-1), 10m, 11m, 9m, 10m, 20_000_000m),
                new(DateTime.Today.AddDays(-2), 10m, 11m, 9m, 10m, 18_000_000m)
            ];
            return Task.FromResult(bars);
        }
    }

    private sealed class FakeLimitPoolProvider : ILimitPoolProvider
    {
        private readonly LimitPoolSnapshot? _snapshot;

        public FakeLimitPoolProvider(LimitPoolSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public string ProviderName => "FakeLimitPool";

        public Task<LimitPoolSnapshot?> LoadAsync(DateOnly tradingDate, CancellationToken cancellationToken)
        {
            return Task.FromResult(_snapshot);
        }

        public MarketSentimentDataSourceStatus GetStatus()
        {
            return _snapshot is null
                ? MarketSentimentDataSourceStatus.Unavailable("FakeLimitPool", "Failed.")
                : MarketSentimentDataSourceStatus.Available("FakeLimitPool", "Available.");
        }
    }

    private sealed class FakeSectorHeatService : ISectorHeatService
    {
        public SectorHeatSnapshot Build(MarketSnapshot snapshot)
        {
            return new SectorHeatSnapshot(snapshot.SnapshotTime, new Dictionary<string, SectorHeat>(), new Dictionary<string, SectorMembership>(), new Dictionary<string, SectorHeat>());
        }

        public SectorHeatMappingStatus GetMappingStatus()
        {
            return new SectorHeatMappingStatus("", 0, null, "Fake");
        }

        public ConceptHeatSnapshot BuildConcepts(MarketSnapshot snapshot)
        {
            return new ConceptHeatSnapshot(snapshot.SnapshotTime, new Dictionary<string, ConceptHeat>(), new Dictionary<string, IReadOnlyList<ConceptMembership>>(), new Dictionary<string, IReadOnlyList<ConceptHeat>>());
        }

        public SectorHeatMappingStatus GetConceptMappingStatus()
        {
            return new SectorHeatMappingStatus("", 0, null, "Fake");
        }

        public void ReloadMappings()
        {
        }
    }

    private static void AssertTradingSessionSchedule()
    {
        var service = new TradingSessionService(
            new TradingCalendarService(new TradingCalendarOptions()),
            new TradingSessionOptions());
        var offset = TimeSpan.FromHours(8);

        AssertMarketStatus(service, new DateTimeOffset(2026, 8, 5, 9, 0, 0, offset), MarketStatus.BeforeOpen);
        AssertMarketStatus(service, new DateTimeOffset(2026, 8, 5, 9, 20, 0, offset), MarketStatus.CallAuction);
        AssertMarketStatus(service, new DateTimeOffset(2026, 8, 5, 10, 0, 0, offset), MarketStatus.Trading);
        AssertMarketStatus(service, new DateTimeOffset(2026, 8, 5, 12, 0, 0, offset), MarketStatus.MiddayBreak);
        AssertMarketStatus(service, new DateTimeOffset(2026, 8, 5, 14, 0, 0, offset), MarketStatus.Trading);
        AssertMarketStatus(service, new DateTimeOffset(2026, 8, 5, 15, 0, 0, offset), MarketStatus.Closed);
        AssertMarketStatus(service, new DateTimeOffset(2026, 8, 8, 10, 0, 0, offset), MarketStatus.NonTradingDay);

        var readyTime = new TimeOnly(15, 15);
        var beforeReady = service.GetLatestCompletedTradingDate(
            new DateTimeOffset(2026, 8, 5, 15, 10, 0, offset),
            readyTime);
        var afterReady = service.GetLatestCompletedTradingDate(
            new DateTimeOffset(2026, 8, 5, 15, 20, 0, offset),
            readyTime);
        var weekend = service.GetLatestCompletedTradingDate(
            new DateTimeOffset(2026, 8, 8, 10, 0, 0, offset),
            readyTime);
        if (beforeReady != new DateOnly(2026, 8, 4)
            || afterReady != new DateOnly(2026, 8, 5)
            || weekend != new DateOnly(2026, 8, 7))
        {
            throw new InvalidOperationException(
                $"Unexpected completed trading dates: {beforeReady}, {afterReady}, {weekend}.");
        }
    }

    private static void AssertMarketStatus(
        TradingSessionService service,
        DateTimeOffset time,
        MarketStatus expected)
    {
        var actual = service.GetMarketStatus(time);
        if (actual != expected)
        {
            throw new InvalidOperationException($"Unexpected market status at {time}: {actual}, expected {expected}.");
        }
    }

    private sealed class FakeMarketSentimentStore : IMarketSentimentStore
    {
        public MarketSentimentSnapshot? Latest { get; private set; }

        public void Save(MarketSentimentSnapshot snapshot)
        {
            Latest = snapshot;
        }

        public MarketSentimentSnapshot? GetLatest()
        {
            return Latest;
        }

        public IReadOnlyList<MarketSentimentSnapshot> Query(DateOnly? tradingDate, int count)
        {
            return Latest is null ? [] : [Latest];
        }
    }

    private sealed class FakeExternalDataProvider : IMarketSentimentExternalDataProvider
    {
        public Task<MarketSentimentExternalSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(MarketSentimentExternalSnapshot.Empty(GetStatus()));
        }

        public MarketSentimentDataSourceStatus GetStatus()
        {
            return MarketSentimentDataSourceStatus.Disabled("ExternalSentimentData");
        }
    }
}
