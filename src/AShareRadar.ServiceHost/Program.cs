using System.Diagnostics;
using AShareRadar.Application.Backtesting;
using AShareRadar.Application.History;
using AShareRadar.Application.Indicators;
using AShareRadar.Application.Monitoring;
using AShareRadar.Application.MarketData;
using AShareRadar.Application.Opportunities;
using AShareRadar.Application.Realtime;
using AShareRadar.Application.Review;
using AShareRadar.Application.Strategies;
using AShareRadar.Application.StrategyTraining;
using AShareRadar.Application.Qlib;
using AShareRadar.Contracts.Backtesting;
using AShareRadar.Contracts.Monitoring;
using AShareRadar.Contracts.History;
using AShareRadar.Contracts.MarketData;
using AShareRadar.Contracts.Opportunities;
using AShareRadar.Contracts.Review;
using AShareRadar.Contracts.Qlib;
using AShareRadar.Contracts.Strategies;
using AShareRadar.Contracts.StrategyTraining;
using AShareRadar.Infrastructure.MarketData;
using AShareRadar.Application.Opportunities.Storage;
using AShareRadar.Persistence.Database;
using AShareRadar.Persistence.History;
using AShareRadar.Persistence.MarketData;
using AShareRadar.Persistence.Opportunities;
using AShareRadar.Persistence.Qlib;
using AShareRadar.Persistence.Review;
using AShareRadar.Persistence.StrategyTraining;
using AShareRadar.ServiceHost.Hubs;
using AShareRadar.ServiceHost.Realtime;
using AShareRadar.ServiceHost.Workers;
using AShareRadar.Strategies.Intraday;
using AShareRadar.Strategies.Qlib;
using AShareRadar.Strategies.Registry;
using DuckDB.NET.Data;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddSingleton<MonitorRuntimeState>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("MarketSentimentStrategy")
        .Get<MarketSentimentStrategyOptions>() ?? new MarketSentimentStrategyOptions());
builder.Services.AddSingleton(
    builder.Configuration.GetSection("MarketSentimentExternalData")
        .Get<MarketSentimentExternalDataOptions>() ?? new MarketSentimentExternalDataOptions());
builder.Services.AddSingleton(
    builder.Configuration.GetSection("TradingCalendar")
        .Get<TradingCalendarOptions>() ?? new TradingCalendarOptions());
builder.Services.AddSingleton(
    builder.Configuration.GetSection("TradingSession")
        .Get<TradingSessionOptions>() ?? new TradingSessionOptions());
builder.Services.AddSingleton(
    builder.Configuration.GetSection("ExternalSentimentAutoUpdate")
        .Get<ExternalSentimentAutoUpdateOptions>() ?? new ExternalSentimentAutoUpdateOptions());
var databaseOptions = builder.Configuration
    .GetSection("Database")
    .Get<DatabaseOptions>() ?? new DatabaseOptions();
builder.Services.AddSingleton(databaseOptions);
builder.Services.AddSingleton<SqliteDatabase>();
builder.Services.AddSingleton<SqliteOpportunityStateStore>();
builder.Services.AddSingleton<IHistoryQueryService, SqliteHistoryQueryService>();
builder.Services.AddSingleton<IMarketSentimentStore, SqliteMarketSentimentStore>();
builder.Services.AddSingleton<SqliteStrategyTrainingStore>();
builder.Services.AddSingleton<IStrategyTrainingStore>(services => services.GetRequiredService<SqliteStrategyTrainingStore>());
builder.Services.AddSingleton<IStrategyParameterProfileStore>(services => services.GetRequiredService<SqliteStrategyTrainingStore>());
builder.Services.AddSingleton<IMarketSentimentExternalDataProvider, ConfiguredMarketSentimentExternalDataProvider>();
builder.Services.AddSingleton<ILimitPoolProvider>(services =>
{
    var options = services.GetRequiredService<MarketDataOptions>();
    var client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AShareRadar/0.1");
    return new EastMoneyLimitPoolProvider(client);
});
builder.Services.AddSingleton<ExternalSentimentCsvStore>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("ExternalSentimentSdkUpdate")
        .Get<ExternalSentimentSdkUpdateOptions>() ?? new ExternalSentimentSdkUpdateOptions());
builder.Services.AddSingleton<ExternalSentimentSdkUpdateService>();
builder.Services.AddSingleton<TradingCalendarService>();
builder.Services.AddSingleton<TradingSessionService>();
builder.Services.AddSingleton<IPredictionReviewStore, SqlitePredictionReviewStore>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("QlibNextDayPrediction")
        .Get<QlibNextDayPredictionOptions>() ?? new QlibNextDayPredictionOptions());
builder.Services.AddSingleton<QlibTomorrowPredictionCsvReader>();
builder.Services.AddSingleton<QlibNextDayPredictionRunner>();
builder.Services.AddSingleton<JsonOpportunityStateStore>();
builder.Services.AddSingleton<IOpportunityStateStore>(services =>
{
    var options = services.GetRequiredService<DatabaseOptions>();
    return string.Equals(options.StateStore, "Json", StringComparison.OrdinalIgnoreCase)
        ? services.GetRequiredService<JsonOpportunityStateStore>()
        : services.GetRequiredService<SqliteOpportunityStateStore>();
});
builder.Services.AddSingleton<MonitorAppService>();
builder.Services.AddSingleton<OpportunityAppService>();
builder.Services.AddSingleton<ReviewAppService>();
builder.Services.AddSingleton<PredictionReviewService>();
builder.Services.AddSingleton(builder.Configuration.GetSection("QlibSignals").Get<QlibSignalOptions>() ?? new QlibSignalOptions());
builder.Services.AddSingleton<QlibSignalFileReader>();
builder.Services.AddSingleton<IQlibSignalSeedStore, SqliteQlibSignalSeedStore>();
builder.Services.AddSingleton<QlibSignalSyncService>();
builder.Services.AddSingleton<BacktestReplayService>();
builder.Services.AddSingleton<ScanOrchestrator>();
builder.Services.AddSingleton<IIndicatorCalculator, IndicatorCalculator>();
builder.Services.AddSingleton<ISectorHeatService, SnapshotSectorHeatService>();
builder.Services.AddSingleton<MarketSentimentService>();
builder.Services.AddSingleton<StrategyTrainingService>();
builder.Services.AddSingleton<StrategyParameterProfileService>();
builder.Services.AddSingleton<DailyLimitUpExclusionService>();

var marketDataOptions = builder.Configuration
    .GetSection("MarketData")
    .Get<MarketDataOptions>() ?? new MarketDataOptions();
marketDataOptions.SeedSymbols = marketDataOptions.SeedSymbols
    .Where(item => !string.IsNullOrWhiteSpace(item))
    .Select(item => item.Trim())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
builder.Services.AddSingleton(marketDataOptions);
builder.Services.AddSingleton(
    builder.Configuration.GetSection("EastMoneyQuant")
        .Get<EastMoneyQuantOptions>() ?? new EastMoneyQuantOptions());
builder.Services.AddSingleton(
    builder.Configuration.GetSection("EastMoneyQuantDotNet")
        .Get<EastMoneyQuantDotNetOptions>() ?? new EastMoneyQuantDotNetOptions());
