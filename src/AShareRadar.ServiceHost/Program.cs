using System.Diagnostics;
using AShareRadar.Application.Backtesting;
using AShareRadar.Application.History;
using AShareRadar.Application.Indicators;
using AShareRadar.Application.Jobs;
using AShareRadar.Application.Monitoring;
using AShareRadar.Application.MarketData;
using AShareRadar.Application.Opportunities;
using AShareRadar.Application.Realtime;
using AShareRadar.Application.Review;
using AShareRadar.Application.Strategies;
using AShareRadar.Contracts.Backtesting;
using AShareRadar.Contracts.Jobs;
using AShareRadar.Contracts.Monitoring;
using AShareRadar.Contracts.History;
using AShareRadar.Contracts.MarketData;
using AShareRadar.Contracts.Opportunities;
using AShareRadar.Contracts.Review;
using AShareRadar.Contracts.Strategies;
using AShareRadar.Infrastructure.MarketData;
using AShareRadar.Application.Opportunities.Storage;
using AShareRadar.Persistence.Database;
using AShareRadar.Persistence.History;
using AShareRadar.Persistence.Jobs;
using AShareRadar.Persistence.MarketData;
using AShareRadar.Persistence.Opportunities;
using AShareRadar.Persistence.Review;
using AShareRadar.Persistence.Strategies;
using AShareRadar.ServiceHost.Hubs;
using AShareRadar.ServiceHost.Jobs;
using AShareRadar.ServiceHost.Realtime;
using AShareRadar.ServiceHost.Services;
using AShareRadar.ServiceHost.Workers;
using AShareRadar.Strategies.Intraday;
using AShareRadar.Strategies.Registry;
using DuckDB.NET.Data;
using NLog.Web;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

const string ServiceHostSingleInstanceMutexName = @"Local\AShareRadar.ServiceHost";
var eastMoneyFirstLevelIndustryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "传媒",
    "电力设备",
    "电子",
    "房地产",
    "纺织服饰",
    "非银金融",
    "公用事业",
    "国防军工",
    "钢铁",
    "环保",
    "交通运输",
    "基础化工",
    "家用电器",
    "建筑材料",
    "建筑装饰",
    "机械设备",
    "计算机",
    "煤炭",
    "美容护理",
    "农林牧渔",
    "汽车",
    "轻工制造",
    "商贸零售",
    "石油石化",
    "社会服务",
    "食品饮料",
    "通信",
    "医药生物",
    "有色金属",
    "银行",
    "综合"
};
using var serviceHostSingleInstanceMutex = new Mutex(
    initiallyOwned: true,
    name: ServiceHostSingleInstanceMutexName,
    createdNew: out var isFirstServiceHostInstance);

if (!isFirstServiceHostInstance)
{
    ReportDuplicateServiceHost(ServiceHostSingleInstanceMutexName);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Host.UseNLog();

builder.Services.AddSingleton<MonitorRuntimeState>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("MarketSentimentStrategy")
        .Get<MarketSentimentStrategyOptions>() ?? new MarketSentimentStrategyOptions());
