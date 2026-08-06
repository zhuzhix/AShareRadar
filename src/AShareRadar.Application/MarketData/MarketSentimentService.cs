using AShareRadar.Application.Opportunities;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Opportunities;

namespace AShareRadar.Application.MarketData;

public sealed class MarketSentimentService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(20);

    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IKLineDataProvider _kLineDataProvider;
    private readonly ISectorHeatService _sectorHeatService;
    private readonly OpportunityAppService _opportunityAppService;
    private readonly IMarketSentimentStore _store;
    private readonly IMarketSentimentExternalDataProvider _externalDataProvider;
    private readonly ILimitPoolProvider _limitPoolProvider;
    private readonly IMarketUniverseProvider _marketUniverseProvider;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private MarketSentimentSnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedAt;

    public MarketSentimentService(
        IMarketDataProvider marketDataProvider,
        IKLineDataProvider kLineDataProvider,
        ISectorHeatService sectorHeatService,
        OpportunityAppService opportunityAppService,
        IMarketSentimentStore store,
        IMarketSentimentExternalDataProvider externalDataProvider,
        TradingCalendarService tradingCalendarService,
        ILimitPoolProvider limitPoolProvider,
        IMarketUniverseProvider marketUniverseProvider)
    {
        _marketDataProvider = marketDataProvider;
        _kLineDataProvider = kLineDataProvider;
        _sectorHeatService = sectorHeatService;
        _opportunityAppService = opportunityAppService;
        _store = store;
        _externalDataProvider = externalDataProvider;
        _limitPoolProvider = limitPoolProvider;
        _marketUniverseProvider = marketUniverseProvider;
    }

    public async Task<MarketSentimentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cachedSnapshot is not null && DateTimeOffset.Now - _cachedAt < CacheDuration)
            {
                return _cachedSnapshot;
            }
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_cachedSnapshot is not null && DateTimeOffset.Now - _cachedAt < CacheDuration)
                {
                    return _cachedSnapshot;
                }
            }

            var marketSnapshot = await _marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
            var snapshot = await BuildSnapshotAsync(marketSnapshot, cancellationToken);
            _store.Save(snapshot);

            lock (_gate)
            {
                _cachedSnapshot = snapshot;
                _cachedAt = DateTimeOffset.Now;
            }

            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public MarketSentimentSnapshot? GetLatestPersistedSnapshot()
    {
        return _store.GetLatest();
    }

    public IReadOnlyList<MarketSentimentSnapshot> QueryPersistedSnapshots(DateOnly? tradingDate, int count)
    {
        return _store.Query(tradingDate, count);
    }

    public IReadOnlyList<MarketSentimentDataSourceStatus> GetDataSourceStatuses()
    {
        return
        [
            MarketSentimentDataSourceStatus.Available("RealtimeSnapshot", $"{_marketDataProvider.ProviderName} realtime quote snapshot."),
            MarketSentimentDataSourceStatus.Available("DailyKLine", $"{_kLineDataProvider.ProviderName} daily k-line."),
            _limitPoolProvider.GetStatus(),
            _externalDataProvider.GetStatus()
        ];
    }

    private async Task<MarketSentimentSnapshot> BuildSnapshotAsync(MarketSnapshot snapshot, CancellationToken cancellationToken)
    {
        var validQuotes = snapshot.Quotes.Where(item => item.Price > 0).ToArray();
        if (validQuotes.Length == 0)
        {
            return new MarketSentimentSnapshot(
                DateTimeOffset.Now,
                snapshot.ProviderName,
                50m,
                "中性",
                "暂无可用行情，情绪分暂按中性处理。",
                "NoData",
                [],
                [],
                ["实时行情没有返回可计算股票。"]);
        }

        var warnings = new List<string>();
        var quoteBySymbol = validQuotes
            .GroupBy(item => StockSymbolNormalizer.NormalizeCode(item.Symbol), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.First(), StringComparer.OrdinalIgnoreCase);

        var realtimeCount = validQuotes.Length;
        var marketUniverse = await _marketUniverseProvider.LoadUniverseAsync(cancellationToken);
        var totalCount = Math.Max(marketUniverse?.TotalCount ?? 0, realtimeCount);
        var risingCount = validQuotes.Count(item => item.ChangePercent > 0);
        var fallingCount = validQuotes.Count(item => item.ChangePercent < 0);
        var localLimitStatuses = validQuotes.Select(LimitStatusCalculator.Calculate).ToArray();
        var localLimitUpCount = localLimitStatuses.Count(item => item == LimitStatus.LimitUp);
        var localLimitDownCount = localLimitStatuses.Count(item => item == LimitStatus.LimitDown);
        var tradingDate = DateOnly.FromDateTime(snapshot.SnapshotTime.LocalDateTime);
        var limitPool = await _limitPoolProvider.LoadAsync(tradingDate, cancellationToken);
        var limitUpCount = limitPool?.LimitUpCount ?? localLimitUpCount;
        var limitDownCount = limitPool?.LimitDownCount ?? localLimitDownCount;
        var limitSource = limitPool?.Source ?? "LocalLimitPriceRule";
        if (limitPool is null)
        {
            warnings.Add("东方财富涨跌停专题池不可用，已回退到本地价格规则。");
        }

        var bigRiseCount = validQuotes.Count(item => item.ChangePercent >= 5m);
        var bigFallCount = validQuotes.Count(item => item.ChangePercent <= -5m);
        var averageChange = validQuotes.Average(item => item.ChangePercent);
        var totalAmount = validQuotes.Sum(item => item.Amount);
        var averageTurnover = validQuotes.Average(item => item.TurnoverRate);
        var turnoverPace = BuildTurnoverPaceSample(snapshot.SnapshotTime, totalAmount);
        var risingRatio = risingCount * 100m / totalCount;
        var fallingRatio = fallingCount * 100m / totalCount;
        var growthAverage = AverageChange(validQuotes, IsGrowthBoard);
        var mainBoardAverage = AverageChange(validQuotes, IsMainBoard);
        var smallAverage = AverageChange(validQuotes, item => item.Amount > 0 && item.Amount <= 200_000_000m);
        var largeAverage = AverageChange(validQuotes, item => item.Amount >= 1_000_000_000m);
        var growthStrength = growthAverage - mainBoardAverage;
        var smallCapStrength = smallAverage - largeAverage;

        var amountAverage = await BuildAmountAverageSampleAsync(validQuotes, cancellationToken);
        if (amountAverage is null)
        {
            warnings.Add("20 日成交额均值样本不可用，交易热度按实时成交额与换手率计算。");
        }

        var sectorSnapshot = BuildSectorHeat(snapshot, warnings);
        var conceptSnapshot = BuildConceptHeat(snapshot, warnings);
        var hotSectorCount = sectorSnapshot?.SectorsByCode.Values.Count(item => item.HeatScore >= 65m) ?? 0;
        var topSector = sectorSnapshot?.SectorsByCode.Values.OrderByDescending(item => item.HeatScore).FirstOrDefault();
        var hotConceptCount = conceptSnapshot?.ConceptsByCode.Values.Count(item => item.HeatScore >= 65m) ?? 0;
        var topConcept = conceptSnapshot?.ConceptsByCode.Values.OrderByDescending(item => item.HeatScore).FirstOrDefault();
        var opportunityBreakdown = BuildOpportunityBreakdown(quoteBySymbol);
        var external = await _externalDataProvider.LoadAsync(cancellationToken);
        var capitalScore = BuildCapitalScore(external);
        var externalScore = BuildExternalPressureScore(external);

        var amountRatioScore = amountAverage is null
            ? 0m
            : Math.Clamp((amountAverage.CurrentAmount / Math.Max(amountAverage.Average20DayAmount, 1m) - 0.85m) * 70m, -15m, 25m);
        var breadthScore = ClampScore(ScorePercent(risingRatio, 20m, 80m) + Math.Clamp((limitUpCount - limitDownCount) * 1.2m, -18m, 18m));
        var fallbackTradingScore = ClampScore(50m + amountRatioScore + Math.Clamp((averageTurnover - 2m) * 4m, -12m, 18m));
        var turnoverScore = turnoverPace is null ? (decimal?)null : ScoreTurnoverPace(turnoverPace.PaceRatio);
        var turnoverAdjustedTurnoverScore = ClampScore(50m + Math.Clamp((averageTurnover - 2m) * 4m, -12m, 18m));
        var tradingScore = turnoverScore.HasValue
            ? ClampScore(turnoverScore.Value * 0.70m + turnoverAdjustedTurnoverScore * 0.30m)
            : fallbackTradingScore;
        var shortTermScore = ClampScore(
            45m +
            Math.Clamp(limitUpCount * 1.8m, 0m, 28m) -
            Math.Clamp(limitDownCount * 2.2m, 0m, 22m) +
            Math.Clamp((bigRiseCount - bigFallCount) * 0.45m, -18m, 18m) +
            Math.Clamp((opportunityBreakdown.All ?? 0m) * 5m, -12m, 12m));
        var riskScore = ClampScore(
            50m +
            Math.Clamp(growthStrength * 8m, -20m, 20m) +
            Math.Clamp(smallCapStrength * 5m, -12m, 12m) +
            Math.Clamp(hotSectorCount * 1.5m, 0m, 12m));
        var temperature = ClampScore(
            breadthScore * 0.28m +
            tradingScore * 0.18m +
            shortTermScore * 0.24m +
            riskScore * 0.14m +
            capitalScore * 0.10m +
            externalScore * 0.06m);
        var level = GetLevel(temperature);

        var categories = new[]
        {
            CreateCategory("breadth", "赚钱效应", breadthScore, $"上涨占比 {risingRatio:F1}%，涨停 {limitUpCount}，跌停 {limitDownCount}。"),
            CreateCategory("trading", "交易热度", tradingScore, BuildTradingDescription(totalAmount, averageTurnover, amountAverage, turnoverPace)),
            CreateCategory("short-term", "短线情绪", shortTermScore, $"涨停 {limitUpCount}，跌停 {limitDownCount}，大涨5% {bigRiseCount}，大跌5% {bigFallCount}，强池收盘 {FormatNullablePercent(opportunityBreakdown.All)}。"),
            CreateCategory("risk", "风险偏好", riskScore, $"成长相对主板 {growthStrength:F2}%，小盘相对大盘 {smallCapStrength:F2}%，热板块 {hotSectorCount} 个。"),
            CreateCategory("capital", "资金情绪", capitalScore, BuildCapitalDescription(external)),
            CreateCategory("external", "外部压力", externalScore, BuildExternalDescription(external))
        };

        var metrics = new List<MarketSentimentMetric>
        {
            CreateMetric("turnover_bucket_amount", "半小时成交额", turnoverPace?.BucketAmount / 100_000_000m, "亿", "trading", turnoverPace is null ? "Unavailable" : "RealtimeSnapshotBucket"),
            CreateMetric("turnover_bucket_baseline", "半小时成交额同段均值", turnoverPace?.BaselineAmount / 100_000_000m, "亿", "trading", turnoverPace is null ? "Unavailable" : "HistoricalSnapshotBucket"),
            CreateMetric("turnover_bucket_pace", "半小时成交节奏", turnoverPace is null ? null : turnoverPace.PaceRatio * 100m, "%", "trading", turnoverPace is null ? "Unavailable" : "HistoricalSnapshotBucket"),
            CreateMetric("rising_ratio", "上涨家数占比", risingRatio, "%", "breadth"),
            CreateMetric("falling_ratio", "下跌家数占比", fallingRatio, "%", "breadth"),
            CreateMetric("market_universe_count", "市场股票总数", totalCount, "只", "breadth", marketUniverse is null ? "RealtimeSnapshotFallback" : marketUniverse.ProviderName),
            CreateMetric("realtime_quote_count", "实时返回股票数", realtimeCount, "只", "breadth"),
            CreateMetric("limit_up_count", "涨停家数", limitUpCount, "只", "breadth", limitSource),
            CreateMetric("limit_down_count", "跌停家数", limitDownCount, "只", "breadth", limitSource),
            CreateMetric("local_limit_up_count", "本地规则涨停家数", localLimitUpCount, "只", "breadth", "LocalLimitPriceRule"),
            CreateMetric("local_limit_down_count", "本地规则跌停家数", localLimitDownCount, "只", "breadth", "LocalLimitPriceRule"),
            CreateMetric("big_rise_count", "大涨5%家数", bigRiseCount, "只", "short-term"),
            CreateMetric("big_fall_count", "大跌5%家数", bigFallCount, "只", "short-term"),
            CreateMetric("total_amount", "两市成交额", totalAmount / 100_000_000m, "亿", "trading"),
            CreateMetric("average_turnover", "平均换手率", averageTurnover, "%", "trading"),
            CreateMetric("amount_vs_20d_average", "成交额相对20日均值", amountAverage is null ? null : amountAverage.CurrentAmount / Math.Max(amountAverage.Average20DayAmount, 1m) * 100m, "%", "trading", amountAverage is null ? "Unavailable" : "DailyKLine"),
            CreateMetric("growth_minus_main", "成长相对主板", growthStrength, "%", "risk"),
            CreateMetric("small_minus_large", "小盘相对大盘", smallCapStrength, "%", "risk"),
            CreateMetric("hot_sector_count", "高热板块数", hotSectorCount, "个", "risk", sectorSnapshot is null ? "Unavailable" : "SectorHeat"),
            CreateMetric("top_sector_heat", topSector is null ? "最热板块热度" : $"最热板块：{topSector.SectorName}", topSector?.HeatScore, "", "risk", sectorSnapshot is null ? "Unavailable" : "SectorHeat"),
            CreateMetric("hot_concept_count", "高热概念数", hotConceptCount, "个", "risk", conceptSnapshot is null ? "Unavailable" : "ConceptHeat"),
            CreateMetric("top_concept_heat", topConcept is null ? "最热概念热度" : $"最热概念：{topConcept.ConceptName}", topConcept?.HeatScore, "", "risk", conceptSnapshot is null ? "Unavailable" : "ConceptHeat"),
            CreateMetric("strong_pool_return_all", "机会池平均收益", opportunityBreakdown.All, "%", "short-term", opportunityBreakdown.All.HasValue ? "OpportunityPool" : "Unavailable"),
            CreateMetric("strong_pool_return_focused", "重点池平均收益", opportunityBreakdown.Focused, "%", "short-term", opportunityBreakdown.Focused.HasValue ? "OpportunityPool" : "Unavailable"),
            CreateMetric("strong_pool_return_candidate", "候选池平均收益", opportunityBreakdown.Candidate, "%", "short-term", opportunityBreakdown.Candidate.HasValue ? "OpportunityPool" : "Unavailable"),
            CreateMetric("strong_pool_return_watch", "观察池平均收益", opportunityBreakdown.Watch, "%", "short-term", opportunityBreakdown.Watch.HasValue ? "OpportunityPool" : "Unavailable"),
            CreateMetric("financing_balance_change", "融资余额变化", external.FinancingBalanceChange, "亿", "capital", MetricStatus(external.FinancingBalanceChange)),
            CreateMetric("etf_net_subscription", "ETF净申购", external.EtfNetSubscription, "亿", "capital", MetricStatus(external.EtfNetSubscription)),
            CreateMetric("northbound_net_flow", "北向资金净流入", external.NorthboundNetFlow, "亿", "capital", MetricStatus(external.NorthboundNetFlow)),
            CreateMetric("index_future_basis", "股指期货基差", external.IndexFutureBasis, "点", "external", MetricStatus(external.IndexFutureBasis)),
            CreateMetric("option_pcr", "Option PCR", external.OptionPcr, "", "external", MetricStatus(external.OptionPcr))
        };

        return new MarketSentimentSnapshot(
            snapshot.SnapshotTime,
            snapshot.ProviderName,
            decimal.Round(temperature, 1),
            level,
            $"情绪{level}，上涨 {risingCount}/{totalCount}，均涨 {averageChange:F2}%，涨停 {limitUpCount}，跌停 {limitDownCount}，{BuildTurnoverSummary(totalAmount, turnoverPace)}",
            totalCount >= 500 ? "Realtime" : "Partial",
            categories,
            metrics,
            warnings);
    }

    private SectorHeatSnapshot? BuildSectorHeat(MarketSnapshot snapshot, List<string> warnings)
    {
        try
        {
            return _sectorHeatService.Build(snapshot);
        }
        catch (Exception ex)
        {
            warnings.Add($"Sector heat calculation failed: {ex.Message}");
            return null;
        }
    }

    private ConceptHeatSnapshot? BuildConceptHeat(MarketSnapshot snapshot, List<string> warnings)
    {
        try
        {
            return _sectorHeatService.BuildConcepts(snapshot);
        }
        catch (Exception ex)
        {
            warnings.Add($"Concept heat calculation failed: {ex.Message}");
            return null;
        }
    }

    private async Task<AmountAverageSample?> BuildAmountAverageSampleAsync(
        IReadOnlyList<StockQuote> quotes,
        CancellationToken cancellationToken)
    {
        var candidates = quotes
            .Where(item => item.Amount > 0)
            .OrderByDescending(item => item.Amount)
            .Take(40)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        decimal currentAmount = 0m;
        decimal historicalAmount = 0m;
        var sampleCount = 0;
        foreach (var quote in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<KLineBar> bars;
            try
            {
                bars = await _kLineDataProvider.LoadKLineAsync(quote.Symbol, "Daily", 20, cancellationToken);
            }
            catch
            {
                continue;
            }

            var amounts = bars
                .Where(item => item.Close > 0 && item.Volume > 0)
                .Select(item => item.Close * item.Volume)
                .ToArray();
            if (amounts.Length == 0)
            {
                continue;
            }

            currentAmount += quote.Amount;
            historicalAmount += amounts.Average();
            sampleCount++;
        }

        return sampleCount == 0 || historicalAmount <= 0
            ? null
            : new AmountAverageSample(currentAmount, historicalAmount);
    }

    private StrongPoolReturnBreakdown BuildOpportunityBreakdown(IReadOnlyDictionary<string, StockQuote> quoteBySymbol)
    {
        var opportunities = _opportunityAppService.QueryOpportunities("Current")
            .Where(item => quoteBySymbol.ContainsKey(item.Symbol))
            .ToArray();
        if (opportunities.Length == 0)
        {
            return new StrongPoolReturnBreakdown(null, null, null, null);
        }

        decimal? Average(IEnumerable<Opportunity> source)
        {
            var values = source
                .Select(item => quoteBySymbol.TryGetValue(item.Symbol, out var quote) ? quote.ChangePercent : (decimal?)null)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .ToArray();
            return values.Length == 0 ? null : values.Average();
        }

        return new StrongPoolReturnBreakdown(
            Average(opportunities),
            Average(opportunities.Where(item => item.Status == OpportunityStatus.Focused || item.ManualTag == "Focus")),
            Average(opportunities.Where(item => item.Status is OpportunityStatus.Candidate or OpportunityStatus.Strengthened or OpportunityStatus.ReHit)),
            Average(opportunities.Where(item => item.Status is OpportunityStatus.Watch or OpportunityStatus.Weakened or OpportunityStatus.New or OpportunityStatus.Continued)));
    }

    private static decimal BuildCapitalScore(MarketSentimentExternalSnapshot external)
    {
        var score = 50m;
        score += ScaleNullable(external.FinancingBalanceChange, 15m, 12m);
        score += ScaleNullable(external.EtfNetSubscription, 30m, 12m);
        score += ScaleNullable(external.NorthboundNetFlow, 50m, 14m);
        return ClampScore(score);
    }

    private static decimal BuildExternalPressureScore(MarketSentimentExternalSnapshot external)
    {
        var score = 50m;
        score += ScaleNullable(external.IndexFutureBasis, 20m, 15m);
        if (external.OptionPcr.HasValue)
        {
            score += Math.Clamp((1.05m - external.OptionPcr.Value) * 35m, -15m, 15m);
        }

        return ClampScore(score);
    }

    private TurnoverPaceSample? BuildTurnoverPaceSample(DateTimeOffset snapshotTime, decimal currentTotalAmount)
    {
        var localTime = snapshotTime.LocalDateTime;
        var bucket = GetLatestCompletedTurnoverBucket(localTime.TimeOfDay);
        if (bucket is null)
        {
            return null;
        }

        var observations = _store.Query(null, 10000)
            .Select(TryCreateTurnoverObservation)
            .Where(item => item is not null)
            .Select(item => item!)
            .Append(new TurnoverObservation(localTime, currentTotalAmount))
            .Where(item => item.Time.Date <= localTime.Date)
            .OrderBy(item => item.Time)
            .ToArray();
        if (observations.Length == 0)
        {
            return null;
        }

        var currentBucketAmount = CalculateBucketAmount(observations, localTime.Date, bucket.Value);
        if (!currentBucketAmount.HasValue || currentBucketAmount.Value <= 0)
        {
            return null;
        }

        var historicalAmounts = observations
            .Select(item => item.Time.Date)
            .Where(date => date < localTime.Date)
            .Distinct()
            .OrderByDescending(date => date)
            .Select(date => CalculateBucketAmount(observations, date, bucket.Value))
            .Where(item => item.HasValue && item.Value > 0)
            .Select(item => item!.Value)
            .Take(20)
            .ToArray();
        if (historicalAmounts.Length < 3)
        {
            return null;
        }

        var baseline = TrimmedAverage(historicalAmounts);
        if (baseline <= 0)
        {
            return null;
        }

        var ratio = currentBucketAmount.Value / baseline;
        return new TurnoverPaceSample(
            $"{bucket.Value.Start:hh\\:mm}-{bucket.Value.End:hh\\:mm}",
            currentBucketAmount.Value,
            baseline,
            ratio,
            historicalAmounts.Length);
    }

    private static TurnoverObservation? TryCreateTurnoverObservation(MarketSentimentSnapshot snapshot)
    {
        var totalAmount = snapshot.Metrics.FirstOrDefault(item => item.Code == "total_amount")?.Value;
        return totalAmount.HasValue
            ? new TurnoverObservation(snapshot.SnapshotTime.LocalDateTime, totalAmount.Value * 100_000_000m)
            : null;
    }

    private static decimal? CalculateBucketAmount(
        IReadOnlyList<TurnoverObservation> observations,
        DateTime date,
        TurnoverBucket bucket)
    {
        var daily = observations
            .Where(item => item.Time.Date == date)
            .OrderBy(item => item.Time)
            .ToArray();
        if (daily.Length == 0)
        {
            return null;
        }

        var endTime = date.Date + bucket.End;
        var startTime = date.Date + bucket.Start;
        if (bucket.Start == new TimeSpan(13, 0, 0))
        {
            startTime = date.Date + new TimeSpan(11, 30, 0);
        }

        var endObservation = daily
            .Where(item => item.Time <= endTime)
            .OrderByDescending(item => item.Time)
            .FirstOrDefault();
        if (endObservation is null)
        {
            return null;
        }

        var startAmount = bucket.Start == new TimeSpan(9, 30, 0)
            ? 0m
            : daily
                .Where(item => item.Time <= startTime)
                .OrderByDescending(item => item.Time)
                .Select(item => (decimal?)item.Amount)
                .FirstOrDefault();
        if (bucket.Start != new TimeSpan(9, 30, 0) && !startAmount.HasValue)
        {
            return null;
        }

        var amount = endObservation.Amount - (startAmount ?? 0m);
        return amount > 0 ? amount : null;
    }

    private static TurnoverBucket? GetLatestCompletedTurnoverBucket(TimeSpan current)
    {
        TurnoverBucket[] buckets =
        [
            new(new TimeSpan(9, 30, 0), new TimeSpan(10, 0, 0)),
            new(new TimeSpan(10, 0, 0), new TimeSpan(10, 30, 0)),
            new(new TimeSpan(10, 30, 0), new TimeSpan(11, 0, 0)),
            new(new TimeSpan(11, 0, 0), new TimeSpan(11, 30, 0)),
            new(new TimeSpan(13, 0, 0), new TimeSpan(13, 30, 0)),
            new(new TimeSpan(13, 30, 0), new TimeSpan(14, 0, 0)),
            new(new TimeSpan(14, 0, 0), new TimeSpan(14, 30, 0)),
            new(new TimeSpan(14, 30, 0), new TimeSpan(15, 0, 0))
        ];

        return buckets
            .Where(item => item.End <= current)
            .OrderByDescending(item => item.End)
            .FirstOrDefault();
    }

    private static decimal TrimmedAverage(IReadOnlyList<decimal> values)
    {
        var ordered = values.OrderBy(item => item).ToArray();
        var trimCount = ordered.Length >= 10 ? Math.Min(2, ordered.Length / 5) : 0;
        var trimmed = ordered
            .Skip(trimCount)
            .Take(ordered.Length - trimCount * 2)
            .ToArray();
        return trimmed.Length == 0 ? 0m : trimmed.Average();
    }

    private static decimal ScoreTurnoverPace(decimal ratio)
    {
        return ClampScore(50m + (ratio - 1m) * 100m);
    }

    private static string BuildTurnoverSummary(decimal totalAmount, TurnoverPaceSample? sample)
    {
        if (sample is null)
        {
            return $"成交额 {totalAmount / 100_000_000m:F1} 亿。";
        }

        var direction = sample.PaceRatio >= 1m ? "放量" : "缩量";
        return $"成交节奏{direction} {sample.PaceRatio:F2}x（{sample.BucketLabel}），成交额 {totalAmount / 100_000_000m:F1} 亿。";
    }

    private static string BuildTradingDescription(decimal totalAmount, decimal averageTurnover, AmountAverageSample? sample, TurnoverPaceSample? turnoverPace)
    {
        if (turnoverPace is not null)
        {
            var direction = turnoverPace.PaceRatio >= 1m ? "放量" : "缩量";
            return $"成交节奏：{turnoverPace.BucketLabel} {direction} {turnoverPace.PaceRatio:F2}x，半小时成交额 {turnoverPace.BucketAmount / 100_000_000m:F1} 亿 / 同段均值 {turnoverPace.BaselineAmount / 100_000_000m:F1} 亿，平均换手 {averageTurnover:F2}%。";
        }

        if (sample is null)
        {
            return $"成交额 {totalAmount / 100_000_000m:F1} 亿，平均换手 {averageTurnover:F2}%。";
        }

        var ratio = sample.CurrentAmount / Math.Max(sample.Average20DayAmount, 1m) * 100m;
        return $"成交额 {totalAmount / 100_000_000m:F1} 亿，平均换手 {averageTurnover:F2}%，样本成交额为 20 日均值 {ratio:F1}%。";
    }

    private static string BuildCapitalDescription(MarketSentimentExternalSnapshot external)
    {
        return external.AvailableMetricCount == 0
            ? "融资、ETF、北向资金暂未接入，资金情绪按中性计分。"
            : $"融资变化 {FormatNullableAmount(external.FinancingBalanceChange)}，ETF 净申购 {FormatNullableAmount(external.EtfNetSubscription)}，北向净流入 {FormatNullableAmount(external.NorthboundNetFlow)}。";
    }

    private static string BuildExternalDescription(MarketSentimentExternalSnapshot external)
    {
        return external.IndexFutureBasis.HasValue || external.OptionPcr.HasValue
            ? $"期指基差 {FormatNullableValue(external.IndexFutureBasis, "点")}，期权PCR {FormatNullableValue(external.OptionPcr, "")}。"
            : "股指期货基差、期权PCR 暂未接入真实数据。";
    }

    private static MarketSentimentCategory CreateCategory(string code, string name, decimal score, string description)
    {
        return new MarketSentimentCategory(code, name, decimal.Round(score, 1), GetLevel(score), description);
    }

    private static MarketSentimentMetric CreateMetric(string code, string name, decimal? value, string unit, string categoryCode, string sourceStatus = "Realtime")
    {
        var rounded = value.HasValue ? decimal.Round(value.Value, UnitDecimalPlaces(unit)) : (decimal?)null;
        return new MarketSentimentMetric(
            code,
            name,
            rounded,
            rounded.HasValue ? FormatMetricDisplay(rounded.Value, unit) : "--",
            unit,
            categoryCode,
            rounded.HasValue,
            sourceStatus);
    }

    private static int UnitDecimalPlaces(string unit)
    {
        return unit is "只" or "个" ? 0 : 2;
    }

    private static string FormatMetricDisplay(decimal value, string unit)
    {
        if (unit == "%")
        {
            return $"{value:F2}%";
        }

        if (unit is "只" or "个")
        {
            return $"{value:F0}{unit}";
        }

        return string.IsNullOrWhiteSpace(unit) ? $"{value:F2}" : $"{value:F2}{unit}";
    }

    private static decimal AverageChange(IEnumerable<StockQuote> quotes, Func<StockQuote, bool> predicate)
    {
        var items = quotes.Where(predicate).ToArray();
        return items.Length == 0 ? 0m : items.Average(item => item.ChangePercent);
    }

    private static bool IsGrowthBoard(StockQuote quote)
    {
        return quote.Symbol.StartsWith("300", StringComparison.Ordinal) ||
               quote.Symbol.StartsWith("301", StringComparison.Ordinal) ||
               quote.Symbol.StartsWith("688", StringComparison.Ordinal);
    }

    private static bool IsMainBoard(StockQuote quote)
    {
        return quote.Symbol.StartsWith('6') || quote.Symbol.StartsWith("00", StringComparison.Ordinal);
    }

    private static decimal ScorePercent(decimal value, decimal low, decimal high)
    {
        return high <= low ? 50m : ClampScore((value - low) * 100m / (high - low));
    }

    private static decimal ScaleNullable(decimal? value, decimal normalizer, decimal maxContribution)
    {
        return value.HasValue
            ? Math.Clamp(value.Value / normalizer * maxContribution, -maxContribution, maxContribution)
            : 0m;
    }

    private static decimal ClampScore(decimal score)
    {
        return Math.Clamp(score, 0m, 100m);
    }

    private static string GetLevel(decimal score)
    {
        if (score >= 80m)
        {
            return "过热";
        }

        if (score >= 65m)
        {
            return "偏热";
        }

        if (score >= 45m)
        {
            return "中性";
        }

        if (score >= 30m)
        {
            return "偏冷";
        }

        return "冰点";
    }

    private static string MetricStatus(decimal? value)
    {
        return value.HasValue ? "ExternalSentimentData" : "Unavailable";
    }

    private static string FormatNullablePercent(decimal? value)
    {
        return value.HasValue ? $"{value.Value:F2}%" : "--";
    }

    private static string FormatNullableAmount(decimal? value)
    {
        return FormatNullableValue(value, "亿");
    }

    private static string FormatNullableValue(decimal? value, string unit)
    {
        return value.HasValue ? $"{value.Value:F2}{unit}" : "--";
    }
}

