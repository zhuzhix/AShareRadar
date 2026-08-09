using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Opportunities;
using AShareRadar.Application.Opportunities;

namespace AShareRadar.Application.MarketData;

public sealed class AuctionObservationOptions
{
    public bool Enabled { get; set; } = true;

    public int MaxWatchCount { get; set; } = 50;

    public int PollIntervalSeconds { get; set; } = 3;

    public string OpenConfirmEndTime { get; set; } = "09:33";
}

public sealed record AuctionWatchItem(
    string Symbol,
    string Name,
    int Rank,
    decimal Score,
    string StrategyNames,
    DateTimeOffset SourceHitTime);

public sealed record AuctionQuoteLevel(
    decimal Price,
    decimal Volume,
    bool IsBid);

public sealed record AuctionTickSnapshot(
    string Symbol,
    string Name,
    DateTimeOffset EventTime,
    decimal? Price,
    decimal PreClose,
    decimal CumVolume,
    decimal CumAmount,
    IReadOnlyList<AuctionQuoteLevel> Quotes);

public sealed record AuctionObservation(
    DateOnly TradingDate,
    DateOnly ReferenceTradeDate,
    string Symbol,
    string Name,
    int SourceRank,
    decimal SourceScore,
    string SourceStrategies,
    DateTimeOffset? LatestEventTime,
    string Phase,
    decimal? ReferencePrice,
    decimal? GapPercent,
    decimal Imbalance,
    decimal QueueDecay,
    decimal StrengthScore,
    decimal RiskScore,
    string Status,
    string OpenConfirmStatus,
    string Reason);

public interface IAuctionDataProvider
{
    Task<IReadOnlyList<AuctionTickSnapshot>> LoadCurrentAuctionAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken);
}

public interface IAuctionObservationStore
{
    void ReplaceWatchPool(
        DateOnly tradingDate,
        DateOnly referenceTradeDate,
        IReadOnlyList<AuctionWatchItem> items);

    IReadOnlyList<AuctionWatchItem> GetWatchPool(DateOnly tradingDate);

    void UpsertTicks(DateOnly tradingDate, IReadOnlyList<AuctionTickSnapshot> snapshots);

    IReadOnlyList<AuctionTickSnapshot> GetTicks(DateOnly tradingDate);
}

public sealed class AuctionObservationService
{
    private readonly OpportunityAppService _opportunityAppService;
    private readonly TradingCalendarService _calendar;
    private readonly TradingSessionService _session;
    private readonly IAuctionDataProvider _dataProvider;
    private readonly IAuctionObservationStore _store;
    private readonly AuctionObservationOptions _options;
    private readonly object _gate = new();

    public AuctionObservationService(
        OpportunityAppService opportunityAppService,
        TradingCalendarService calendar,
        TradingSessionService session,
        IAuctionDataProvider dataProvider,
        IAuctionObservationStore store,
        AuctionObservationOptions options)
    {
        _opportunityAppService = opportunityAppService;
        _calendar = calendar;
        _session = session;
        _dataProvider = dataProvider;
        _store = store;
        _options = options;
    }

    public IReadOnlyList<AuctionObservation> Query(DateOnly tradingDate)
    {
        var pool = _store.GetWatchPool(tradingDate);
        var ticks = _store.GetTicks(tradingDate);
        return BuildObservations(tradingDate, pool, ticks, DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<AuctionObservation>> RefreshAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return [];
        }

        var tradingDate = DateOnly.FromDateTime(now.LocalDateTime);
        var status = _session.GetMarketStatus(now);
        if (!_calendar.IsTradingDay(tradingDate) ||
            status is not Domain.Monitoring.MarketStatus.CallAuction and not Domain.Monitoring.MarketStatus.Trading)
        {
            return Query(tradingDate);
        }

        if (status == Domain.Monitoring.MarketStatus.Trading &&
            (!TimeOnly.TryParse(_options.OpenConfirmEndTime, out var openConfirmEnd) ||
             TimeOnly.FromDateTime(now.LocalDateTime) > openConfirmEnd))
        {
            return Query(tradingDate);
        }

        var pool = EnsureWatchPool(tradingDate);
        if (pool.Count == 0)
        {
            return [];
        }

        var snapshots = await _dataProvider.LoadCurrentAuctionAsync(
            pool.Select(item => item.Symbol).ToArray(),
            cancellationToken);
        if (snapshots.Count > 0)
        {
            _store.UpsertTicks(tradingDate, snapshots);
        }

        return Query(tradingDate);
    }