builder.Services.AddSingleton(
    builder.Configuration.GetSection("StrategyPools")
        .Get<StrategyPoolScanOptions>() ?? new StrategyPoolScanOptions());
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
builder.Services.AddSingleton<IBackgroundJobStore, SqliteBackgroundJobStore>();
builder.Services.AddSingleton<BackgroundJobQueue>();
builder.Services.AddSingleton<BackgroundJobService>();
builder.Services.AddSingleton<SqliteOpportunityStateStore>();
builder.Services.AddSingleton<IHistoryQueryService, SqliteHistoryQueryService>();
builder.Services.AddSingleton<IMarketSentimentStore, SqliteMarketSentimentStore>();
builder.Services.AddSingleton<IHeatSnapshotStore, SqliteHeatSnapshotStore>();
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
builder.Services.AddSingleton<ISignalHeatContextStore, SqliteSignalHeatContextStore>();
builder.Services.AddSingleton<SignalHeatContextService>();
builder.Services.AddSingleton<ISignalReturnStatsStore, SqliteSignalReturnStatsStore>();
builder.Services.AddSingleton<SignalReturnStatsService>();
builder.Services.AddSingleton<IStrategyVersionStore, SqliteStrategyVersionStore>();
builder.Services.AddSingleton<StrategyVersionService>();
builder.Services.AddSingleton<ILongTermTrackingStore, SqliteLongTermTrackingStore>();
builder.Services.AddSingleton<LongTermTrackingService>();
builder.Services.AddSingleton<BacktestReplayService>();
builder.Services.AddSingleton<ScanOrchestrator>();
builder.Services.AddSingleton<IIndicatorCalculator, IndicatorCalculator>();
builder.Services.AddSingleton<ISectorHeatService, SnapshotSectorHeatService>();
builder.Services.AddSingleton<MarketSentimentService>();
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
builder.Services.AddSingleton<AShareRadar.ServiceHost.Services.StockNameMapSyncService>();
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
builder.Services.AddSingleton<DuckDbIntradayKLineCacheProvider>(services =>
{
    var options = services.GetRequiredService<DatabaseOptions>();
    return new DuckDbIntradayKLineCacheProvider(options.DuckDbPath);
});
builder.Services.AddSingleton<IKLineDataProvider>(services => new HybridKLineDataProvider(
    services.GetRequiredService<DuckDbMinuteKLineCacheProvider>(),
    services.GetRequiredService<DuckDbIntradayKLineCacheProvider>(),
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
builder.Services.AddSingleton<IStrategyRegistry, StrategyRegistry>();
builder.Services.AddSingleton<IRealtimeEventPublisher, SignalRRealtimeEventPublisher>();
var historicalDataUpdateOptions = builder.Configuration
    .GetSection("HistoricalDataUpdate")
    .Get<HistoricalDataUpdateOptions>() ?? new HistoricalDataUpdateOptions();
builder.Services.AddSingleton(historicalDataUpdateOptions);
builder.Services.AddSingleton<HistoricalDataUpdateService>();
builder.Services.AddSingleton<MarketMappingSyncService>();
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
var thirtyMinuteKLineCacheWorkerOptions = builder.Configuration
    .GetSection("ThirtyMinuteKLineCacheWorker")
    .Get<ThirtyMinuteKLineCacheWorkerOptions>() ?? new ThirtyMinuteKLineCacheWorkerOptions();
builder.Services.AddSingleton(thirtyMinuteKLineCacheWorkerOptions);
builder.Services.AddSingleton<MarketSentimentRuntimeState>();
builder.Services.AddSingleton<IBackgroundJobHandler, HistoryDataUpdateJobHandler>();
builder.Services.AddSingleton<IBackgroundJobHandler, NextDayPredictionJobHandler>();
builder.Services.AddSingleton<IBackgroundJobHandler, ThirtyMinuteKLineUpdateJobHandler>();

builder.Services
    .AddSignalR()
    .AddHubOptions<MonitorHub>(options =>
    {
        options.MaximumReceiveMessageSize = 4 * 1024 * 1024;
    });
builder.Services.AddHostedService<BackgroundJobWorker>();
builder.Services.AddHostedService<MonitorWorker>();
builder.Services.AddHostedService<HistoricalDataUpdateWorker>();
builder.Services.AddHostedService<HistoricalStrategyScanWorker>();
builder.Services.AddHostedService<ExternalSentimentAutoUpdateWorker>();
builder.Services.AddHostedService<MarketSentimentWorker>();
builder.Services.AddHostedService<MinuteKLineCacheWorker>();
builder.Services.AddHostedService<ThirtyMinuteKLineCacheWorker>();

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AShareRadar.ServiceHost.Startup");
startupLogger.LogInformation(
    "ServiceHost started. Version={Version} BaseDirectory={BaseDirectory} ProcessId={ProcessId} Environment={Environment} OS={OS} Runtime={Runtime}",
    typeof(Program).Assembly.GetName().Version,
    AppContext.BaseDirectory,
    Environment.ProcessId,
    app.Environment.EnvironmentName,
    Environment.OSVersion,
    Environment.Version);

app.Use(async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    var requestLogger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AShareRadar.Http.Request");
    using var scope = requestLogger.BeginScope(new Dictionary<string, object>
    {
        ["TraceId"] = context.TraceIdentifier
    });
    try
    {
        await next();
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        if (context.Response.StatusCode >= 500)
        {
            requestLogger.LogError(
                "HTTP request returned a server error. Method={Method} Path={Path} StatusCode={StatusCode} ElapsedMs={ElapsedMs}",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsedMs);
        }
        else if (context.Response.StatusCode >= 400 || elapsedMs >= 2000)
        {
            requestLogger.LogWarning(
                "HTTP request completed with a warning. Method={Method} Path={Path} StatusCode={StatusCode} ElapsedMs={ElapsedMs}",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsedMs);
        }
        else if (!HttpMethods.IsGet(context.Request.Method))
        {
            requestLogger.LogInformation(
                "HTTP command completed. Method={Method} Path={Path} StatusCode={StatusCode} ElapsedMs={ElapsedMs}",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsedMs);
        }
        else
        {
            requestLogger.LogDebug(
                "HTTP query completed. Method={Method} Path={Path} StatusCode={StatusCode} ElapsedMs={ElapsedMs}",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsedMs);
        }
    }
    catch (Exception ex)
    {
        requestLogger.LogError(
            ex,
            "HTTP request failed. Method={Method} Path={Path} ElapsedMs={ElapsedMs}",
            context.Request.Method,
            context.Request.Path,
            stopwatch.ElapsedMilliseconds);
        throw;
    }
});

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
        status.HistoricalStrategyScanSignalCount,
        status.RealtimePoolStatus,
        status.ObservationPoolStatus,
        status.RealtimePoolSignalCount,
        status.ObservationPoolSignalCount,
        status.PlatformBreakoutAlertCount,
        status.PlatformBreakoutConfirmedCount);
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
    var takeCount = Math.Clamp(count ?? 20, 1, 5000);

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