builder.Services.AddSingleton<EastMoneyQuantDotNetClient>();
builder.Services.AddSingleton<IMarketUniverseProvider, EastMoneyQuantUniverseProvider>();
builder.Services.AddSingleton<EastMoneyQuantDotNetRealtimeProvider>();
builder.Services.AddSingleton<EastMoneyQuantRealtimeProvider>();
builder.Services.AddSingleton(services =>
{
    var options = services.GetRequiredService<MarketDataOptions>();
    var client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AShareRadar/0.1");
    return new TencentRealtimeProvider(
        client,
        options,
        services.GetRequiredService<IHistoricalSymbolProvider>());
});
builder.Services.AddSingleton(services =>
{
    var options = services.GetRequiredService<MarketDataOptions>();
    var client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AShareRadar/0.1");
    return new SinaRealtimeProvider(
        client,
        options,
        services.GetRequiredService<IHistoricalSymbolProvider>());
});
builder.Services.AddSingleton<SimulatedMarketDataProvider>();
builder.Services.AddSingleton<SimulatedKLineDataProvider>();
builder.Services.AddSingleton<EastMoneyQuantDotNetKLineDataProvider>();
builder.Services.AddSingleton<EastMoneyQuantKLineDataProvider>();
builder.Services.AddSingleton(services =>
{
    var client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(services.GetRequiredService<MarketDataOptions>().RequestTimeoutSeconds)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AShareRadar/0.1");
    return new TencentKLineDataProvider(client);
});
builder.Services.AddSingleton<DuckDbKLineDataProvider>(services =>
{
    var options = services.GetRequiredService<DatabaseOptions>();
    return new DuckDbKLineDataProvider(
        options.DuckDbPath,
        services.GetRequiredService<SimulatedKLineDataProvider>());
});
builder.Services.AddSingleton<DuckDbMinuteKLineCacheProvider>(services =>
{
    var options = services.GetRequiredService<DatabaseOptions>();
    return new DuckDbMinuteKLineCacheProvider(options.DuckDbPath);
});
builder.Services.AddSingleton<IKLineDataProvider>(services => new HybridKLineDataProvider(
    services.GetRequiredService<DuckDbMinuteKLineCacheProvider>(),
    services.GetRequiredService<EastMoneyQuantDotNetKLineDataProvider>(),
    services.GetRequiredService<EastMoneyQuantKLineDataProvider>(),
    services.GetRequiredService<TencentKLineDataProvider>(),
    services.GetRequiredService<DuckDbKLineDataProvider>()));
builder.Services.AddSingleton<IHistoricalSymbolProvider>(services => services.GetRequiredService<DuckDbKLineDataProvider>());
builder.Services.AddSingleton<IIntradayKLineOverlayService, IntradayKLineOverlayService>();
builder.Services.AddSingleton<IMarketDataProvider>(services =>
{
    var options = services.GetRequiredService<MarketDataOptions>();
    CompositeMarketDataProvider CreateCompositeProvider() => new(
        [
            services.GetRequiredService<EastMoneyQuantDotNetRealtimeProvider>(),
            services.GetRequiredService<EastMoneyQuantRealtimeProvider>(),
            services.GetRequiredService<TencentRealtimeProvider>(),
            services.GetRequiredService<SinaRealtimeProvider>(),
            services.GetRequiredService<SimulatedMarketDataProvider>()
        ]);

    return options.Provider switch
    {
        "EastMoneyQuantDotNet" => CreateCompositeProvider(),
        "EastMoneyQuant" => CreateCompositeProvider(),
        "Tencent" => services.GetRequiredService<TencentRealtimeProvider>(),
        "Sina" => services.GetRequiredService<SinaRealtimeProvider>(),
        "Composite" => CreateCompositeProvider(),
        _ => services.GetRequiredService<SimulatedMarketDataProvider>()
    };
});
builder.Services.AddSingleton<ISignalStrategy, PlatformVolumeBreakoutStrategy>();
builder.Services.AddSingleton<ISignalStrategy, MainSectorResonanceStrategy>();
builder.Services.AddSingleton<ISignalStrategy>(_ => MainSectorResonanceStrategy.CreateGapRecovery());
builder.Services.AddSingleton<ISignalStrategy, MovingAveragePullbackRestartStrategy>();
builder.Services.AddSingleton<ISignalStrategy, LongSupportReboundStrategy>();
builder.Services.AddSingleton<ISignalStrategy, StrongTrendContinuationStrategy>();
builder.Services.AddSingleton<ISignalStrategy, CounterTrendStrengthStrategy>();
builder.Services.AddSingleton<ISignalStrategy, StrongRepairReboundStrategy>();
builder.Services.AddSingleton<ISignalStrategy, DreamerDaAStrategy>();
builder.Services.AddSingleton<ISignalStrategy, ZhongheYingtaiMainriseStrategy>();
builder.Services.AddSingleton<ISignalStrategy, QlibR013SignalStrategy>();
builder.Services.AddSingleton<IStrategyRegistry, StrategyRegistry>();
builder.Services.AddSingleton<IRealtimeEventPublisher, SignalRRealtimeEventPublisher>();
var historicalDataUpdateOptions = builder.Configuration
    .GetSection("HistoricalDataUpdate")
    .Get<HistoricalDataUpdateOptions>() ?? new HistoricalDataUpdateOptions();
builder.Services.AddSingleton(historicalDataUpdateOptions);
builder.Services.AddSingleton<HistoricalDataUpdateService>();
var marketMappingUpdateOptions = builder.Configuration
    .GetSection("MarketMappingUpdate")
    .Get<MarketMappingUpdateOptions>() ?? new MarketMappingUpdateOptions();
builder.Services.AddSingleton(marketMappingUpdateOptions);
builder.Services.AddSingleton<MarketMappingUpdateService>();
var historicalStrategyScanOptions = builder.Configuration
    .GetSection("HistoricalStrategyScan")
    .Get<HistoricalStrategyScanOptions>() ?? new HistoricalStrategyScanOptions();
builder.Services.AddSingleton(historicalStrategyScanOptions);
builder.Services.AddSingleton<HistoricalStrategyScanService>();
var marketSentimentWorkerOptions = builder.Configuration
    .GetSection("MarketSentimentWorker")
    .Get<MarketSentimentWorkerOptions>() ?? new MarketSentimentWorkerOptions();
builder.Services.AddSingleton(marketSentimentWorkerOptions);
var minuteKLineCacheWorkerOptions = builder.Configuration
    .GetSection("MinuteKLineCacheWorker")
    .Get<MinuteKLineCacheWorkerOptions>() ?? new MinuteKLineCacheWorkerOptions();
builder.Services.AddSingleton(minuteKLineCacheWorkerOptions);
builder.Services.AddSingleton<MarketSentimentRuntimeState>();

builder.Services.AddSignalR();
builder.Services.AddHostedService<MonitorWorker>();
builder.Services.AddHostedService<HistoricalDataUpdateWorker>();
builder.Services.AddHostedService<HistoricalStrategyScanWorker>();
builder.Services.AddHostedService<ExternalSentimentAutoUpdateWorker>();
builder.Services.AddHostedService<MarketSentimentWorker>();
builder.Services.AddHostedService<MinuteKLineCacheWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/api/monitor/status"));

app.MapGet("/api/monitor/status", (MonitorAppService monitorAppService) =>
{
    var status = monitorAppService.GetStatus();
    return new MonitorStatusDto(
        status.MarketStatus,
        status.MonitorStatus,
        status.LastScanTime,
        status.NextScanTime,
        status.ActiveOpportunityCount,
        status.TodayNewCount,
        status.DisappearedCount,
        status.FocusedCount,
        status.HistoricalStrategyScanStatus,
        status.LastHistoricalStrategyScanTime,
        status.NextHistoricalStrategyScanTime,
        status.HistoricalStrategyScanSymbolCount,
        status.HistoricalStrategyScanSignalCount);
});

app.MapPost("/api/monitor/start", (StartMonitorRequest request, MonitorAppService monitorAppService) =>
{
    var status = monitorAppService.Start(request.ScanIntervalSeconds);
    return Results.Accepted("/api/monitor/status", status);
});