    public IReadOnlyList<AuctionWatchItem> EnsureWatchPool(DateOnly tradingDate)
    {
        lock (_gate)
        {
            var existing = _store.GetWatchPool(tradingDate);
            if (existing.Count > 0)
            {
                return existing;
            }

            var referenceDate = _calendar.GetPreviousTradingDate(tradingDate);
            var events = _opportunityAppService.GetEventsForTradingDate(referenceDate);
            var items = events
                .GroupBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var ordered = group
                        .OrderByDescending(item => item.Score)
                        .ThenByDescending(item => item.EventTime)
                        .ToArray();
                    var best = ordered[0];
                    var strategyNames = ordered
                        .SelectMany(item => item.StrategyHits.Count > 0
                            ? item.StrategyHits.Select(hit => hit.StrategyName)
                            : [item.StrategyName])
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return new AuctionWatchItem(
                        best.Symbol,
                        best.Name,
                        0,
                        best.Score,
                        string.Join(" / ", strategyNames),
                        best.EventTime);
                })
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.SourceHitTime)
                .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(_options.MaxWatchCount, 1, 200))
                .Select((item, index) => item with { Rank = index + 1 })
                .ToArray();

            _store.ReplaceWatchPool(tradingDate, referenceDate, items);
            return items;
        }
    }

    private IReadOnlyList<AuctionObservation> BuildObservations(
        DateOnly tradingDate,
        IReadOnlyList<AuctionWatchItem> pool,
        IReadOnlyList<AuctionTickSnapshot> ticks,
        DateTimeOffset now)
    {
        var phase = GetPhase(now);
        var result = new List<AuctionObservation>(pool.Count);
        foreach (var item in pool)
        {
            var symbolTicks = ticks
                .Where(tick => tick.Symbol.Equals(item.Symbol, StringComparison.OrdinalIgnoreCase))
                .OrderBy(tick => tick.EventTime)
                .ToArray();
            var latest = symbolTicks.LastOrDefault();
            decimal? latestPrice = latest?.Price is > 0m ? latest.Price : null;
            decimal? gap = latestPrice.HasValue && latest!.PreClose > 0m
                ? (latestPrice.Value - latest.PreClose) / latest.PreClose * 100m
                : null;
            var imbalance = CalculateImbalance(latest?.Quotes ?? []);
            var queueDecay = CalculateQueueDecay(symbolTicks);
            var strength = Math.Clamp(50m + (gap ?? 0m) * 4m + imbalance * 25m, 0m, 100m);
            var risk = Math.Clamp(Math.Max(0m, queueDecay * 100m) + Math.Max(0m, -imbalance * 25m), 0m, 100m);
            var status = latest is null ? "等待竞价数据" :
                risk >= 60m ? "撤单/转弱观察" :
                strength >= 65m ? "竞价偏强" :
                strength <= 35m ? "竞价偏弱" : "震荡观察";
            var openConfirm = phase == "开盘确认"
                ? latestPrice.HasValue ? "已获取开盘确认" : "等待开盘确认"
                : "待开盘确认";
            var reason = latest is null
                ? "上一交易日命中前50，等待集合竞价快照"
                : $"昨日排名 {item.Rank}，分数 {item.Score:F1}；盘口失衡 {imbalance:P1}，队列衰减 {queueDecay:P1}";

            result.Add(new AuctionObservation(
                tradingDate,
                _calendar.GetPreviousTradingDate(tradingDate),
                item.Symbol,
                item.Name,
                item.Rank,
                item.Score,
                item.StrategyNames,
                latest?.EventTime,
                phase,
                latestPrice,
                gap.HasValue ? Math.Round(gap.Value, 2) : null,
                Math.Round(imbalance, 4),
                Math.Round(queueDecay, 4),
                Math.Round(strength, 1),
                Math.Round(risk, 1),
                status,
                openConfirm,
                reason));
        }

        return result
            .OrderByDescending(item => item.StrengthScore)
            .ThenBy(item => item.SourceRank)
            .ToArray();
    }

    private string GetPhase(DateTimeOffset now)
    {
        var status = _session.GetMarketStatus(now);
        if (status == Domain.Monitoring.MarketStatus.CallAuction)
        {
            return "集合竞价";
        }

        if (status == Domain.Monitoring.MarketStatus.Trading &&
            TimeOnly.TryParse(_options.OpenConfirmEndTime, out var end) &&
            TimeOnly.FromDateTime(now.LocalDateTime) <= end)
        {
            return "开盘确认";
        }

        return status.ToString();
    }

    private static decimal CalculateImbalance(IReadOnlyList<AuctionQuoteLevel> quotes)
    {
        var bid = quotes.Where(item => item.IsBid).Sum(item => item.Volume);
        var ask = quotes.Where(item => !item.IsBid).Sum(item => item.Volume);
        return bid + ask <= 0m ? 0m : (bid - ask) / (bid + ask);
    }

    private static decimal CalculateQueueDecay(IReadOnlyList<AuctionTickSnapshot> ticks)
    {
        if (ticks.Count < 2)
        {
            return 0m;
        }

        var early = ticks
            .Take(Math.Min(3, ticks.Count))
            .Select(item => item.Quotes.Sum(quote => quote.Volume))
            .DefaultIfEmpty(0m)
            .Max();
        var latest = ticks[^1].Quotes.Sum(item => item.Volume);
        return early <= 0m ? 0m : Math.Clamp((early - latest) / early, 0m, 1m);
    }
}