app.MapGet("/api/market-data/sector-mapping/boards", (ISectorHeatService sectorHeatService, int? count) =>
{
    var takeCount = Math.Clamp(count ?? 1000, 1, 5000);
    var status = sectorHeatService.GetMappingStatus();
    return ReadMappingBoardsFromCsv(status.MappingPath, 5000)
        .Where(item => eastMoneyFirstLevelIndustryNames.Contains(item.Name))
        .Take(takeCount)
        .Select((item, index) => new MappingBoardItemDto(item.Code, item.Name, item.StockCount, index + 1))
        .ToArray();
});

app.MapGet("/api/market-data/concepts", async (
    IMarketDataProvider marketDataProvider,
    ISectorHeatService sectorHeatService,
    int? count,
    CancellationToken cancellationToken) =>
{
    var snapshot = await marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
    var conceptHeatSnapshot = sectorHeatService.BuildConcepts(snapshot);
    var takeCount = Math.Clamp(count ?? 20, 1, 5000);

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

app.MapGet("/api/market-data/concept-mapping/boards", (ISectorHeatService sectorHeatService, int? count) =>
{
    var takeCount = Math.Clamp(count ?? 100, 1, 5000);
    var status = sectorHeatService.GetConceptMappingStatus();
    return ReadMappingBoardsFromCsv(status.MappingPath, takeCount);
});

app.MapGet("/api/market-data/heat-snapshots/latest", (IHeatSnapshotStore store, int? sectorCount, int? conceptCount) =>
{
    var snapshot = store.GetLatestHeatSnapshot(
        Math.Clamp(sectorCount ?? 20, 0, 5000),
        Math.Clamp(conceptCount ?? 20, 0, 5000));
    return snapshot is null
        ? Results.NotFound()
        : Results.Ok(MapHeatSnapshotOverview(snapshot));
});

app.MapGet("/api/market-data/heat-snapshots/by-time", (IHeatSnapshotStore store, DateTimeOffset time, int? sectorCount, int? conceptCount) =>
{
    var snapshot = store.GetHeatSnapshotAt(
        time,
        Math.Clamp(sectorCount ?? 20, 0, 5000),
        Math.Clamp(conceptCount ?? 20, 0, 5000));
    return snapshot is null
        ? Results.NotFound()
        : Results.Ok(MapHeatSnapshotOverview(snapshot));
});

app.MapGet("/api/market-data/mapping-snapshots/latest", (IHeatSnapshotStore store, string type) =>
{
    if (!string.Equals(type, "sector", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, "concept", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("type must be sector or concept.");
    }

    var snapshot = store.GetLatestMappingSnapshot(type);
    return snapshot is null
        ? Results.NotFound()
        : Results.Ok(MapMappingSnapshotBatch(snapshot));
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

app.MapGet("/api/opportunities/{id:guid}", (
    Guid id,
    OpportunityAppService opportunityAppService,
    ISignalHeatContextStore signalHeatContextStore) =>
{
    var opportunity = opportunityAppService.GetOpportunity(id);
    if (opportunity is null)
    {
        return Results.NotFound();
    }

    var sourceEvents = opportunityAppService.GetEventsForOpportunity(id, 20);
    var heatContextsByEvent = signalHeatContextStore.GetByEventIds(sourceEvents.Select(item => item.Id));
    var events = sourceEvents
        .Select(item => MapSignalEvent(item, null, heatContextsByEvent))
        .ToArray();

    return Results.Ok(new OpportunityDetailDto(
        MapOpportunity(opportunity, latestEvent: sourceEvents.FirstOrDefault()),
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
    ISignalHeatContextStore signalHeatContextStore,
    int? count,
    CancellationToken cancellationToken) =>
{
    var events = opportunityAppService.GetRecentEvents(Math.Clamp(count ?? 50, 1, 200));
    var stockNames = events.Any(item => IsMissingStockName(item.Symbol, item.Name))
        ? await LoadStockNamesAsync(marketDataProvider, app.Configuration, cancellationToken)
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    var heatContextsByEvent = signalHeatContextStore.GetByEventIds(events.Select(item => item.Id));
    return events
        .Select(item => MapSignalEvent(item, stockNames, heatContextsByEvent))
        .ToArray();
});

app.MapGet("/api/signals/events/{eventId:guid}/heat-context", (
    Guid eventId,
    ISignalHeatContextStore signalHeatContextStore) =>
{
    return signalHeatContextStore.GetByEventId(eventId)
        .Select(MapSignalHeatContext)
        .ToArray();
});

app.MapGet("/api/signals/events/{eventId:guid}/strategy-versions", (
    Guid eventId,
    StrategyVersionService strategyVersionService) =>
{
    return strategyVersionService.GetHitVersions(eventId)
        .Select(MapStrategyHitVersion)
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

app.MapPost("/api/history-data/update-now", (BackgroundJobService jobService) =>
{
    var job = jobService.Enqueue("history-data-update", "历史更新", new { });
    return Results.Accepted($"/api/jobs/{job.Id}", new CreateBackgroundJobResponse(job.Id));
});

app.MapGet("/api/jobs/{id:guid}", (Guid id, BackgroundJobService jobService) =>
{
    var job = jobService.Get(id);
    return job is null ? Results.NotFound() : Results.Ok(MapBackgroundJob(job));
});

app.MapGet("/api/jobs/latest", (string? type, BackgroundJobService jobService) =>
{
    var job = jobService.GetLatest(type);
    return job is null ? Results.NotFound() : Results.Ok(MapBackgroundJob(job));
});

app.MapGet("/api/jobs/active", (BackgroundJobService jobService) =>
{
    return jobService.GetActive().Select(MapBackgroundJob).ToArray();
});

app.MapGet("/api/jobs/{id:guid}/logs", (Guid id, int? count, BackgroundJobService jobService) =>
{
    return jobService.GetLogs(id, count ?? 300).Select(MapBackgroundJobLog).ToArray();
});

app.MapPost("/api/jobs/next-day-prediction", (DateOnly? date, BackgroundJobService jobService) =>
{
    var signalDate = date ?? DateOnly.FromDateTime(DateTime.Today);
    var job = jobService.Enqueue("next-day-prediction", $"次日预测 {signalDate:yyyy-MM-dd}", new { SignalDate = signalDate });
    return Results.Accepted($"/api/jobs/{job.Id}", new CreateBackgroundJobResponse(job.Id));
});

app.MapPost("/api/jobs/history-data-update", (BackgroundJobService jobService) =>
{
    var job = jobService.Enqueue("history-data-update", "历史更新", new { });
    return Results.Accepted($"/api/jobs/{job.Id}", new CreateBackgroundJobResponse(job.Id));
});

app.MapPost("/api/jobs/m30-kline-update", (BackgroundJobService jobService) =>
{
    var job = jobService.Enqueue("m30-kline-update", "30分钟K更新", new { });
    return Results.Accepted($"/api/jobs/{job.Id}", new CreateBackgroundJobResponse(job.Id));
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

app.MapGet("/api/strategies", (IStrategyRegistry strategyRegistry) =>
{
    return strategyRegistry.GetEnabledStrategies()
        .Select(strategy => MapStrategyDefinition(strategy.Definition))
        .ToArray();
});

app.MapGet("/api/strategies/versions", (StrategyVersionService strategyVersionService) =>
{
    return strategyVersionService.QueryVersions()
        .Select(MapStrategyVersion)
        .ToArray();
});

app.MapGet("/api/strategies/{strategyCode}/versions", (
    string strategyCode,
    StrategyVersionService strategyVersionService) =>
{
    return strategyVersionService.QueryVersions(strategyCode)
        .Select(MapStrategyVersion)
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

app.MapGet("/api/review/long-term-tracking", async (
    DateOnly? fromDate,
    DateOnly? toDate,
    string? symbol,
    string? strategyCode,
    string? status,
    string? sortBy,
    bool? descending,
    int? count,
    LongTermTrackingService longTermTrackingService,
    IMarketDataProvider marketDataProvider,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var snapshot = await marketDataProvider.LoadMarketSnapshotAsync(cancellationToken);
    var quotes = snapshot.Quotes
        .GroupBy(item => StockSymbolNormalizer.NormalizeCode(item.Symbol), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    var stockNames = MergeStockNames(LoadHistoricalStockNames(configuration), quotes);
    var result = longTermTrackingService.Query(
        new LongTermTrackingQuery(
            fromDate,
            toDate,
            symbol,
            strategyCode,
            status,
            string.IsNullOrWhiteSpace(sortBy) ? "LastHitAt" : sortBy,
            descending ?? true,
            count ?? 500));
    return MapLongTermTrackingResult(result, stockNames, quotes);
});

app.MapPost("/api/review/long-term-tracking/backfill", (
    LongTermTrackingService longTermTrackingService) =>
{
    return MapLongTermTrackingBackfill(longTermTrackingService.Backfill());
});

app.MapGet("/api/review/long-term-tracking/{symbol}/timeline", (
    string symbol,
    int? count,
    LongTermTrackingService longTermTrackingService,
    IConfiguration configuration) =>
{
    var stockNames = LoadHistoricalStockNames(configuration);
    return longTermTrackingService.QueryTimeline(symbol, count ?? 200)
        .Select(item => MapLongTermTrackingTimelineItem(item, stockNames))
        .ToArray();
});

app.MapGet("/api/review/signal-returns/horizons", (SignalReturnStatsService signalReturnStatsService) =>
{
    return signalReturnStatsService.GetHorizons()
        .Select(MapSignalReturnHorizon)
        .ToArray();
});

app.MapPost("/api/review/signal-returns/recalculate", async (
    SignalReturnRecalculateRequestDto request,
    SignalReturnStatsService signalReturnStatsService,
    CancellationToken cancellationToken) =>
{
    var result = await signalReturnStatsService.RecalculateAsync(
        new SignalReturnRecalculateRequest(ToSignalReturnQuery(request)),
        cancellationToken);
    return MapSignalReturnRecalculateResult(result);
});

app.MapGet("/api/review/signal-returns/records", (
    SignalReturnStatsService signalReturnStatsService,
    DateOnly? fromDate,
    DateOnly? toDate,
    string? symbol,
    string? strategyCode,
    string? strategyGroup,
    string? strategyVersion,
    string? horizonGroup,
    string? horizonCode,
    string? status,
    int? count) =>
{
    return MapSignalReturnQueryResult(signalReturnStatsService.QueryRecords(new SignalReturnQuery(
        fromDate,
        toDate,
        symbol,
        strategyCode,
        strategyGroup,
        strategyVersion,
        horizonGroup,
        horizonCode,
        status,
        count ?? 200)));
});

app.MapGet("/api/review/signal-returns/summary", (
    SignalReturnStatsService signalReturnStatsService,
    DateOnly? fromDate,
    DateOnly? toDate,
    string? strategyCode,
    string? strategyGroup,
    string? strategyVersion,
    string? horizonGroup,
    string? horizonCode,
    int? count) =>
{
    return signalReturnStatsService.QueryStrategySummaries(new SignalReturnSummaryQuery(
            fromDate,
            toDate,
            strategyCode,
            strategyGroup,
            strategyVersion,
            horizonGroup,
            horizonCode,
            count ?? 100))
        .Select(MapSignalReturnStrategySummary)
        .ToArray();
});

app.MapPost("/api/review/long-term-tracking/{id:guid}/status", (
    Guid id,
    UpdateLongTermTrackingStatusRequest request,
    LongTermTrackingService longTermTrackingService,
    IConfiguration configuration) =>
{
    var item = longTermTrackingService.UpdateStatus(id, request.Status);
    return item is null
        ? Results.NotFound()
        : Results.Ok(MapLongTermTrackingItem(item, LoadHistoricalStockNames(configuration), null));
});

app.MapPost("/api/review/long-term-tracking/{id:guid}/note", (
    Guid id,
    UpdateLongTermTrackingNoteRequest request,
    LongTermTrackingService longTermTrackingService,
    IConfiguration configuration) =>
{
    var item = longTermTrackingService.UpdateNote(id, request.Note);
    return item is null
        ? Results.NotFound()
        : Results.Ok(MapLongTermTrackingItem(item, LoadHistoricalStockNames(configuration), null));
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

static BackgroundJobDto MapBackgroundJob(AShareRadar.Application.Jobs.BackgroundJob item)
{
    return new BackgroundJobDto(
        item.Id,
        item.Type,
        item.Title,
        item.Status,
        item.ProgressPercent,
        item.CurrentStep,
        item.CreatedAt,
        item.StartedAt,
        item.FinishedAt,
        item.ExitCode,
        item.ErrorMessage,
        item.FixSuggestion,
        item.ResultJson);
}

static BackgroundJobLogDto MapBackgroundJobLog(AShareRadar.Application.Jobs.BackgroundJobLog item)
{
    return new BackgroundJobLogDto(
        item.Id,
        item.JobId,
        item.CreatedAt,
        item.Stream,
        item.Message);
}

static SignalEventDto MapSignalEvent(
    AShareRadar.Domain.Opportunities.SignalEvent item,
    IReadOnlyDictionary<string, string>? stockNames = null,
    IReadOnlyDictionary<Guid, IReadOnlyList<SignalHeatContext>>? heatContextsByEvent = null)
{
    var heatContexts = heatContextsByEvent is not null
        && heatContextsByEvent.TryGetValue(item.Id, out var eventHeatContexts)
            ? eventHeatContexts.Select(MapSignalHeatContext).ToArray()
            : [];

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
            .ToArray(),
        heatContexts);
}

static SignalHeatContextDto MapSignalHeatContext(SignalHeatContext item)
{
    return new SignalHeatContextDto(
        item.EventId,
        item.Symbol,
        item.EventTime,
        item.ContextType,
        item.Code,
        item.Name,
        item.HeatRank,
        item.StockCount,
        item.RisingCount,
        item.AverageChangePercent,
        item.RisingRatioPercent,
        item.TotalAmount,
        item.HeatScore,
        item.IsLeader,
        item.HeatSnapshotBatchId,
        item.CreatedAt);
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
        item.Volume,
        item.Amount,
        item.TurnoverRate);
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

static LongTermTrackingQueryResultDto MapLongTermTrackingResult(
    LongTermTrackingQueryResult item,
    IReadOnlyDictionary<string, string>? stockNames = null,
    IReadOnlyDictionary<string, AShareRadar.Domain.MarketData.StockQuote>? quotes = null)
{
    return new LongTermTrackingQueryResultDto(
        item.TotalCount,
        item.LastHitAt,
        item.Items.Select(trackingItem => MapLongTermTrackingItem(trackingItem, stockNames, quotes)).ToArray());
}

static LongTermTrackingBackfillResultDto MapLongTermTrackingBackfill(LongTermTrackingBackfillResult item)
{
    return new LongTermTrackingBackfillResultDto(
        item.BackfilledAt,
        item.ItemCount,
        item.EventCount);
}

static LongTermTrackingItemDto MapLongTermTrackingItem(
    LongTermTrackingItem item,
    IReadOnlyDictionary<string, string>? stockNames = null,
    IReadOnlyDictionary<string, AShareRadar.Domain.MarketData.StockQuote>? quotes = null)
{
    AShareRadar.Domain.MarketData.StockQuote? quote = null;
    quotes?.TryGetValue(StockSymbolNormalizer.NormalizeCode(item.Symbol), out quote);
    var hitPrice = item.LatestPrice;
    var currentPrice = quote?.Price;
    var returnFromHit = hitPrice is > 0m && currentPrice.HasValue
        ? (currentPrice.Value - hitPrice.Value) * 100m / hitPrice.Value
        : (decimal?)null;
    return new LongTermTrackingItemDto(
        item.Id,
        StockSymbolNormalizer.NormalizeCode(item.Symbol),
        NormalizeStockName(item.Symbol, quote?.Name ?? item.Name, stockNames),
        item.StrategyCode,
        item.StrategyName,
        item.FirstHitAt,
        item.LastHitAt,
        item.HitCount,
        RoundNullable(hitPrice),
        RoundNullable(currentPrice),
        RoundNullable(returnFromHit),
        Math.Round(item.LatestScore, 4),
        Math.Round(item.BestScore, 4),
        item.LatestReason,
        item.LatestRisk,
        item.Status,
        item.ManualPriority,
        item.Note,
        item.Tags,
        item.LatestEventId,
        item.CreatedAt,
        item.UpdatedAt);
}

static LongTermTrackingTimelineItemDto MapLongTermTrackingTimelineItem(
    LongTermTrackingTimelineItem item,
    IReadOnlyDictionary<string, string>? stockNames = null)
{
    return new LongTermTrackingTimelineItemDto(
        item.EventId,
        item.EventTime,
        StockSymbolNormalizer.NormalizeCode(item.Symbol),
        NormalizeStockName(item.Symbol, item.Name, stockNames),
        item.StrategyCode,
        item.StrategyName,
        Math.Round(item.Score, 4),
        RoundNullable(item.Price),
        item.Reason,
        item.Risk);
}

static SignalReturnQuery ToSignalReturnQuery(SignalReturnRecalculateRequestDto request)
{
    return new SignalReturnQuery(
        request.FromDate,
        request.ToDate,
        request.Symbol,
        request.StrategyCode,
        request.StrategyGroup,
        request.StrategyVersion,
        request.HorizonGroup,
        request.HorizonCode,
        request.Status,
        request.Count <= 0 ? 1000 : request.Count);
}

static SignalReturnHorizonDto MapSignalReturnHorizon(SignalReturnHorizon item)
{
    return new SignalReturnHorizonDto(
        item.Code,
        item.Name,
        item.TradingDays,
        item.Group);
}

static SignalReturnRecalculateResultDto MapSignalReturnRecalculateResult(SignalReturnRecalculateResult item)
{
    return new SignalReturnRecalculateResultDto(
        item.CalculatedAt,
        item.SourceSignalCount,
        item.ProcessedSignalCount,
        item.SkippedSignalCount,
        item.FailedSignalCount,
        item.RecordCount);
}

static SignalReturnQueryResultDto MapSignalReturnQueryResult(SignalReturnQueryResult item)
{
    return new SignalReturnQueryResultDto(
        item.TotalCount,
        item.Items.Select(MapSignalReturnRecord).ToArray());
}

static SignalReturnRecordDto MapSignalReturnRecord(SignalReturnRecord item)
{
    return new SignalReturnRecordDto(
        item.EventId,
        item.OpportunityId,
        item.EventTime,
        item.SignalDate,
        StockSymbolNormalizer.NormalizeCode(item.Symbol),
        NormalizeStockName(item.Symbol, item.Name),
        item.StrategyCode,
        item.StrategyName,
        item.StrategyGroup,
        item.StrategyVersionId,
        item.StrategyVersion,
        item.Score,
        RoundNullable(item.SignalPrice),
        Math.Round(item.EntryPrice, 4),
        item.HorizonCode,
        item.HorizonName,
        item.TradingDays,
        item.HorizonGroup,
        item.TargetDate,
        RoundNullable(item.TargetClose),
        RoundNullable(item.ReturnPercent),
        RoundNullable(item.MaxReturnPercent),
        RoundNullable(item.MinReturnPercent),
        item.Status,
        item.CalculatedAt,
        item.UpdatedAt);
}

static SignalReturnStrategySummaryDto MapSignalReturnStrategySummary(SignalReturnStrategySummary item)
{
    return new SignalReturnStrategySummaryDto(
        item.StrategyCode,
        item.StrategyName,
        item.StrategyGroup,
        item.StrategyVersion,
        item.HorizonCode,
        item.HorizonName,
        item.HorizonGroup,
        item.SignalCount,
        item.CompletedCount,
        item.PendingCount,
        item.WinCount,
        RoundNullable(item.WinRatePercent),
        RoundNullable(item.AverageReturnPercent),
        RoundNullable(item.AverageMaxReturnPercent),
        RoundNullable(item.AverageMinReturnPercent),
        RoundNullable(item.BestReturnPercent),
        RoundNullable(item.WorstReturnPercent),
        item.LastSignalTime);
}

static StrategyVersionDto MapStrategyVersion(StrategyVersion item)
{
    return new StrategyVersionDto(
        item.Id,
        item.StrategyCode,
        item.StrategyName,
        item.Version,
        item.Status,
        item.RuleSummary,
        item.ParameterJson,
        item.DataRequirementJson,
        item.DefinitionHash,
        item.CreatedAt,
        item.ActivatedAt,
        item.DeactivatedAt,
        item.Source);
}

static StrategyHitVersionDto MapStrategyHitVersion(StrategyHitVersion item)
{
    return new StrategyHitVersionDto(
        item.EventId,
        item.StrategyCode,
        item.StrategyVersionId,
        item.Version,
        item.ParameterJson,
        item.RuleSummary,
        item.CreatedAt);
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

static HeatSnapshotOverviewDto MapHeatSnapshotOverview(HeatSnapshotOverview item)
{
    return new HeatSnapshotOverviewDto(
        item.Id,
        item.SnapshotTime,
        item.TradeDate,
        item.SectorMappingBatchId,
        item.ConceptMappingBatchId,
        item.SectorCount,
        item.ConceptCount,
        item.Sectors.Select(MapHeatSnapshotItem).ToArray(),
        item.Concepts.Select(MapHeatSnapshotItem).ToArray());
}

static HeatSnapshotItemDto MapHeatSnapshotItem(HeatSnapshotItem item)
{
    return new HeatSnapshotItemDto(
        item.Code,
        item.Name,
        item.HeatRank,
        item.StockCount,
        item.RisingCount,
        item.AverageChangePercent,
        item.RisingRatioPercent,
        item.TotalAmount,
        item.HeatScore,
        item.Leaders.Select(MapHeatLeader).ToArray(),
        item.LeaderSymbols);
}

static MappingSnapshotBatchDto MapMappingSnapshotBatch(MappingSnapshotBatch item)
{
    return new MappingSnapshotBatchDto(
        item.Id,
        item.MappingType,
        item.SnapshotTime,
        item.TradeDate,
        item.Source,
        item.ItemCount,
        item.FileHash,
        item.CreatedAt);
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

static MappingBoardItemDto[] ReadMappingBoardsFromCsv(string mappingPath, int count)
{
    if (!File.Exists(mappingPath))
    {
        return [];
    }

    var boards = new Dictionary<string, (string Code, string Name, int Rank, HashSet<string> Symbols)>(StringComparer.OrdinalIgnoreCase);
    var rank = 0;
    foreach (var columns in ReadSimpleCsvRows(mappingPath))
    {
        if (columns.Count < 3)
        {
            continue;
        }

        var symbol = StockSymbolNormalizer.NormalizeCode(columns[0]);
        var code = columns[1].Trim();
        var name = columns[2].Trim();
        if (string.IsNullOrWhiteSpace(symbol)
            || string.IsNullOrWhiteSpace(code)
            || string.IsNullOrWhiteSpace(name))
        {
            continue;
        }

        if (!boards.TryGetValue(code, out var board))
        {
            rank++;
            board = (code, name, rank, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            boards[code] = board;
        }

        board.Symbols.Add(symbol);
        boards[code] = board;
    }

    return boards.Values
        .OrderBy(item => item.Rank)
        .Take(count)
        .Select(item => new MappingBoardItemDto(
            item.Code,
            item.Name,
            item.Symbols.Count,
            item.Rank))
        .ToArray();
}

static IEnumerable<IReadOnlyList<string>> ReadSimpleCsvRows(string mappingPath)
{
    foreach (var line in File.ReadLines(mappingPath).Skip(1))
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
        {
            continue;
        }

        yield return ParseSimpleCsvLine(line);
    }
}

static IReadOnlyList<string> ParseSimpleCsvLine(string line)
{
    var values = new List<string>();
    var current = new System.Text.StringBuilder();
    var inQuotes = false;
    for (var i = 0; i < line.Length; i++)
    {
        var ch = line[i];
        if (ch == '"')
        {
            if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else
            {
                inQuotes = !inQuotes;
            }
        }
        else if (ch == ',' && !inQuotes)
        {
            values.Add(current.ToString().Trim());
            current.Clear();
        }
        else
        {
            current.Append(ch);
        }
    }

    values.Add(current.ToString().Trim());
    return values;
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
    try
    {
        // Realtime instrument names are the authoritative source when the historical cache is stale or malformed.
        foreach (var item in await LoadRealtimeStockNamesAsync(marketDataProvider, cancellationToken))
        {
            names[item.Key] = item.Value;
        }
    }
    catch
    {
        // Keep the historical fallback when the realtime provider is unavailable.
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

static IReadOnlyDictionary<string, string> MergeStockNames(
    IReadOnlyDictionary<string, string> historicalNames,
    IReadOnlyDictionary<string, AShareRadar.Domain.MarketData.StockQuote> quotes)
{
    var names = new Dictionary<string, string>(historicalNames, StringComparer.OrdinalIgnoreCase);
    foreach (var quote in quotes.Values)
    {
        var symbol = StockSymbolNormalizer.NormalizeCode(quote.Symbol);
        if (symbol.Length == 0 || IsMissingStockName(symbol, quote.Name))
        {
            continue;
        }

        // A valid realtime name must override historical cache values because the cache may contain shifted fields.
        names[symbol] = quote.Name.Trim();
    }

    return names;
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
        || value.Count(ch => ch == '?') >= 2
        || value.All(ch => char.IsDigit(ch) || ch is '.' or '-' or '_');
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

static void ReportDuplicateServiceHost(string mutexName)
{
    var message = $"{DateTimeOffset.Now:O} Duplicate ServiceHost instance blocked. Mutex={mutexName} ProcessId={Environment.ProcessId} BaseDirectory={AppContext.BaseDirectory}";
    Console.Error.WriteLine(message);
    try
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        File.AppendAllText(
            Path.Combine(logDirectory, "service-single-instance.log"),
            message + Environment.NewLine);
    }
    catch
    {
        // 单例保护不能因为诊断日志写入失败而阻止退出。
    }
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