app.MapPost("/api/monitor/pause", (MonitorAppService monitorAppService) =>
{
    var status = monitorAppService.Pause();
    return Results.Accepted("/api/monitor/status", status);
});

app.MapPost("/api/monitor/scan-once", async (ScanOrchestrator scanOrchestrator, CancellationToken cancellationToken) =>
{
    await scanOrchestrator.RunOnceAsync(cancellationToken);
    return Results.Accepted("/api/monitor/status");
});

app.MapGet("/api/market-data/status", (
    MarketDataOptions options,
    IMarketDataProvider marketDataProvider) =>
{
    return new MarketDataStatusDto(
        options.Provider,
        marketDataProvider.ProviderName,
        options.Universe,
        options.StockPool,
        options.MaxSymbols,
        options.RequestBatchSize,
        options.RequestConcurrency,
        options.SeedSymbols,
        options.RequestTimeoutSeconds);
});

app.MapGet("/api/market-data/snapshot", async (
    IMarketDataProvider marketDataProvider,
    CancellationToken cancellationToken) =>
{
    var stopwatch = Stopwatch.StartNew();
    var snapshot = await marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
    stopwatch.Stop();

    return new MarketSnapshotDto(
        snapshot.SnapshotTime,
        snapshot.ProviderName,
        stopwatch.ElapsedMilliseconds,
        snapshot.Quotes.Select(MapStockQuote).ToArray());
});

app.MapGet("/api/market-data/sectors", async (
    IMarketDataProvider marketDataProvider,
    ISectorHeatService sectorHeatService,
    int? count,
    CancellationToken cancellationToken) =>
{
    var snapshot = await marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
    var sectorHeatSnapshot = sectorHeatService.Build(snapshot);
    var takeCount = Math.Clamp(count ?? 20, 1, 80);

    return sectorHeatSnapshot.SectorsByCode.Values
        .OrderByDescending(item => item.HeatScore)
        .ThenByDescending(item => item.TotalAmount)
        .Take(takeCount)
        .Select(item => new HeatBoardItemDto(
            item.SectorCode,
            item.SectorName,
            item.StockCount,
            item.RisingCount,
            item.AverageChangePercent,
            item.RisingRatioPercent,
            item.TotalAmount,
            item.HeatScore,
            item.Leaders.Select(MapHeatLeader).ToArray(),
            item.LeaderSymbols))
        .ToArray();
});

app.MapGet("/api/market-data/sector-mapping/status", (ISectorHeatService sectorHeatService) =>
{
    return sectorHeatService.GetMappingStatus();
});

app.MapGet("/api/market-data/concepts", async (
    IMarketDataProvider marketDataProvider,
    ISectorHeatService sectorHeatService,
    int? count,
    CancellationToken cancellationToken) =>
{
    var snapshot = await marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
    var conceptHeatSnapshot = sectorHeatService.BuildConcepts(snapshot);
    var takeCount = Math.Clamp(count ?? 20, 1, 80);

    return conceptHeatSnapshot.ConceptsByCode.Values
        .OrderByDescending(item => item.HeatScore)
        .ThenByDescending(item => item.TotalAmount)
        .Take(takeCount)
        .Select(item => new HeatBoardItemDto(
            item.ConceptCode,
            item.ConceptName,
            item.StockCount,
            item.RisingCount,
            item.AverageChangePercent,
            item.RisingRatioPercent,
            item.TotalAmount,
            item.HeatScore,
            item.Leaders.Select(MapHeatLeader).ToArray(),
            item.LeaderSymbols))
        .ToArray();
});

app.MapGet("/api/market-data/concept-mapping/status", (ISectorHeatService sectorHeatService) =>
{
    return sectorHeatService.GetConceptMappingStatus();
});

app.MapGet("/api/market-data/mapping-update/status", (MarketMappingUpdateService marketMappingUpdateService) =>
{
    return marketMappingUpdateService.GetStatus();
});

app.MapPost("/api/market-data/mapping-update/run", (MarketMappingUpdateService marketMappingUpdateService) =>
{
    _ = marketMappingUpdateService.TryStartManualUpdate();
    return Results.Accepted("/api/market-data/mapping-update/status", marketMappingUpdateService.GetStatus());
});

app.MapGet("/api/market-sentiment/snapshot", async (
    MarketSentimentService marketSentimentService,
    ExternalSentimentSdkUpdateService externalSentimentSdkUpdateService,
    bool? refresh,
    CancellationToken cancellationToken) =>
{
    if (refresh == true || marketSentimentService.GetLatestPersistedSnapshot() is null)
    {
        await externalSentimentSdkUpdateService.TryUpdateAsync(cancellationToken);
    }

    var snapshot = refresh == true
        ? await marketSentimentService.GetSnapshotAsync(cancellationToken)
        : marketSentimentService.GetLatestPersistedSnapshot()
          ?? await marketSentimentService.GetSnapshotAsync(cancellationToken);
    return MapMarketSentiment(snapshot);
});

app.MapGet("/api/market-sentiment/history", (
    MarketSentimentService marketSentimentService,
    DateOnly? tradingDate,
    int? count) =>
{
    return marketSentimentService.QueryPersistedSnapshots(tradingDate, count ?? 240)
        .Select(MapMarketSentiment)
        .ToArray();
});

app.MapGet("/api/market-sentiment/status", (
    MarketSentimentRuntimeState runtimeState) =>
{
    return new MarketSentimentStatusDto(
        runtimeState.IsEnabled,
        runtimeState.IsRunning,
        runtimeState.LastRunAt,
        runtimeState.NextRunAt,
        runtimeState.LastStatus,
        runtimeState.LastError);
});

app.MapGet("/api/market-sentiment/data-sources", (
    MarketSentimentService marketSentimentService) =>
{
    return marketSentimentService.GetDataSourceStatuses()
        .Select(MapMarketSentimentDataSourceStatus)
        .ToArray();
});

app.MapGet("/api/market-sentiment/regimes", (
    MarketSentimentService marketSentimentService,
    int? count) =>
{
    return BuildMarketSentimentRegimes(marketSentimentService.QueryPersistedSnapshots(null, count ?? 1000));
});

app.MapGet("/api/market-sentiment/strategy-rules", (
    MarketSentimentStrategyOptions options) =>
{
    return MapMarketSentimentStrategyRules(options);
});

app.MapPut("/api/market-sentiment/strategy-rules", (
    MarketSentimentStrategyRulesDto request,
    MarketSentimentStrategyOptions options) =>
{
    ApplyMarketSentimentStrategyRules(request, options);
    return MapMarketSentimentStrategyRules(options);
});

app.MapGet("/api/trading-calendar/status", (
    TradingCalendarService tradingCalendarService) =>
{
    return tradingCalendarService.GetStatus();
});

app.MapGet("/api/market-data/kline", async (
    string symbol,
    string? period,
    int? count,
    IKLineDataProvider kLineDataProvider,
    IIntradayKLineOverlayService intradayOverlayService,
    CancellationToken cancellationToken) =>
{
    var bars = await kLineDataProvider.LoadKLineAsync(
        symbol,
        period ?? "day",
        count ?? 120,
        cancellationToken);
    bars = await intradayOverlayService.AppendTemporaryDailyBarAsync(
        symbol,
        period ?? "day",
        bars,
        cancellationToken);
    return bars.Select(MapKLineBar).ToArray();
});