public sealed record AmountAverageSample(decimal CurrentAmount, decimal Average20DayAmount);

public sealed record TurnoverPaceSample(
    string BucketLabel,
    decimal BucketAmount,
    decimal BaselineAmount,
    decimal PaceRatio,
    int BaselineSampleCount);

public sealed record TurnoverObservation(DateTime Time, decimal Amount);

public readonly record struct TurnoverBucket(TimeSpan Start, TimeSpan End);

public sealed record StrongPoolReturnBreakdown(
    decimal? All,
    decimal? Focused,
    decimal? Candidate,
    decimal? Watch);

public sealed record MarketSentimentSnapshot(
    DateTimeOffset SnapshotTime,
    string ProviderName,
    decimal TemperatureScore,
    string Level,
    string Summary,
    string DataQuality,
    IReadOnlyList<MarketSentimentCategory> Categories,
    IReadOnlyList<MarketSentimentMetric> Metrics,
    IReadOnlyList<string> Warnings);

public sealed record MarketSentimentCategory(
    string Code,
    string Name,
    decimal Score,
    string Status,
    string Description);

public sealed record MarketSentimentMetric(
    string Code,
    string Name,
    decimal? Value,
    string DisplayValue,
    string Unit,
    string CategoryCode,
    bool IsAvailable,
    string SourceStatus = "Realtime");