app.MapGet("/api/market-data/indicators", async (
    string symbol,
    string? period,
    string? type,
    int? count,
    IKLineDataProvider kLineDataProvider,
    IIntradayKLineOverlayService intradayOverlayService,
    IIndicatorCalculator indicatorCalculator,
    CancellationToken cancellationToken) =>
{
    var bars = await kLineDataProvider.LoadKLineAsync(
        symbol,
        period ?? "day",
        count ?? 120,
        cancellationToken);
    bars = await intradayOverlayService.AppendTemporaryDailyBarAsync(
        symbol,
        period ?? "day",
        bars,
        cancellationToken);
    var series = indicatorCalculator.Calculate(bars, type ?? "MACD");
    return new IndicatorSeriesDto(
        series.Type.ToString().ToUpperInvariant(),
        series.Points.Select(MapIndicatorPoint).ToArray());
});

app.MapGet("/api/opportunities", async (
    OpportunityAppService opportunityAppService,
    DailyLimitUpExclusionService limitUpExclusionService,
    IMarketDataProvider marketDataProvider,
    string? view,
    CancellationToken cancellationToken) =>
{
    var opportunities = opportunityAppService.QueryOpportunities(view);
    if (string.Equals(view, "Current", StringComparison.OrdinalIgnoreCase))
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
        opportunities = opportunities
            .Where(item => !limitUpExclusionService.IsExcluded(today, item.Symbol))
            .ToArray();
    }

    var stockNames = opportunities.Any(item => IsMissingStockName(item.Symbol, item.Name))
        ? await LoadStockNamesAsync(marketDataProvider, app.Configuration, cancellationToken)
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    return opportunities
        .GroupBy(item => StockSymbolNormalizer.NormalizeCode(item.Symbol), StringComparer.OrdinalIgnoreCase)
        .Select(group => group
            .OrderBy(item => IsMissingStockName(item.Symbol, item.Name) ? 1 : 0)
            .ThenByDescending(item => item.CurrentScore)
            .ThenByDescending(item => item.LastSeenTime)
            .First())
        .Select(item => MapOpportunity(
            item,
            stockNames,
            opportunityAppService.GetEventsForOpportunity(item.Id, 1).FirstOrDefault()))
        .ToArray();
});

app.MapGet("/api/opportunities/{id:guid}", (Guid id, OpportunityAppService opportunityAppService) =>
{
    var opportunity = opportunityAppService.GetOpportunity(id);
    if (opportunity is null)
    {
        return Results.NotFound();
    }

    var events = opportunityAppService.GetEventsForOpportunity(id, 20)
        .Select(item => MapSignalEvent(item))
        .ToArray();

    return Results.Ok(new OpportunityDetailDto(
        MapOpportunity(opportunity),
        events.FirstOrDefault(),
        events));
});

app.MapPost("/api/opportunities/{id:guid}/decision", (
    Guid id,
    DecisionRequest request,
    OpportunityAppService opportunityAppService) =>
{
    var opportunity = opportunityAppService.MarkOpportunity(id, request.DecisionType, request.Note);
    if (opportunity is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(MapOpportunity(opportunity));
});

app.MapPost("/api/maintenance/opportunities/archive-missing-events", (OpportunityAppService opportunityAppService) =>
{
    return Results.Ok(opportunityAppService.ArchiveOpportunitiesMissingEventDetails());
});

app.MapGet("/api/signals/events", async (
    OpportunityAppService opportunityAppService,
    IMarketDataProvider marketDataProvider,
    int? count,
    CancellationToken cancellationToken) =>
{
    var events = opportunityAppService.GetRecentEvents(Math.Clamp(count ?? 50, 1, 200));
    var stockNames = events.Any(item => IsMissingStockName(item.Symbol, item.Name))
        ? await LoadStockNamesAsync(marketDataProvider, app.Configuration, cancellationToken)
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    return events
        .Select(item => MapSignalEvent(item, stockNames))
        .ToArray();
});

app.MapGet("/api/history/signals", async (
    IHistoryQueryService historyQueryService,
    IMarketDataProvider marketDataProvider,
    DateOnly? tradingDate,
    string? symbol,
    string? strategyCode,
    int? count,
    CancellationToken cancellationToken) =>
{
    var signals = historyQueryService.QuerySignals(new HistoricalSignalQuery(
            tradingDate,
            symbol,
            strategyCode,
            count ?? 100));
    var stockNames = signals.Any(item => IsMissingStockName(item.Symbol, item.Name))
        ? await LoadStockNamesAsync(marketDataProvider, app.Configuration, cancellationToken)
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    return signals
        .Select(item => MapHistoricalSignal(item, stockNames))
        .ToArray();
});

app.MapGet("/api/history/strategies", (
    IHistoryQueryService historyQueryService,
    DateOnly? tradingDate,
    int? count) =>
{
    return historyQueryService.QueryStrategyPerformance(tradingDate, count ?? 50)
        .Select(MapStrategyPerformance)
        .ToArray();
});

app.MapGet("/api/history-data/status", (HistoricalDataUpdateService historicalDataUpdateService) =>
{
    return historicalDataUpdateService.GetStatus();
});

app.MapPost("/api/history-data/update-now", (HistoricalDataUpdateService historicalDataUpdateService) =>
{
    _ = historicalDataUpdateService.TryStartManualUpdate();
    return Results.Accepted("/api/history-data/status", historicalDataUpdateService.GetStatus());
});

app.MapGet("/api/qlib-signals/r013/status", (QlibSignalSyncService qlibSignalSyncService) =>
{
    return MapQlibSignalStatus(qlibSignalSyncService.GetStatus());
});

app.MapGet("/api/qlib-signals/r013/latest", (QlibSignalSyncService qlibSignalSyncService) =>
{
    return MapQlibSignalSnapshot(qlibSignalSyncService.GetLatest());
});

app.MapGet("/api/qlib-signals/r013/rebalance-plan", (QlibSignalSyncService qlibSignalSyncService) =>
{
    return MapQlibSignalSnapshot(qlibSignalSyncService.GetRebalancePlan());
});

app.MapGet("/api/qlib-signals/r013/seeds", (
    DateOnly? signalDate,
    int? count,
    QlibSignalSyncService qlibSignalSyncService) =>
{
    return qlibSignalSyncService.QuerySeeds(signalDate, count)
        .Select(MapQlibSignalSeed)
        .ToArray();
});

app.MapPost("/api/qlib-signals/r013/import-seeds", (QlibSignalSyncService qlibSignalSyncService) =>
{
    return MapQlibSignalSeedImportResult(qlibSignalSyncService.ImportLatestSeeds());
});

app.MapPost("/api/backtest/replay", async (
    BacktestReplayRequest request,
    BacktestReplayService backtestReplayService,
    CancellationToken cancellationToken) =>
{
    var result = await backtestReplayService.ReplayAsync(
        new BacktestReplayQuery(
            request.StartDate,
            request.EndDate,
            request.Symbols,
            request.StrategyCodes,
            request.LookbackDays,
            request.StockPool,
            request.MaxSymbols),
        cancellationToken);

    return new BacktestReplayResultDto(
        result.StartDate,
        result.EndDate,
        result.Symbols,
        result.StrategyCodes,
        result.StockPool,
        result.DataSourceStatus,
        result.Message,
        result.ElapsedMilliseconds,
        result.Signals.Count,
        result.StrategySummaries.Select(MapBacktestStrategySummary).ToArray(),
        result.Signals.Select(MapBacktestSignal).ToArray(),
        result.SentimentSummaries.Select(MapBacktestSentimentSummary).ToArray());
});

app.MapPost("/api/strategy-training/dataset", async (
    StrategyTrainingDatasetRequest request,
    StrategyTrainingService strategyTrainingService,
    IMarketDataProvider marketDataProvider,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var dataset = await strategyTrainingService.BuildDatasetAsync(
        new StrategyTrainingQuery(
            request.StartDate,
            request.EndDate,
            request.StrategyCode,
            request.SuccessHighReturnThreshold,
            request.RequirePositiveClose,
            ForceRebuild: request.ForceRebuild,
            ScoreThresholds: request.ScoreThresholds,
            AmountThresholds: request.AmountThresholds,
            RelativeStrengthThresholds: request.RelativeStrengthThresholds,
            HeatThresholds: request.HeatThresholds,
            OutputLimits: request.OutputLimits),
        cancellationToken);
    var stockNames = dataset.Samples.Any(item => IsMissingStockName(item.Symbol, item.Name))
        ? await LoadStockNamesAsync(marketDataProvider, configuration, cancellationToken)
        : LoadHistoricalStockNames(configuration);

    return MapStrategyTrainingDataset(dataset, stockNames);
});

app.MapPost("/api/strategy-training/run", async (
    StrategyTrainingRunRequest request,
    StrategyTrainingService strategyTrainingService,
    CancellationToken cancellationToken) =>
{
    var run = await strategyTrainingService.RunAsync(
        new StrategyTrainingQuery(
            request.StartDate,
            request.EndDate,
            request.StrategyCode,
            request.SuccessHighReturnThreshold,
            request.RequirePositiveClose,
            ForceRebuild: request.ForceRebuild,
            ScoreThresholds: request.ScoreThresholds,
            AmountThresholds: request.AmountThresholds,
            RelativeStrengthThresholds: request.RelativeStrengthThresholds,
            HeatThresholds: request.HeatThresholds,
            OutputLimits: request.OutputLimits),
        cancellationToken);

    return MapStrategyTrainingRun(run);
});

app.MapGet("/api/strategy-parameters", (
    StrategyParameterProfileService strategyParameterProfileService,
    string? strategyCode) =>
{
    return strategyParameterProfileService.GetProfiles(strategyCode)
        .Select(MapStrategyParameterProfile)
        .ToArray();
});

app.MapPost("/api/strategy-parameters", (
    SaveStrategyParameterProfileRequest request,
    StrategyParameterProfileService strategyParameterProfileService) =>
{
    var profile = strategyParameterProfileService.SaveProfile(
        new SaveStrategyParameterProfileCommand(
            request.StrategyCode,
            request.ProfileName,
            request.SourceTrainingRunId,
            request.MinScore,
            request.MinAmountYi,
            request.MinRelativeStrengthPercent,
            request.MinHeatScore,
            request.MaxOutputPerDay,
            request.SampleCount,
            request.SuccessRate,
            request.AverageNextHighReturn,
            request.AverageNextCloseReturn));
    return Results.Ok(MapStrategyParameterProfile(profile));
});

app.MapPost("/api/strategy-parameters/{id:guid}/activate", (
    Guid id,
    StrategyParameterProfileService strategyParameterProfileService) =>
{
    var profile = strategyParameterProfileService.Activate(id);
    return profile is null
        ? Results.NotFound()
        : Results.Ok(MapStrategyParameterProfile(profile));
});

app.MapPost("/api/strategy-parameters/default", (
    string strategyCode,
    StrategyParameterProfileService strategyParameterProfileService) =>
{
    strategyParameterProfileService.Deactivate(strategyCode);
    return Results.NoContent();
});

app.MapGet("/api/strategies", (IStrategyRegistry strategyRegistry) =>
{
    return strategyRegistry.GetEnabledStrategies()
        .Select(strategy => MapStrategyDefinition(strategy.Definition))
        .ToArray();
});

app.MapGet("/api/review/today", (ReviewAppService reviewAppService) =>
{
    var review = reviewAppService.BuildTodayReview();
    return new TodayReviewDto(
        review.TradingDate,
        review.OpportunityCount,
        review.FocusedCount,
        review.GivenUpCount,
        review.WaitPullbackCount,
        review.AverageScore,
        review.Strategies
            .Select(item => new StrategyReviewDto(
                item.StrategyName,
                item.HitCount,
                item.AverageScore))
            .ToArray(),
        review.Opportunities
            .Select(item => new ReviewOpportunityDto(
                item.Symbol,
                NormalizeStockName(item.Symbol, item.Name),
                item.Status,
                item.ManualTag,
                item.CurrentScore,
                item.HitCount,
                item.FirstSeenTime,
                item.LastSeenTime))
            .ToArray());
});

app.MapGet("/api/review/predictions", (
    DateOnly? date,
    PredictionReviewService predictionReviewService,
    IConfiguration configuration) =>
{
    var signalDate = date ?? DateOnly.FromDateTime(DateTime.Today);
    return MapPredictionReview(predictionReviewService.Get(signalDate), LoadHistoricalStockNames(configuration));
});

app.MapPost("/api/review/predictions/generate", (
    DateOnly? date,
    PredictionReviewService predictionReviewService,
    IConfiguration configuration) =>
{
    var signalDate = date ?? DateOnly.FromDateTime(DateTime.Today);
    return MapPredictionReview(predictionReviewService.Generate(signalDate), LoadHistoricalStockNames(configuration));
});

app.MapPost("/api/review/predictions/verify", async (
    DateOnly? date,
    PredictionReviewService predictionReviewService,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var signalDate = date ?? DateOnly.FromDateTime(DateTime.Today);
    return MapPredictionReview(
        await predictionReviewService.VerifyAsync(signalDate, cancellationToken),
        LoadHistoricalStockNames(configuration));
});

app.MapHub<MonitorHub>("/hubs/monitor");

app.Run();

static OpportunityDto MapOpportunity(
    AShareRadar.Domain.Opportunities.Opportunity item,
    IReadOnlyDictionary<string, string>? stockNames = null,
    AShareRadar.Domain.Opportunities.SignalEvent? latestEvent = null)
{
    var strategyHits = latestEvent?.StrategyHits ?? [];
    return new OpportunityDto(
        item.Id,
        StockSymbolNormalizer.NormalizeCode(item.Symbol),
        NormalizeStockName(item.Symbol, item.Name, stockNames),
        item.Status.ToString(),
        item.CurrentScore,
        item.BestScore,
        item.HitCount,
        item.FirstSeenTime,
        item.LastSeenTime,
        item.ManualTag,
        item.Note,
        BuildOpportunityStrategySummary(strategyHits, latestEvent),
        BuildOpportunityStrategyExplanation(strategyHits, latestEvent));
}

static string BuildOpportunityStrategySummary(
    IReadOnlyList<AShareRadar.Domain.Opportunities.StrategyHitDetail> hits,
    AShareRadar.Domain.Opportunities.SignalEvent? latestEvent)
{
    if (hits.Count == 0)
    {
        return string.IsNullOrWhiteSpace(latestEvent?.StrategyName)
            ? "命中策略：历史命中明细缺失"
            : $"命中策略：{latestEvent.StrategyName}";
    }

    var names = hits
        .OrderByDescending(item => item.Score)
        .Select(item => item.StrategyName)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(3)
        .ToArray();
    if (names.Length == 0)
    {
        return string.IsNullOrWhiteSpace(latestEvent?.StrategyName)
            ? "命中策略：历史命中明细缺失"
            : $"命中策略：{latestEvent.StrategyName}";
    }

    var suffix = hits.Count > names.Length ? $" \u7B49 {hits.Count} \u4E2A\u7B56\u7565" : string.Empty;
    return $"\u547D\u4E2D\u7B56\u7565\uFF1A{string.Join("\u3001", names)}{suffix}";
}

static string BuildOpportunityStrategyExplanation(
    IReadOnlyList<AShareRadar.Domain.Opportunities.StrategyHitDetail> hits,
    AShareRadar.Domain.Opportunities.SignalEvent? latestEvent)
{
    var bestHit = hits
        .OrderByDescending(item => item.Score)
        .FirstOrDefault();
    if (bestHit is null)
    {
        return latestEvent is null
            ? "策略解释：该机会由旧版本扫描生成，缺少事件明细；等待下一次命中后自动补全。"
            : $"策略解释：{latestEvent.StrategyName}，{latestEvent.Reason}";
    }

    var reason = string.IsNullOrWhiteSpace(bestHit.Reason) ? "\u6682\u65E0\u89E3\u91CA" : bestHit.Reason.Trim();
    var risk = string.IsNullOrWhiteSpace(bestHit.Risk) ? string.Empty : $"\uFF1B\u98CE\u9669\uFF1A{bestHit.Risk.Trim()}";
    return $"\u7B56\u7565\u89E3\u91CA\uFF1A{bestHit.StrategyName}\uFF0C{reason}{risk}";
}
static SignalEventDto MapSignalEvent(
    AShareRadar.Domain.Opportunities.SignalEvent item,
    IReadOnlyDictionary<string, string>? stockNames = null)
{
    return new SignalEventDto(
        item.Id,
        item.OpportunityId,
        item.EventTime,
        item.EventType.ToString(),
        StockSymbolNormalizer.NormalizeCode(item.Symbol),
        NormalizeStockName(item.Symbol, item.Name, stockNames),
        item.StrategyCode,
        item.StrategyName,
        item.Score,
        item.Price,
        item.Reason,
        item.Risk,
        item.StrategyHits
            .Select(hit => new StrategyHitDto(
                hit.StrategyCode,
                hit.StrategyName,
                hit.Score,
                hit.Price,
                hit.Reason,
                hit.Risk,
                hit.Metrics,
                hit.Tags,
                hit.PassedConditions,
                hit.FailedConditions,
                hit.StopLossPrice,
                hit.TakeProfitPrice))
            .ToArray());
}

static StockQuoteDto MapStockQuote(AShareRadar.Domain.MarketData.StockQuote item)
{
    return new StockQuoteDto(
        item.Symbol,
        NormalizeStockName(item.Symbol, item.Name),
        item.Price,
        item.ChangePercent,
        item.VolumeRatio,
        item.TurnoverRate,
        item.Amount,
        item.QuoteTime);
}

static MarketSentimentSnapshotDto MapMarketSentiment(MarketSentimentSnapshot item)
{
    return new MarketSentimentSnapshotDto(
        item.SnapshotTime,
        item.ProviderName,
        item.TemperatureScore,
        item.Level,
        item.Summary,
        item.DataQuality,
        item.Categories
            .Select(category => new MarketSentimentCategoryDto(
                category.Code,
                category.Name,
                category.Score,
                category.Status,
                category.Description))
            .ToArray(),
        item.Metrics
            .Select(metric => new MarketSentimentMetricDto(
                metric.Code,
                metric.Name,
                metric.Value,
                metric.DisplayValue,
                metric.Unit,
                metric.CategoryCode,
                metric.IsAvailable,
                metric.SourceStatus))
            .ToArray(),
        item.Warnings);
}

static MarketSentimentDataSourceStatusDto MapMarketSentimentDataSourceStatus(MarketSentimentDataSourceStatus item)
{
    return new MarketSentimentDataSourceStatusDto(
        item.Code,
        item.Status,
        item.Message,
        item.CheckedAt);
}

static IReadOnlyList<MarketSentimentRegimeDto> BuildMarketSentimentRegimes(IReadOnlyList<MarketSentimentSnapshot> snapshots)
{
    var ordered = snapshots.OrderBy(item => item.SnapshotTime).ToArray();
    if (ordered.Length == 0)
    {
        return [];
    }

    var regimes = new List<MarketSentimentRegimeDto>();
    var startIndex = 0;
    for (var i = 1; i <= ordered.Length; i++)
    {
        if (i < ordered.Length && string.Equals(ordered[i].Level, ordered[startIndex].Level, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var range = ordered[startIndex..i];
        var start = range[0];
        var end = range[^1];
        regimes.Add(new MarketSentimentRegimeDto(
            start.SnapshotTime,
            end.SnapshotTime,
            start.Level,
            end.Level,
            start.TemperatureScore,
            end.TemperatureScore,
            range.Min(item => item.TemperatureScore),
            range.Max(item => item.TemperatureScore),
            range.Length,
            BuildRegimeLabel(start, end)));
        startIndex = i;
    }

    return regimes
        .OrderByDescending(item => item.EndTime)
        .Take(80)
        .ToArray();
}

static string BuildRegimeLabel(MarketSentimentSnapshot start, MarketSentimentSnapshot end)
{
    if (end.TemperatureScore - start.TemperatureScore >= 10m)
    {
        return $"{start.Level}回暖";
    }

    if (start.TemperatureScore - end.TemperatureScore >= 10m)
    {
        return $"{start.Level}回落";
    }

    return $"{end.Level}震荡";
}

static MarketSentimentStrategyRulesDto MapMarketSentimentStrategyRules(MarketSentimentStrategyOptions options)
{
    return new MarketSentimentStrategyRulesDto(
        options.Enabled,
        options.MaxSnapshotAgeMinutes,
        options.EnableActionDemotion,
        options.DemoteAggressiveBelowTemperature,
        options.OverheatedRiskTemperature,
        MapAdjustmentRuleDto(options.Frozen),
        MapAdjustmentRuleDto(options.Cold),
        MapAdjustmentRuleDto(options.Neutral),
        MapAdjustmentRuleDto(options.Hot),
        MapAdjustmentRuleDto(options.Overheated));
}

static SentimentAdjustmentRuleDto MapAdjustmentRuleDto(SentimentAdjustmentRule rule)
{
    return new SentimentAdjustmentRuleDto(
        rule.Aggressive,
        rule.Defensive,
        rule.MainlineOrTrend);
}

static void ApplyMarketSentimentStrategyRules(
    MarketSentimentStrategyRulesDto request,
    MarketSentimentStrategyOptions options)
{
    options.Enabled = request.Enabled;
    options.MaxSnapshotAgeMinutes = Math.Clamp(request.MaxSnapshotAgeMinutes, 1, 60);
    options.EnableActionDemotion = request.EnableActionDemotion;
    options.DemoteAggressiveBelowTemperature = Math.Clamp(request.DemoteAggressiveBelowTemperature, 0m, 100m);
    options.OverheatedRiskTemperature = Math.Clamp(request.OverheatedRiskTemperature, 0m, 100m);
    options.Frozen = ToAdjustmentRule(request.Frozen);
    options.Cold = ToAdjustmentRule(request.Cold);
    options.Neutral = ToAdjustmentRule(request.Neutral);
    options.Hot = ToAdjustmentRule(request.Hot);
    options.Overheated = ToAdjustmentRule(request.Overheated);
}

static SentimentAdjustmentRule ToAdjustmentRule(SentimentAdjustmentRuleDto rule)
{
    return new SentimentAdjustmentRule(
        Math.Clamp(rule.Aggressive, -50m, 50m),
        Math.Clamp(rule.Defensive, -50m, 50m),
        Math.Clamp(rule.MainlineOrTrend, -50m, 50m));
}

static KLineBarDto MapKLineBar(KLineBar item)
{
    return new KLineBarDto(
        item.TradingTime,
        item.Open,
        item.High,
        item.Low,
        item.Close,
        item.Volume);
}

static HistoricalSignalDto MapHistoricalSignal(
    HistoricalSignalItem item,
    IReadOnlyDictionary<string, string>? stockNames = null)
{
    return new HistoricalSignalDto(
        item.Id,
        item.OpportunityId,
        item.EventTime,
        item.EventType,
        item.Symbol,
        NormalizeStockName(item.Symbol, item.Name, stockNames),
        item.StrategyCode,
        item.StrategyName,
        item.Score,
        item.Price,
        item.Reason,
        item.Risk,
        item.StrategyHitCount);
}

static StrategyPerformanceDto MapStrategyPerformance(StrategyPerformanceItem item)
{
    return new StrategyPerformanceDto(
        item.StrategyCode,
        item.StrategyName,
        item.HitCount,
        Math.Round(item.AverageScore, 4),
        item.MaxScore,
        item.LastHitTime);
}

static PredictionReviewDto MapPredictionReview(
    PredictionReview item,
    IReadOnlyDictionary<string, string>? stockNames = null)
{
    return new PredictionReviewDto(
        item.SignalDate,
        item.VerifyDate,
        item.PredictionCount,
        item.UpPredictionCount,
        item.VerifiedCount,
        item.CloseSuccessCount,
        item.IntradaySuccessCount,
        RoundNullable(item.CloseSuccessRate),
        RoundNullable(item.IntradaySuccessRate),
        RoundNullable(item.AverageNextCloseReturn),
        item.Message,
        item.Records.Select(record => MapPredictionRecord(record, stockNames)).ToArray());
}

static PredictionRecordDto MapPredictionRecord(
    PredictionRecord item,
    IReadOnlyDictionary<string, string>? stockNames = null)
{
    return new PredictionRecordDto(
        item.Id,
        item.SignalDate,
        item.Symbol,
        NormalizeStockName(item.Symbol, item.Name, stockNames),
        item.StrategyCodes,
        item.StrategyNames,
        item.SignalCount,
        item.StrategyHitCount,
        Math.Round(item.Score, 4),
        Math.Round(item.BestScore, 4),
        item.PredictionDirection,
        Math.Round(item.PredictionScore, 4),
        item.PredictionReason,
        item.RiskNote,
        item.VerifyDate,
        RoundNullable(item.NextOpenReturn),
        RoundNullable(item.NextCloseReturn),
        RoundNullable(item.NextHighReturn),
        RoundNullable(item.NextLowReturn),
        item.IsCloseSuccess,
        item.IsIntradaySuccess,
        item.VerifyStatus,
        item.CreatedAt,
        item.VerifiedAt);
}

static IndicatorPointDto MapIndicatorPoint(IndicatorPoint item)
{
    return new IndicatorPointDto(
        item.TradingTime,
        item.Value1,
        item.Value2,
        item.Value3,
        item.BarValue);
}

static HeatLeaderDto MapHeatLeader(HeatLeader item)
{
    return new HeatLeaderDto(
        item.Rank,
        item.Symbol,
        NormalizeStockName(item.Symbol, item.Name),
        item.ChangePercent,
        item.Amount,
        item.VolumeRatio);
}

static BacktestSignalDto MapBacktestSignal(BacktestSignalItem item)
{
    return new BacktestSignalDto(
        item.TradingDate,
        item.Symbol,
        NormalizeStockName(item.Symbol, item.Name),
        item.StrategyCode,
        item.StrategyName,
        item.Action,
        item.Confidence,
        item.Score,
        item.Price,
        item.Reason,
        item.Risk,
        item.Return1Day,
        item.Return3Day,
        item.Return5Day,
        item.Metrics,
        item.Tags,
        item.PassedConditions,
        item.FailedConditions,
        item.StopLossPrice,
        item.TakeProfitPrice);
}

static QlibSignalStatusDto MapQlibSignalStatus(QlibSignalStatus item)
{
    return new QlibSignalStatusDto(
        item.Enabled,
        item.FileExists,
        item.SignalRoot,
        item.WatchlistPath,
        item.SignalDate,
        item.RecordCount,
        item.LastWriteTime,
        item.Error);
}

static QlibSignalSnapshotDto MapQlibSignalSnapshot(QlibSignalSnapshot item)
{
    return new QlibSignalSnapshotDto(
        item.StrategyCode,
        item.StrategyName,
        item.SourceExperimentId,
        item.SignalDate,
        item.LoadedAt,
        item.Records.Select(MapQlibSignalRecord).ToArray());
}

static QlibSignalRecordDto MapQlibSignalRecord(QlibSignalRecord item)
{
    return new QlibSignalRecordDto(
        item.SignalDate,
        item.Code,
        item.Symbol,
        item.Exchange,
        item.Name,
        item.PredScore,
        item.RankTotal,
        item.ModelRank,
        item.ModelScore100,
        item.TargetWeight,
        item.Action,
        item.Confidence,
        item.StrategyCode,
        item.StrategyName,
        item.SourceExperimentId,
        item.Reason,
        item.Risk);
}

static QlibSignalSeedImportResultDto MapQlibSignalSeedImportResult(QlibSignalSeedImportResult item)
{
    return new QlibSignalSeedImportResultDto(
        item.ImportedAt,
        item.SignalDate,
        item.StrategyCode,
        item.StrategyName,
        item.SourceExperimentId,
        item.ImportedCount,
        item.Seeds.Select(MapQlibSignalSeed).ToArray());
}

static QlibSignalSeedDto MapQlibSignalSeed(QlibSignalSeed item)
{
    return new QlibSignalSeedDto(
        item.Id,
        item.SignalDate,
        item.Code,
        item.Symbol,
        item.Exchange,
        item.Name,
        item.PredScore,
        item.RankTotal,
        item.ModelRank,
        item.ModelScore100,
        item.TargetWeight,
        item.Action,
        item.Confidence,
        item.StrategyCode,
        item.StrategyName,
        item.SourceExperimentId,
        item.Reason,
        item.Risk,
        item.ImportedAt);
}

static StrategyTrainingDatasetDto MapStrategyTrainingDataset(
    StrategyTrainingDataset item,
    IReadOnlyDictionary<string, string>? stockNames = null)
{
    return new StrategyTrainingDatasetDto(
        item.StartDate,
        item.EndDate,
        item.StrategyCode,
        item.SourceSignalCount,
        item.SampleCount,
        item.SuccessCount,
        RoundNullable(item.SuccessRate),
        item.Message,
        item.Samples.Select(sample => MapStrategyTrainingSample(sample, stockNames)).ToArray());
}

static StrategyTrainingSampleDto MapStrategyTrainingSample(
    StrategyTrainingSample item,
    IReadOnlyDictionary<string, string>? stockNames = null)
{
    return new StrategyTrainingSampleDto(
        item.Id,
        item.SignalDate,
        StockSymbolNormalizer.NormalizeCode(item.Symbol),
        NormalizeStockName(item.Symbol, item.Name, stockNames),
        item.StrategyCode,
        item.StrategyName,
        Math.Round(item.Score, 4),
        RoundNullable(item.Price),
        RoundNullable(item.AmountYi),
        RoundNullable(item.ChangePercent),
        RoundNullable(item.VolumeRatio),
        RoundNullable(item.RelativeStrengthPercent),
        RoundNullable(item.SectorHeatScore),
        RoundNullable(item.ConceptHeatScore),
        RoundNullable(item.SentimentTemperature),
        RoundNullable(item.NextOpenReturn),
        RoundNullable(item.NextHighReturn),
        RoundNullable(item.NextCloseReturn),
        item.IsSuccess,
        item.Reason,
        item.Metrics);
}

static StrategyTrainingRunDto MapStrategyTrainingRun(StrategyTrainingRun item)
{
    return new StrategyTrainingRunDto(
        item.RunId,
        item.StartDate,
        item.EndDate,
        item.StrategyCode,
        item.SourceSignalCount,
        item.SampleCount,
        item.ResultCount,
        item.CreatedAt,
        item.Message,
        item.Results.Select(MapStrategyTrainingResult).ToArray());
}

static StrategyTrainingResultDto MapStrategyTrainingResult(StrategyTrainingResult item)
{
    return new StrategyTrainingResultDto(
        item.Rank,
        Math.Round(item.MinScore, 4),
        Math.Round(item.MinAmountYi, 4),
        Math.Round(item.MinRelativeStrengthPercent, 4),
        Math.Round(item.MinHeatScore, 4),
        item.MaxOutputPerDay,
        item.HitCount,
        item.SuccessCount,
        RoundNullable(item.SuccessRate),
        RoundNullable(item.AverageNextOpenReturn),
        RoundNullable(item.AverageNextHighReturn),
        RoundNullable(item.AverageNextCloseReturn),
        RoundNullable(item.WorstNextCloseReturn),
        item.Summary);
}

static StrategyParameterProfileDto MapStrategyParameterProfile(StrategyParameterProfile item)
{
    return new StrategyParameterProfileDto(
        item.Id,
        item.StrategyCode,
        item.ProfileName,
        item.SourceTrainingRunId,
        item.Parameters,
        item.SampleCount,
        RoundNullable(item.SuccessRate),
        RoundNullable(item.AverageNextHighReturn),
        RoundNullable(item.AverageNextCloseReturn),
        item.IsActive,
        item.CreatedAt,
        item.ActivatedAt);
}

static BacktestStrategySummaryDto MapBacktestStrategySummary(BacktestStrategySummaryItem item)
{
    return new BacktestStrategySummaryDto(
        item.StrategyCode,
        item.StrategyName,
        item.SignalCount,
        Math.Round(item.AverageScore, 4),
        RoundNullable(item.WinRate1Day),
        RoundNullable(item.WinRate3Day),
        RoundNullable(item.WinRate5Day),
        RoundNullable(item.AverageReturn1Day),
        RoundNullable(item.AverageReturn3Day),
        RoundNullable(item.AverageReturn5Day),
        RoundNullable(item.BestReturn5Day),
        RoundNullable(item.WorstReturn5Day));
}

static BacktestSentimentSummaryDto MapBacktestSentimentSummary(BacktestSentimentSummaryItem item)
{
    return new BacktestSentimentSummaryDto(
        item.SentimentLevel,
        item.SignalCount,
        Math.Round(item.AverageScore, 4),
        RoundNullable(item.WinRate1Day),
        RoundNullable(item.WinRate3Day),
        RoundNullable(item.WinRate5Day),
        RoundNullable(item.AverageReturn1Day),
        RoundNullable(item.AverageReturn3Day),
        RoundNullable(item.AverageReturn5Day));
}

static decimal? RoundNullable(decimal? value)
{
    return value.HasValue ? Math.Round(value.Value, 4) : null;
}

static async Task<IReadOnlyDictionary<string, string>> LoadStockNamesAsync(
    IMarketDataProvider marketDataProvider,
    IConfiguration configuration,
    CancellationToken cancellationToken)
{
    var names = LoadHistoricalStockNames(configuration);
    if (names.Count > 0)
    {
        return names;
    }

    foreach (var item in await LoadRealtimeStockNamesAsync(marketDataProvider, cancellationToken))
    {
        names[item.Key] = item.Value;
    }

    return names;
}

static Dictionary<string, string> LoadHistoricalStockNames(IConfiguration configuration)
{
    var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var duckDbPath = configuration["EastMoneyQuant:DuckDbPath"] ?? configuration["Database:DuckDbPath"];
    if (string.IsNullOrWhiteSpace(duckDbPath) || !File.Exists(duckDbPath))
    {
        return names;
    }

    try
    {
        using var connection = new DuckDBConnection($"Data Source={duckDbPath};ACCESS_MODE=READ_ONLY");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT code, any_value(code_name) AS stock_name
            FROM daily_bars
            WHERE code_name IS NOT NULL
              AND code_name <> ''
            GROUP BY code;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var symbol = reader.GetString(0);
            var name = reader.GetString(1).Trim();
            if (!IsMissingStockName(symbol, name))
            {
                names[StockSymbolNormalizer.NormalizeCode(symbol)] = name;
            }
        }
    }
    catch
    {
        return names;
    }

    return names;
}

static async Task<IReadOnlyDictionary<string, string>> LoadRealtimeStockNamesAsync(
    IMarketDataProvider marketDataProvider,
    CancellationToken cancellationToken)
{
    try
    {
        var snapshot = await marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
        return snapshot.Quotes
            .Where(item => !IsMissingStockName(item.Symbol, item.Name))
            .GroupBy(item => StockSymbolNormalizer.NormalizeCode(item.Symbol), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Name.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }
    catch
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

static bool IsMissingStockName(string symbol, string? currentName)
{
    if (string.IsNullOrWhiteSpace(currentName))
    {
        return true;
    }

    if (IsLikelyGarbledStockName(currentName))
    {
        return true;
    }

    var normalizedSymbol = StockSymbolNormalizer.NormalizeCode(symbol);
    var normalizedName = StockSymbolNormalizer.NormalizeCode(currentName.Trim());
    return string.Equals(normalizedSymbol, normalizedName, StringComparison.OrdinalIgnoreCase);
}

static bool IsLikelyGarbledStockName(string value)
{
    return value.Contains('\uFFFD')
        || value.Contains('\u951F')
        || value.Count(ch => ch == '?') >= 2;
}

static string NormalizeStockName(
    string symbol,
    string? currentName,
    IReadOnlyDictionary<string, string>? stockNames = null)
{
    var normalized = symbol.Trim().ToLowerInvariant();
    if ((normalized.StartsWith("sh") || normalized.StartsWith("sz")) && normalized.Length == 8)
    {
        normalized = normalized[2..];
    }

    if (stockNames is not null && stockNames.TryGetValue(StockSymbolNormalizer.NormalizeCode(symbol), out var realtimeName))
    {
        return realtimeName;
    }

    return normalized switch
    {
        "000001" => "\u5E73\u5B89\u94F6\u884C",
        "002230" => "\u79D1\u5927\u8BAF\u98DE",
        "002415" => "\u6D77\u5EB7\u5A01\u89C6",
        "300059" => "\u4E1C\u65B9\u8D22\u5BCC",
        "300750" => "\u5B81\u5FB7\u65F6\u4EE3",
        "600000" => "\u6D66\u53D1\u94F6\u884C",
        "600519" => "\u8D35\u5DDE\u8305\u53F0",
        "601318" => "\u4E2D\u56FD\u5E73\u5B89",
        _ => IsMissingStockName(symbol, currentName) ? symbol : currentName!.Trim()
    };
}
static StrategyDefinitionDto MapStrategyDefinition(AShareRadar.Domain.Strategies.StrategyDefinition item)
{
    return new StrategyDefinitionDto(
        item.Code,
        item.Name,
        item.Type.ToString(),
        item.Stage.ToString(),
        item.DefaultAction.ToString(),
        new StrategyDataRequirementDto(
            item.DataRequirement.RequiresRealtimeQuote,
            item.DataRequirement.RequiresDailyKLine,
            item.DataRequirement.RequiresMinuteKLine,
            item.DataRequirement.RequiresSectorData,
            item.DataRequirement.RequiresCapitalFlow,
            item.DataRequirement.MinDailyBarCount),
        item.Parameters,
        item.Description);
}




