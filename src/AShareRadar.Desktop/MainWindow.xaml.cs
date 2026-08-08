using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AShareRadar.Desktop.Controls;
using AShareRadar.Contracts.Backtesting;
using AShareRadar.Contracts.History;
using AShareRadar.Contracts.Jobs;
using AShareRadar.Contracts.MarketData;
using AShareRadar.Contracts.Monitoring;
using AShareRadar.Contracts.Opportunities;
using AShareRadar.Contracts.Review;
using AShareRadar.Contracts.Strategies;
using AShareRadar.Desktop.Services;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;

namespace AShareRadar.Desktop;

public partial class MainWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly NLog.Logger MappingLogger = NLog.LogManager.GetLogger("AShareRadar.Mapping.Desktop");
    private WebView2? _mappingWebView;
    private string? _activeMappingTraceId;
    private string? _activeMappingRunId;
    private readonly RadarApiClient _apiClient = new("http://127.0.0.1:18730");
    private readonly MinimalSignalRClient _realtimeClient = new("http://127.0.0.1:18730/hubs/monitor");
    private readonly DispatcherTimer _refreshTimer;
    private Guid? _selectedOpportunityId;
    private string? _selectedSymbol;
    private string? _selectedName;
    private string _opportunityView = "Current";
    private string _kLinePeriod = "day";
    private string _indicatorMode = "MACD";
    private bool _showSectorHeat;
    private bool _showConceptHeat;
    private bool _showMarketSentiment;
    private bool _showHistory;
    private bool _showPredictionReview;
    private bool _showStrategyCenter;
    private bool _showLongTermTracking;
    private bool _showBacktest;
    private bool _showStockPools;
    private bool _showResearchPage;
    private bool _isRefreshingOpportunityList;
    private int _dailyHitCount;
    private IReadOnlyList<DailyHitDisplay> _dailyHitItems = [];
    private DateOnly? _historyTradingDate;
    private DateOnly _predictionDate = DateOnly.FromDateTime(DateTime.Today);
    private string? _historySymbol;
    private string? _historyStrategyCode;
    private BacktestReplayResultDto? _lastBacktestResult;
    private bool _isResearchDialogOpen;
    private KLineFloatingWindow? _floatingKLineWindow;
    private Guid? _latestBackgroundJobId;
    private Guid? _lastAutoRefreshedPredictionJobId;
    private IReadOnlyList<HeatBoardItemDto> _mappingSectorHeatItems = [];
    private IReadOnlyList<HeatBoardItemDto> _mappingConceptHeatItems = [];

    public MainWindow()
    {
        InitializeComponent();
        InitializeMappingBrowser();
        HistoryDatePicker.SelectedDate = null;
        PredictionDatePicker.SelectedDate = DateTime.Today;
        BacktestStartDatePicker.SelectedDate = DateTime.Today.AddDays(-60);
        BacktestEndDatePicker.SelectedDate = DateTime.Today;
        StockPoolHitDatePicker.SelectedDate = DateTime.Today;
        ApplyWorkspaceVisibility();
        ApplyKLineButtonStyles();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        Loaded += async (_, _) =>
        {
            await StartRealtimeClientAsync();
            await RefreshAsync();
        };
        Closed += async (_, _) => await _realtimeClient.DisposeAsync();
    }

    private async void UpdateHistoryDataButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            HistoricalDataStatusText.Text = "历史数据：已提交后台任务...";
            var response = await _apiClient.TriggerHistoricalDataUpdateAsync(token);
            _latestBackgroundJobId = response?.JobId;
            await RefreshBackgroundJobStatusAsync(token);
        });
    }

    private async void InitializeMappingBrowser()
    {
        var stopwatch = Stopwatch.StartNew();
        MappingLogger.Info("WebView2 initialization started. BaseDirectory={BaseDirectory}", AppContext.BaseDirectory);
        try
        {
            _mappingWebView = MappingWebView;
            await _mappingWebView.EnsureCoreWebView2Async();
            _mappingWebView.CoreWebView2.WebResourceResponseReceived += MappingWebResourceResponseReceived;
            _mappingWebView.CoreWebView2.NavigationStarting += (_, args) =>
                MappingLogger.Info("WebView2 navigation started. NavigationId={NavigationId} Uri={Uri} TraceId={TraceId}", args.NavigationId, args.Uri, _activeMappingTraceId);
            _mappingWebView.CoreWebView2.NavigationCompleted += (_, args) =>
                MappingLogger.Info(
                    "WebView2 navigation completed. NavigationId={NavigationId} Success={Success} WebErrorStatus={WebErrorStatus} TraceId={TraceId}",
                    args.NavigationId,
                    args.IsSuccess,
                    args.WebErrorStatus,
                    _activeMappingTraceId);
            MappingLogger.Info(
                "WebView2 initialization completed. RuntimeVersion={RuntimeVersion} UserDataFolder={UserDataFolder} ElapsedMs={ElapsedMs}",
                _mappingWebView.CoreWebView2.Environment.BrowserVersionString,
                _mappingWebView.CoreWebView2.Environment.UserDataFolder,
                stopwatch.ElapsedMilliseconds);
            _mappingWebView.CoreWebView2.Navigate("https://quote.eastmoney.com/center/gridlist.html#concept_board");
        }
        catch (Exception ex)
        {
            MappingLogger.Error(ex, "WebView2 initialization failed. ElapsedMs={ElapsedMs}", stopwatch.ElapsedMilliseconds);
        }
    }

    private void MappingWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (_activeMappingTraceId is not null && e.Request.Uri.Contains("push2.eastmoney.com/api/qt/clist/get", StringComparison.OrdinalIgnoreCase))
        {
            string? contentType = null;
            try
            {
                contentType = e.Response.Headers.GetHeader("Content-Type");
            }
            catch (Exception ex)
            {
                MappingLogger.Debug(ex, "EastMoney response Content-Type could not be read. TraceId={TraceId} Uri={Uri}", _activeMappingTraceId, e.Request.Uri);
            }

            MappingLogger.Info(
                "EastMoney response received. TraceId={TraceId} Method={Method} StatusCode={StatusCode} ContentType={ContentType} Uri={Uri}",
                _activeMappingTraceId,
                e.Request.Method,
                e.Response.StatusCode,
                contentType,
                e.Request.Uri);
        }
    }

    private async void UpdateM30KLineButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            BackgroundJobStatusText.Text = "后台任务：已提交30分钟K更新...";
            var response = await _apiClient.StartM30KLineUpdateJobAsync(token);
            _latestBackgroundJobId = response?.JobId;
            await RefreshBackgroundJobStatusAsync(token);
        });
    }

    private async void UpdateMarketMappingButton_Click(object sender, RoutedEventArgs e)
    {
        var traceId = $"mapping-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        using var traceScope = NLog.ScopeContext.PushProperty("TraceId", traceId);
        MappingLogger.Info("Mapping update button clicked. TraceId={TraceId}", traceId);
        _refreshTimer.Stop();
        try
        {
            await RunUiActionAsync(async token =>
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    MappingUpdateStatusText.Text = "概念行业：正在通过 WebView2 获取...";
                    MappingPageStateText.Text = "采集中";
                    MappingPageStateText.Visibility = Visibility.Visible;
                    MappingPageErrorText.Visibility = Visibility.Collapsed;
                    var payload = await UpdateMappingsFromWebViewAsync(traceId, token);
                    MappingUpdateStatusText.Text = "概念行业：正在同步服务端...";
                    MappingPageStateText.Text = "同步中";
                    var sectorRows = payload.Sectors
                        .SelectMany(board => board.Members.Select(member => new MarketMappingRowDto(member.Symbol, board.Code, board.Name)))
                        .ToArray();
                    var conceptRows = payload.Concepts
                        .SelectMany(board => board.Members.Select(member => new MarketMappingRowDto(member.Symbol, board.Code, board.Name)))
                        .ToArray();
                    var request = new MarketMappingSyncRequest(traceId, DateTimeOffset.Now, sectorRows, conceptRows);

                    MappingLogger.Info(
                        "Service upload requested. TraceId={TraceId} SectorRows={SectorRows} ConceptRows={ConceptRows}",
                        traceId,
                        sectorRows.Length,
                        conceptRows.Length);
                    using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    uploadTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                    var result = await _realtimeClient.UploadMarketMappingsAsync(request, uploadTimeout.Token);
                    if (!result.Success)
                    {
                        throw new InvalidOperationException(result.Error ?? result.Message);
                    }

                    MappingLogger.Info("Refreshing Desktop data after service upload. TraceId={TraceId}", traceId);
                    MappingPageStateText.Text = "刷新中";
                    using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    refreshTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                    await RefreshAsync(refreshTimeout.Token);
                    MappingLogger.Info(
                        "Mapping update UI flow completed. TraceId={TraceId} Version={Version} SectorRows={SectorRows} ConceptRows={ConceptRows} ElapsedMs={ElapsedMs}",
                        traceId,
                        result.Version,
                        result.SectorRows,
                        result.ConceptRows,
                        stopwatch.ElapsedMilliseconds);
                    MappingUpdateStatusText.Text = $"概念行业：更新完成，行业 {result.SectorRows} / 概念 {result.ConceptRows}";
                    MappingPageStateText.Text = "更新完成";
                    MappingPageSummaryText.Text = $"行业映射 {result.SectorRows:N0} | 概念映射 {result.ConceptRows:N0} | 更新时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                }
                catch (Exception ex)
                {
                    _activeMappingTraceId = null;
                    MappingPageStateText.Text = "更新失败";
                    MappingPageErrorText.Text = ex is OperationCanceledException ? "采集或同步超时，任务已终止。" : ex.Message;
                    MappingPageErrorText.Visibility = Visibility.Visible;
                    MappingLogger.Error(ex, "Mapping update UI flow failed. TraceId={TraceId} ElapsedMs={ElapsedMs}", traceId, stopwatch.ElapsedMilliseconds);
                    throw;
                }
            }, TimeSpan.FromMinutes(10));
        }
        finally
        {
            _refreshTimer.Start();
        }
    }

    private async Task<MappingPayload> UpdateMappingsFromWebViewAsync(string traceId, CancellationToken cancellationToken)
    {
        if (_mappingWebView?.CoreWebView2 is null)
        {
            MappingLogger.Error("Mapping collection cannot start because WebView2 is not initialized. TraceId={TraceId}", traceId);
            throw new InvalidOperationException("WebView2 尚未初始化。");
        }

        const string targetUri = "https://quote.eastmoney.com/center/gridlist.html#concept_board";
        var stopwatch = Stopwatch.StartNew();
        _activeMappingTraceId = traceId;
        MappingLogger.Info("WebView2 mapping collection started. TraceId={TraceId} Uri={Uri}", traceId, targetUri);

        var currentUri = _mappingWebView.Source?.AbsoluteUri ?? _mappingWebView.CoreWebView2.Source;
        if (!string.Equals(currentUri, targetUri, StringComparison.OrdinalIgnoreCase))
        {
            var navigation = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) => navigation.TrySetResult(args);
            _mappingWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            try
            {
                _mappingWebView.CoreWebView2.Navigate(targetUri);
                var navigationResult = await navigation.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
                if (!navigationResult.IsSuccess)
                {
                    throw new InvalidOperationException($"东方财富页面导航失败：{navigationResult.WebErrorStatus}");
                }
            }
            finally
            {
                _mappingWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        MappingLogger.Info("EastMoney page ready for script. TraceId={TraceId} CurrentUri={CurrentUri} ElapsedMs={ElapsedMs}", traceId, currentUri, stopwatch.ElapsedMilliseconds);

        var runId = Guid.NewGuid().ToString("N");
        _activeMappingRunId = runId;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                var message = args.TryGetWebMessageAsString();
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (!root.TryGetProperty("runId", out var messageRunId)
                    || !string.Equals(messageRunId.GetString(), runId, StringComparison.Ordinal))
                {
                    return;
                }

                var kind = root.GetProperty("kind").GetString();
                if (string.Equals(kind, "progress", StringComparison.Ordinal))
                {
                    var completed = root.GetProperty("completed").GetInt32();
                    var total = root.GetProperty("total").GetInt32();
                    var boardName = root.GetProperty("boardName").GetString();
                    MappingUpdateStatusText.Text = $"概念行业：正在采集 {completed}/{total}，{boardName}";
                    MappingPageStateText.Text = $"采集中 {completed}/{total}";
                    return;
                }

                if (string.Equals(kind, "result", StringComparison.Ordinal))
                {
                    completion.TrySetResult(root.GetProperty("payload").GetRawText());
                    return;
                }

                if (string.Equals(kind, "error", StringComparison.Ordinal))
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"东方财富采集失败：{root.GetProperty("error").GetString()}"));
                }
            }
            catch (Exception ex)
            {
                MappingLogger.Error(ex, "WebView2 mapping message processing failed. TraceId={TraceId} RunId={RunId}", traceId, runId);
                completion.TrySetException(ex);
            }
        }
        _mappingWebView.CoreWebView2.WebMessageReceived += OnMessage;
        var script = $$$"""
            (async () => {
              const runId='{{{runId}}}';
              if(window.__asrMappingRun?.controller){
                window.__asrMappingRun.controller.abort();
              }
              const controller=new AbortController();
              const signal=controller.signal;
              window.__asrMappingRun={runId,controller};
              const fields='f12,f14';
              const post=(value)=>chrome.webview.postMessage(JSON.stringify({runId,...value}));
              async function fetchJson(url){
                let lastError;
                for(let attempt=1;attempt<=3;attempt++){
                  try{
                    const response=await fetch(url,{signal,cache:'no-store'});
                    if(!response.ok) throw new Error(`HTTP ${response.status}: ${url}`);
                    return await response.json();
                  }catch(error){
                    if(signal.aborted) throw error;
                    lastError=error;
                    if(attempt<3) await new Promise(resolve=>setTimeout(resolve,attempt*500));
                  }
                }
                throw lastError;
              }
              async function boards(type){
                const pageSize=100;
                async function page(pageNumber){
                  const url=`https://push2.eastmoney.com/api/qt/clist/get?pn=${pageNumber}&pz=${pageSize}&po=1&np=1&ut=bd1d9ddb04089700cf9c27f6f7426281&fltt=2&invt=2&fid=f3&fs=m:90+t:${type}&fields=${fields}`;
                  return await fetchJson(url);
                }
                const first=await page(1);
                const firstItems=first?.data?.diff||[];
                const total=Number(first?.data?.total||firstItems.length);
                const pageCount=Math.ceil(total/pageSize);
                if(pageCount<=1) return firstItems;
                const remainingPages=Array.from({length:pageCount-1},(_,index)=>index+2);
                const remaining=await mapLimit(remainingPages,4,async pageNumber=>(await page(pageNumber))?.data?.diff||[]);
                return firstItems.concat(...remaining);
              }
              async function members(board){
                const url=`https://push2.eastmoney.com/api/qt/clist/get?pn=1&pz=10000&po=1&np=1&ut=bd1d9ddb04089700cf9c27f6f7426281&fltt=2&invt=2&fid=f3&fs=b:${board.f12}+f:!50&fields=f12,f14`;
                const json=await fetchJson(url);
                return (json?.data?.diff||[]).map(x=>({symbol:x.f12,name:x.f14}));
              }
              async function mapLimit(items,limit,work){
                const result=new Array(items.length);
                let next=0;
                async function worker(){
                  while(true){
                    const index=next++;
                    if(index>=items.length) return;
                    result[index]=await work(items[index],index);
                  }
                }
                await Promise.all(Array.from({length:Math.min(limit,items.length)},worker));
                return result;
              }
              const [sectorBoards,conceptBoards]=await Promise.all([boards(2),boards(3)]);
              const allBoards=[
                ...sectorBoards.map(board=>({kind:'sector',board})),
                ...conceptBoards.map(board=>({kind:'concept',board}))
              ];
              const total=allBoards.length;
              let completed=0;
              const collected=await mapLimit(allBoards,8,async item=>{
                const result={code:item.board.f12,name:item.board.f14,members:await members(item.board)};
                completed++;
                post({kind:'progress',completed,total,boardName:item.board.f14});
                return {kind:item.kind,result};
              });
              const sectors=collected.filter(x=>x.kind==='sector').map(x=>x.result);
              const concepts=collected.filter(x=>x.kind==='concept').map(x=>x.result);
              if(signal.aborted) throw new DOMException('Mapping collection canceled.','AbortError');
              post({kind:'result',payload:{sectors,concepts}});
            })().catch(error => {
              const runId='{{{runId}}}';
              chrome.webview.postMessage(JSON.stringify({
                runId,
                kind:'error',
                error:error?.name==='AbortError'?'采集已取消':String(error)
              }));
            })
            """;
        MappingLogger.Info("EastMoney collection script execution started. TraceId={TraceId}", traceId);
        var scriptResult = await _mappingWebView.ExecuteScriptAsync(script);
        MappingLogger.Info(
            "EastMoney collection script dispatched. TraceId={TraceId} ExecuteResultLength={ExecuteResultLength} ElapsedMs={ElapsedMs}",
            traceId,
            scriptResult?.Length ?? 0,
            stopwatch.ElapsedMilliseconds);
        using var collectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        collectionTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var registration = collectionTimeout.Token.Register(() =>
        {
            completion.TrySetCanceled(collectionTimeout.Token);
            _ = Dispatcher.InvokeAsync(async () =>
            {
                if (_mappingWebView?.CoreWebView2 is not null)
                {
                    await _mappingWebView.ExecuteScriptAsync(
                        $"if(window.__asrMappingRun?.runId==='{runId}')window.__asrMappingRun.controller.abort();");
                }
            });
        });
        string json;
        try
        {
            json = await completion.Task;
        }
        finally
        {
            _mappingWebView.CoreWebView2.WebMessageReceived -= OnMessage;
            if (string.Equals(_activeMappingRunId, runId, StringComparison.Ordinal))
            {
                _activeMappingRunId = null;
            }
        }
        MappingLogger.Info("EastMoney collection JSON received. TraceId={TraceId} JsonLength={JsonLength} ElapsedMs={ElapsedMs}", traceId, json.Length, stopwatch.ElapsedMilliseconds);
        using (var errorDocument = JsonDocument.Parse(json))
        {
            if (errorDocument.RootElement.TryGetProperty("error", out var error))
            {
                MappingLogger.Error("EastMoney collection script returned an error. TraceId={TraceId} Error={Error}", traceId, error.GetString());
                throw new InvalidOperationException($"东方财富采集失败：{error.GetString()}");
            }
        }
        var payload = JsonSerializer.Deserialize<MappingPayload>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("东方财富没有返回映射数据。");
        if (payload.Sectors.Count == 0 || payload.Concepts.Count == 0)
        {
            throw new InvalidOperationException("东方财富返回的行业或概念列表为空。");
        }
        MappingLogger.Info(
            "WebView2 mapping collection completed. TraceId={TraceId} SectorBoards={SectorBoards} ConceptBoards={ConceptBoards} SectorMembers={SectorMembers} ConceptMembers={ConceptMembers} ElapsedMs={ElapsedMs}",
            traceId,
            payload.Sectors.Count,
            payload.Concepts.Count,
            payload.Sectors.Sum(board => board.Members.Count),
            payload.Concepts.Sum(board => board.Members.Count),
            stopwatch.ElapsedMilliseconds);
        _activeMappingTraceId = null;
        return payload;
    }

    private sealed record MappingPayload(List<MappingBoard> Sectors, List<MappingBoard> Concepts);
    private sealed record MappingBoard(string Code, string Name, List<MappingMember> Members);
    private sealed record MappingMember(string Symbol, string Name);

    private async void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveDecisionAsync("Watch");
    }

    private async void FocusButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveDecisionAsync("Focus");
    }

    private async void WaitPullbackButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveDecisionAsync("WaitPullback");
    }

    private async void GiveUpButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveDecisionAsync("GiveUp");
    }

    private async void SignalStreamViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = false;
        _showSectorHeat = false;
        _showConceptHeat = false;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();
    }

    private async void SectorHeatViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = false;
        _showSectorHeat = true;
        _showConceptHeat = false;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();
    }

    private async void ConceptHeatViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = false;
        _showSectorHeat = false;
        _showConceptHeat = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();
    }

    private async void MarketSentimentViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = false;
        _showSectorHeat = false;
        _showConceptHeat = false;
        _showMarketSentiment = true;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();
    }

    private void MarketSentimentSummaryCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dialog = new MarketSentimentDetailWindow(_apiClient)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private async void HistoryStatsViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = true;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();
    }

    private async void PredictionReviewViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = true;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshPredictionReviewAsync();
    }

    private async void StrategyCenterViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = true;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();
    }

    private async void LongTermTrackingViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = true;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshLongTermTrackingAsync();
    }

    private void BacktestViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = true;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
    }

    private async void StockPoolsViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = true;
        ApplyWorkspaceVisibility();
        await RefreshDailyHitsAsync();
    }

    private async void RealtimePageButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = false;
        _showSectorHeat = false;
        _showConceptHeat = false;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();
    }

    private async void ResearchPageButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenResearchDialogAsync();
    }

    private async Task OpenResearchDialogAsync()
    {
        if (_isResearchDialogOpen)
        {
            return;
        }

        _isResearchDialogOpen = true;
        if (WorkspacePanel.Parent is not Grid workspaceHost)
        {
            _isResearchDialogOpen = false;
            return;
        }

        var originalColumn = Grid.GetColumn(WorkspacePanel);
        var originalMargin = WorkspacePanel.Margin;
        workspaceHost.Children.Remove(WorkspacePanel);
        WorkspacePanel.Margin = new Thickness(0);

        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = true;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();

        var dialog = new Window
        {
            Title = "研究复盘",
            Owner = this,
            Content = WorkspacePanel,
            Width = Math.Max(1100, ActualWidth * 0.86),
            Height = Math.Max(720, ActualHeight * 0.86),
            MinWidth = 960,
            MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize
        };

        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            dialog.Content = null;
            WorkspacePanel.Margin = originalMargin;
            Grid.SetColumn(WorkspacePanel, originalColumn);
            workspaceHost.Children.Add(WorkspacePanel);
            _isResearchDialogOpen = false;

            _showResearchPage = false;
            _showSectorHeat = false;
            _showConceptHeat = false;
            _showMarketSentiment = false;
            _showHistory = false;
            _showPredictionReview = false;
            _showStrategyCenter = false;
            _showLongTermTracking = false;
            _showBacktest = false;
            _showStockPools = false;
            ApplyWorkspaceVisibility();
            await RefreshAsync();
        }
    }

    private async void ApplyHistoryFilterButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyHistoryFilterAsync();
    }

    private async void ClearHistoryFilterButton_Click(object sender, RoutedEventArgs e)
    {
        _historyTradingDate = null;
        _historySymbol = null;
        _historyStrategyCode = null;
        HistoryDatePicker.SelectedDate = null;
        HistorySymbolTextBox.Text = string.Empty;
        HistoryStrategyCodeTextBox.Text = string.Empty;

        await ApplyHistoryFilterAsync();
    }

    private async void UseSelectedSymbolFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedSymbol))
        {
            FooterText.Text = "请先在左侧选择一个机会，再按当前股票筛选历史。";
            return;
        }

        HistorySymbolTextBox.Text = NormalizeHistorySymbol(_selectedSymbol);
        await ApplyHistoryFilterAsync();
    }

    private async Task ApplyHistoryFilterAsync()
    {
        _historyTradingDate = HistoryDatePicker.SelectedDate.HasValue
            ? DateOnly.FromDateTime(HistoryDatePicker.SelectedDate.Value)
            : null;
        _historySymbol = string.IsNullOrWhiteSpace(HistorySymbolTextBox.Text)
            ? null
            : NormalizeHistorySymbol(HistorySymbolTextBox.Text);
        _historyStrategyCode = string.IsNullOrWhiteSpace(HistoryStrategyCodeTextBox.Text)
            ? null
            : HistoryStrategyCodeTextBox.Text.Trim();

        _showHistory = true;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showLongTermTracking = false;
        _showBacktest = false;
        _showStockPools = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();
    }

    private async void GeneratePredictionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            UpdatePredictionDateFromPicker();
            PredictionSummaryText.Text = "次日预测已提交后台任务...";
            PredictionWaitText.Text = "任务运行完成后会自动刷新预测数据。";
            PredictionRecordListBox.ItemsSource = Array.Empty<PredictionRecordDisplay>();
            var response = await _apiClient.StartNextDayPredictionJobAsync(_predictionDate, token);
            _latestBackgroundJobId = response?.JobId;
            await RefreshBackgroundJobStatusAsync(token);
        });
    }

    private async void VerifyPredictionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            UpdatePredictionDateFromPicker();
            PredictionSummaryText.Text = "正在验证预测结果...";
            PredictionWaitText.Text = "正在调用后端验证预测结果，请稍候...";
            var review = await _apiClient.VerifyPredictionReviewAsync(_predictionDate, token);
            ApplyPredictionReview(review);
        });
    }

    private async void RefreshPredictionButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshPredictionReviewAsync();
    }

    private async void BackgroundJobLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_latestBackgroundJobId.HasValue)
        {
            FooterText.Text = "暂无后台任务日志。";
            return;
        }

        await RunUiActionAsync(async token =>
        {
            var job = await _apiClient.GetJobAsync(_latestBackgroundJobId.Value, token);
            var logs = await _apiClient.GetJobLogsAsync(_latestBackgroundJobId.Value, 500, token);
            ShowBackgroundJobLogDialog(job, logs);
        });
    }

    private async void RefreshLongTermTrackingButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLongTermTrackingAsync();
    }

    private async void BackfillLongTermTrackingButton_Click(object sender, RoutedEventArgs e)
    {
        var originalContent = BackfillLongTermTrackingButton.Content;
        SetActionsEnabled(false);
        BackfillLongTermTrackingButton.Content = "回填中";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            LongTermTrackingSummaryText.Text = "正在从历史信号回填长期跟踪...";
            var result = await _apiClient.BackfillLongTermTrackingAsync(timeout.Token);
            LongTermTrackingSummaryText.Text = result is null
                ? "回填完成，但后端没有返回明细。"
                : $"回填完成：跟踪 {result.ItemCount} 只/策略组合，来源事件 {result.EventCount} 条，时间 {FormatDateTime(result.BackfilledAt)}。";
            await RefreshLongTermTrackingAsync(timeout.Token);
        }
        catch (TaskCanceledException)
        {
            LongTermTrackingSummaryText.Text = "历史回填请求超时，已停止前端等待；请稍后点击查询确认结果。";
            FooterText.Text = "历史回填超过前端等待时间，按钮已恢复。";
        }
        catch (Exception ex)
        {
            LongTermTrackingSummaryText.Text = $"历史回填失败：{ex.Message}";
            FooterText.Text = $"操作失败：{ex.Message}";
        }
        finally
        {
            BackfillLongTermTrackingButton.Content = originalContent;
            SetActionsEnabled(true);
        }
    }

    private async void RefreshDailyHitsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDailyHitsAsync();
    }
    private async void DailyHitDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DailyHitDataGrid.SelectedItem is DailyHitDisplay item)
        {
            await SelectDailyHitAsync(item);
        }
    }

    private void DailyHitFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyDailyHitFilters();
    }

    private void DailyHitFilterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyDailyHitFilters();
    }

    private async void RunBacktestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBacktestAsync();
    }

    private void BacktestScale20Button_Click(object sender, RoutedEventArgs e)
    {
        BacktestMaxSymbolsTextBox.Text = "20";
    }

    private void BacktestScale50Button_Click(object sender, RoutedEventArgs e)
    {
        BacktestMaxSymbolsTextBox.Text = "50";
    }

    private void BacktestScale100Button_Click(object sender, RoutedEventArgs e)
    {
        BacktestMaxSymbolsTextBox.Text = "100";
    }

    private void BacktestPositive5DayCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ApplyBacktestResult(_lastBacktestResult);
    }

    private void ExportBacktestButton_Click(object sender, RoutedEventArgs e)
    {
        ExportBacktestResult();
    }

    private void UseSelectedSymbolBacktestButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedSymbol))
        {
            FooterText.Text = "请先在左侧选择一个机会，再按当前股票回放。";
            return;
        }

        BacktestSymbolsTextBox.Text = NormalizeHistorySymbol(_selectedSymbol);
        BacktestStockPoolComboBox.SelectedIndex = 0;
    }

    private void ExportBacktestResult()
    {
        if (_lastBacktestResult is null)
        {
            FooterText.Text = "请先执行一次策略回放，再导出结果。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"策略回放_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("类型,日期,股票,名称,策略,动作,置信度,分数,1日收益,3日收益,5日收益,原因");
        foreach (var item in _lastBacktestResult.Signals)
        {
            builder.AppendLine(string.Join(",",
                "明细",
                EscapeCsv(item.TradingDate.ToString("yyyy-MM-dd")),
                EscapeCsv(item.Symbol),
                EscapeCsv(item.Name),
                EscapeCsv(item.StrategyName),
                EscapeCsv(TranslateStrategyAction(item.Action)),
                EscapeCsv(TranslateSignalConfidence(item.Confidence)),
                item.Score.ToString("F4"),
                FormatNullableNumber(item.Return1Day),
                FormatNullableNumber(item.Return3Day),
                FormatNullableNumber(item.Return5Day),
                EscapeCsv(item.Reason)));
        }

        builder.AppendLine();
        builder.AppendLine("类型,策略,命中次数,平均分,1日胜率,3日胜率,5日胜率,1日平均收益,3日平均收益,5日平均收益,5日最好,5日最差");
        foreach (var item in _lastBacktestResult.StrategySummaries)
        {
            builder.AppendLine(string.Join(",",
                "汇总",
                EscapeCsv(item.StrategyName),
                item.SignalCount,
                item.AverageScore.ToString("F4"),
                FormatNullableNumber(item.WinRate1Day),
                FormatNullableNumber(item.WinRate3Day),
                FormatNullableNumber(item.WinRate5Day),
                FormatNullableNumber(item.AverageReturn1Day),
                FormatNullableNumber(item.AverageReturn3Day),
                FormatNullableNumber(item.AverageReturn5Day),
                FormatNullableNumber(item.BestReturn5Day),
                FormatNullableNumber(item.WorstReturn5Day)));
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        FooterText.Text = $"已导出回放结果：{dialog.FileName}";
    }

    private async Task RefreshDailyHitsAsync(CancellationToken cancellationToken = default)
    {
        var tradingDate = DateOnly.FromDateTime(StockPoolHitDatePicker.SelectedDate ?? DateTime.Today);
        try
        {
            var signalsTask = _apiClient.GetHistoricalSignalsAsync(
                tradingDate,
                null,
                null,
                10000,
                cancellationToken);
            var opportunitiesTask = _apiClient.GetOpportunitiesAsync("All", cancellationToken);
            await Task.WhenAll(signalsTask, opportunitiesTask);
            var opportunityBySymbol = opportunitiesTask.Result
                .GroupBy(item => NormalizeHistorySymbol(item.Symbol), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => item.LastSeenTime).First(),
                    StringComparer.OrdinalIgnoreCase);
            var dailyHits = signalsTask.Result
                .GroupBy(item => NormalizeHistorySymbol(item.Symbol), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var byScore = group
                        .OrderByDescending(item => item.Score)
                        .ThenByDescending(item => item.EventTime)
                        .ToArray();
                    var latest = group.OrderByDescending(item => item.EventTime).First();
                    var best = byScore[0];
                    var strategyNames = group
                        .Select(item => item.StrategyName)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    opportunityBySymbol.TryGetValue(NormalizeHistorySymbol(best.Symbol), out var opportunity);
                    var strategyCount = Math.Max(
                        strategyNames.Length,
                        group.Max(item => Math.Max(item.StrategyHitCount, 1)));
                    var manualStatus = BuildDailyHitManualStatus(opportunity);
                    var reviewText = opportunity is null
                        ? "机会状态：未找到关联记录"
                        : $"机会状态：{BuildDailyHitReviewText(opportunity)}";

                    return new DailyHitDisplay(
                        NormalizeHistorySymbol(best.Symbol),
                        best.Name,
                        latest.EventTime,
                        best.StrategyName,
                        strategyNames,
                        strategyCount,
                        byScore.Length,
                        best.Score,
                        best.Price,
                        best.Reason,
                        best.Risk,
                        $"{best.Score:F2}",
                        best.Price.HasValue ? best.Price.Value.ToString("F2") : "--",
                        FormatTime(latest.EventTime),
                        TranslateEventType(latest.EventType),
                        manualStatus,
                        strategyNames.Length == 0 ? "命中策略：--" : $"命中策略：{string.Join(" / ", strategyNames)}",
                        $"最近命中 {FormatTime(latest.EventTime)} | 命中 {byScore.Length} 次 | 策略 {strategyCount} 个 | 事件 {TranslateEventType(latest.EventType)}",
                        reviewText,
                        $"{best.Symbol} {best.Name} · {best.StrategyName}",
                        string.IsNullOrWhiteSpace(best.Risk) ? "暂无风险说明" : best.Risk);
                })
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.EventTime)
                .ToArray();

            _dailyHitItems = dailyHits;
            _dailyHitCount = dailyHits.Length;
            PopulateDailyHitFilters(dailyHits);
            ApplyDailyHitFilters();
            DailyHitSummaryText.Text = dailyHits.Length == 0
                ? $"{tradingDate:yyyy-MM-dd} 当前日期暂无每日命中，可切换日期或等待扫描。"
                : $"{tradingDate:yyyy-MM-dd} 命中股票 {dailyHits.Length} 只。可搜索、筛选或点击表头排序。";
            if (dailyHits.Length == 0)
            {
                DailyHitDataGrid.SelectedItem = null;
                ClearDailyHitKLine();
            }

            ApplyWorkspaceVisibility();
            if (dailyHits.Length > 0 && DailyHitDataGrid.SelectedItem is null)
            {
                DailyHitDataGrid.SelectedItem = dailyHits[0];
            }
        }
        catch (Exception ex)
        {
            _dailyHitItems = [];
            DailyHitDataGrid.ItemsSource = null;
            _dailyHitCount = 0;
            ClearDailyHitKLine();
            DailyHitSummaryText.Text = $"每日命中加载失败：{ex.Message}";
            ApplyWorkspaceVisibility();
        }
    }

    private void PopulateDailyHitFilters(IReadOnlyList<DailyHitDisplay> items)
    {
        var selectedStrategy = DailyHitStrategyFilterComboBox.SelectedItem as string;
        var selectedStatus = DailyHitStatusFilterComboBox.SelectedItem as string;
        var strategies = items
            .SelectMany(item => item.StrategyNames)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.CurrentCulture)
            .Prepend("全部策略")
            .ToArray();
        var statuses = items
            .Select(item => item.ManualStatusText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.CurrentCulture)
            .Prepend("全部状态")
            .ToArray();

        DailyHitStrategyFilterComboBox.ItemsSource = strategies;
        DailyHitStrategyFilterComboBox.SelectedItem = strategies.Contains(selectedStrategy, StringComparer.OrdinalIgnoreCase)
            ? selectedStrategy
            : "全部策略";
        DailyHitStatusFilterComboBox.ItemsSource = statuses;
        DailyHitStatusFilterComboBox.SelectedItem = statuses.Contains(selectedStatus, StringComparer.OrdinalIgnoreCase)
            ? selectedStatus
            : "全部状态";
    }

    private void ApplyDailyHitFilters()
    {
        if (DailyHitDataGrid is null || DailyHitSearchBox is null || DailyHitStrategyFilterComboBox is null || DailyHitStatusFilterComboBox is null)
        {
            return;
        }

        var selectedSymbol = (DailyHitDataGrid.SelectedItem as DailyHitDisplay)?.Symbol;
        var search = DailyHitSearchBox.Text.Trim();
        var strategy = DailyHitStrategyFilterComboBox.SelectedItem as string;
        var status = DailyHitStatusFilterComboBox.SelectedItem as string;
        var filtered = _dailyHitItems
            .Where(item => string.IsNullOrWhiteSpace(search)
                || item.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(strategy)
                || strategy == "全部策略"
                || item.StrategyNames.Contains(strategy, StringComparer.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(status)
                || status == "全部状态"
                || string.Equals(item.ManualStatusText, status, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.EventTime)
            .ToArray();

        DailyHitDataGrid.ItemsSource = filtered;
        DailyHitDataGrid.SelectedItem = selectedSymbol is null
            ? filtered.FirstOrDefault()
            : filtered.FirstOrDefault(item => string.Equals(item.Symbol, selectedSymbol, StringComparison.OrdinalIgnoreCase))
                ?? filtered.FirstOrDefault();

        if (_dailyHitItems.Count > 0)
        {
            var tradingDate = DateOnly.FromDateTime(StockPoolHitDatePicker.SelectedDate ?? DateTime.Today);
            DailyHitSummaryText.Text = $"{tradingDate:yyyy-MM-dd} 命中股票 {_dailyHitItems.Count} 只，当前显示 {filtered.Length} 只。";
        }
    }

    private static string BuildDailyHitManualStatus(OpportunityDto? opportunity)
    {
        if (opportunity is null || string.IsNullOrWhiteSpace(opportunity.ManualTag))
        {
            return "未处理";
        }

        return TranslateManualTag(opportunity.ManualTag);
    }

    private static string BuildDailyHitReviewText(OpportunityDto opportunity)
    {
        var manual = TranslateManualTag(opportunity.ManualTag);
        var status = TranslateOpportunityStatus(opportunity.Status);
        var note = string.IsNullOrWhiteSpace(opportunity.Note)
            ? string.Empty
            : $" | {opportunity.Note.Trim()}";
        return string.IsNullOrWhiteSpace(manual)
            ? $"{status}{note}"
            : $"{manual} / {status}{note}";
    }

    private Task SelectDailyHitAsync(DailyHitDisplay item)
    {
        _selectedOpportunityId = null;
        _selectedSymbol = item.Symbol;
        _selectedName = item.Name;
        FooterText.Text = $"已选中每日命中：{item.Symbol} {item.Name}，{item.StrategyName}，强度 {item.Score:F2}";
        return Task.CompletedTask;
    }

    private void ClearDailyHitKLine()
    {
        if (!_showStockPools)
        {
            return;
        }

        _selectedOpportunityId = null;
        _selectedSymbol = null;
        _selectedName = null;
        ChartTitleText.Text = "K线分析";
        ChartCaptionText.Text = "选择每日命中股票后显示对应个股走势、均线、成交量、MACD 与筹码分布。";
        KLineChart.SymbolName = "未选择";
        KLineChart.Candles = [];
        KLineChart.IndicatorSeries = null;
        KLineChart.TradeMarkers = [];
    }

    private async void MinutePeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("minute");
    }

    private async void FiveDayPeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("five-day");
    }

    private async void M1PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("m1");
    }

    private async void M5PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("m5");
    }

    private async void M15PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("m15");
    }

    private async void M30PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("m30");
    }

    private async void M60PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("m60");
    }

    private async void DayPeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("day");
    }

    private async void WeekPeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("week");
    }

    private async void MonthPeriodButton_Click(object sender, RoutedEventArgs e)
    {
        await SetKLinePeriodAsync("month");
    }

    private async void MacdIndicatorButton_Click(object sender, RoutedEventArgs e)
    {
        await SetIndicatorModeAsync("MACD");
    }

    private async void KdjIndicatorButton_Click(object sender, RoutedEventArgs e)
    {
        await SetIndicatorModeAsync("KDJ");
    }

    private async void RsiIndicatorButton_Click(object sender, RoutedEventArgs e)
    {
        await SetIndicatorModeAsync("RSI");
    }

    private async void OpportunityListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingOpportunityList)
        {
            return;
        }

        if (sender is not ListBox listBox || listBox.SelectedItem is not OpportunityDisplay opportunity)
        {
            ClearSnapshot();
            return;
        }

        _selectedOpportunityId = opportunity.Id;
        await RunUiActionAsync(async token =>
        {
            var detail = await _apiClient.GetOpportunityDetailAsync(opportunity.Id, token);
            if (detail is null)
            {
                ClearSnapshot();
                return;
            }

            await ApplySnapshotAsync(detail, token, refreshKLine: KLinePanel.Visibility == Visibility.Visible);
        });
    }

    private async void CopyOpportunitySymbolButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.Tag is not string symbol || string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        symbol = symbol.Trim();
        var copied = false;
        for (var attempt = 0; attempt < 3 && !copied; attempt++)
        {
            try
            {
                Clipboard.SetText(symbol);
                copied = true;
            }
            catch (ExternalException) when (attempt < 2)
            {
                await Task.Delay(80);
            }
        }

        if (!copied)
        {
            button.ToolTip = "复制失败，请重试";
            return;
        }

        var originalContent = button.Content;
        var successIcon = new TextBlock
        {
            Text = "✓",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("PrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        button.Content = successIcon;
        button.ToolTip = $"已复制 {symbol}";
        await Task.Delay(1000);

        if (button.IsLoaded && ReferenceEquals(button.Content, successIcon))
        {
            button.Content = originalContent;
            button.ToolTip = "复制股票代码";
        }
    }

    private async void OpenOpportunityKLineButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (element.DataContext is DailyHitDisplay dailyHit)
        {
            await RunUiActionAsync(async token =>
            {
                EnsureFloatingKLineWindow();
                _floatingKLineWindow!.Show();
                if (_floatingKLineWindow.WindowState == WindowState.Minimized)
                {
                    _floatingKLineWindow.WindowState = WindowState.Normal;
                }

                _floatingKLineWindow.Activate();
                await _floatingKLineWindow.LoadSymbolAsync(dailyHit.Symbol, dailyHit.Name, null, token);
                FooterText.Text = $"已在独立窗口打开K线：{dailyHit.Symbol} {dailyHit.Name}";
            });
            return;
        }

        if (element.DataContext is not OpportunityDisplay opportunity)
        {
            return;
        }

        await RunUiActionAsync(async token =>
        {
            var detail = await _apiClient.GetOpportunityDetailAsync(opportunity.Id, token);
            EnsureFloatingKLineWindow();
            _floatingKLineWindow!.Show();
            if (_floatingKLineWindow.WindowState == WindowState.Minimized)
            {
                _floatingKLineWindow.WindowState = WindowState.Normal;
            }

            _floatingKLineWindow.Activate();
            await _floatingKLineWindow.LoadSymbolAsync(
                opportunity.Symbol,
                opportunity.Name,
                detail?.LatestEvent,
                token);
            FooterText.Text = $"已在独立窗口打开K线：{opportunity.Symbol} {opportunity.Name}";
        });
    }

    private void EnsureFloatingKLineWindow()
    {
        if (_floatingKLineWindow is { IsLoaded: true })
        {
            return;
        }

        _floatingKLineWindow = new KLineFloatingWindow(_apiClient)
        {
            Owner = this
        };
        _floatingKLineWindow.Closed += (_, _) => _floatingKLineWindow = null;
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var statusTask = _apiClient.GetMonitorStatusAsync(cancellationToken);
            var marketDataStatusTask = _apiClient.GetMarketDataStatusAsync(cancellationToken);
            var historicalDataStatusTask = _apiClient.GetHistoricalDataUpdateStatusAsync(cancellationToken);
            var activeJobsTask = _apiClient.GetActiveJobsAsync(cancellationToken);
            var latestJobTask = _apiClient.GetLatestJobAsync(null, cancellationToken);
            var marketSentimentTask = _apiClient.GetMarketSentimentAsync(cancellationToken);
            var marketSentimentDataSourcesTask = _showMarketSentiment
                ? _apiClient.GetMarketSentimentDataSourcesAsync(cancellationToken)
                : Task.FromResult<IReadOnlyList<MarketSentimentDataSourceStatusDto>>([]);
            var marketSentimentRegimesTask = _showMarketSentiment
                ? _apiClient.GetMarketSentimentRegimesAsync(120, cancellationToken)
                : Task.FromResult<IReadOnlyList<MarketSentimentRegimeDto>>([]);

            var opportunitiesTask = !_showResearchPage && !_showMarketSentiment
                ? _apiClient.GetOpportunitiesAsync(_opportunityView, cancellationToken)
                : Task.FromResult<IReadOnlyList<OpportunityDto>>([]);
            var eventsTask = !_showResearchPage && !_showMarketSentiment
                ? _apiClient.GetSignalEventsAsync(cancellationToken)
                : Task.FromResult<IReadOnlyList<SignalEventDto>>([]);
            var sectorHeatTask = !_showResearchPage
                ? _apiClient.GetSectorHeatAsync(5000, cancellationToken)
                : Task.FromResult<IReadOnlyList<HeatBoardItemDto>>([]);
            var conceptHeatTask = !_showResearchPage
                ? _apiClient.GetConceptHeatAsync(5000, cancellationToken)
                : Task.FromResult<IReadOnlyList<HeatBoardItemDto>>([]);
            var strategiesTask = _showStrategyCenter
                ? _apiClient.GetStrategiesAsync(cancellationToken)
                : Task.FromResult<IReadOnlyList<StrategyDefinitionDto>>([]);
            var strategyRulesTask = _showStrategyCenter
                ? _apiClient.GetMarketSentimentStrategyRulesAsync(cancellationToken)
                : Task.FromResult<MarketSentimentStrategyRulesDto?>(null);
            var historicalSignalsTask = _showHistory
                ? _apiClient.GetHistoricalSignalsAsync(
                    _historyTradingDate,
                    _historySymbol,
                    _historyStrategyCode,
                    80,
                    cancellationToken)
                : Task.FromResult<IReadOnlyList<HistoricalSignalDto>>([]);
            var strategyPerformanceTask = _showHistory
                ? _apiClient.GetStrategyPerformanceAsync(
                    _historyTradingDate,
                    20,
                    cancellationToken)
                : Task.FromResult<IReadOnlyList<StrategyPerformanceDto>>([]);
            var predictionReviewTask = _showPredictionReview
                ? _apiClient.GetPredictionReviewAsync(_predictionDate, cancellationToken)
                : Task.FromResult<PredictionReviewDto?>(null);

            await Task.WhenAll([
                statusTask,
                marketDataStatusTask,
                opportunitiesTask,
                eventsTask,
                sectorHeatTask,
                conceptHeatTask,
                historicalDataStatusTask,
                activeJobsTask,
                latestJobTask,
                marketSentimentTask,
                marketSentimentDataSourcesTask,
                marketSentimentRegimesTask,
                strategiesTask,
                strategyRulesTask,
                historicalSignalsTask,
                strategyPerformanceTask,
                predictionReviewTask
            ]);

            ApplyStatus(statusTask.Result, marketDataStatusTask.Result);
            ApplyHistoricalDataUpdateStatus(historicalDataStatusTask.Result);
            ApplyBackgroundJobs(activeJobsTask.Result, latestJobTask.Result);
            ApplyMarketSentiment(marketSentimentTask.Result);
            ApplyMarketSentimentPhaseTwo(
                marketSentimentTask.Result,
                marketSentimentDataSourcesTask.Result,
                marketSentimentRegimesTask.Result);
            if (!_showResearchPage && !_showMarketSentiment)
            {
                var selectedOpportunityId = _selectedOpportunityId;
                var opportunityDisplays = opportunitiesTask.Result
                    .Select(MapOpportunityDisplay)
                    .ToArray();
                var realtimeDisplays = opportunityDisplays
                    .Where(IsRealtimePoolOpportunity)
                    .ToArray();
                _isRefreshingOpportunityList = true;
                try
                {
                    OpportunityListBox.ItemsSource = realtimeDisplays;
                    OpportunityListBox.SelectedItem = selectedOpportunityId.HasValue
                        ? realtimeDisplays.FirstOrDefault(item => item.Id == selectedOpportunityId.Value)
                        : null;
                }
                finally
                {
                    _isRefreshingOpportunityList = false;
                }

                SignalEventListBox.ItemsSource = eventsTask.Result
                    .Select(item => new SignalEventDisplay(
                        FormatDateTime(item.EventTime),
                        TranslateEventType(item.EventType),
                        item.Symbol,
                        item.Name,
                        BuildStrategySummary(item),
                        $"分数 {item.Score:F2}",
                        item.Reason))
                    .ToArray();
                if (_showSectorHeat)
                {
                    ApplyHeatBoard(sectorHeatTask.Result, SectorHeatSummaryText, SectorHeatItemsControl, "行业热度");
                }

                if (_showConceptHeat)
                {
                    ApplyHeatBoard(conceptHeatTask.Result, ConceptHeatSummaryText, ConceptHeatItemsControl, "概念热度");
                }

                _mappingSectorHeatItems = sectorHeatTask.Result;
                _mappingConceptHeatItems = conceptHeatTask.Result;
                ApplyMappingHeatFilters();
            }

            if (_showHistory)
            {
                ApplyHistoryStats(historicalSignalsTask.Result, strategyPerformanceTask.Result);
            }

            if (_showStrategyCenter)
            {
                ApplyStrategyCenter(strategiesTask.Result);
                ApplyStrategySentimentRules(strategyRulesTask.Result);
            }

            if (_showPredictionReview)
            {
                ApplyPredictionReview(predictionReviewTask.Result);
            }

            FooterText.Text = $"后端已连接 | 数据源：{BuildMarketDataLabel(marketDataStatusTask.Result)} | 最近刷新：{DateTime.Now:HH:mm:ss}";

            await RefreshSelectedOpportunityAsync(cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Warn(ex, "Desktop refresh timed out. SectorHeatVisible={SectorHeatVisible} ConceptHeatVisible={ConceptHeatVisible} ResearchVisible={ResearchVisible}", _showSectorHeat, _showConceptHeat, _showResearchPage);
            FooterText.Text = $"后端响应较慢：接口超过等待时间。服务仍可访问，请稍后刷新或减少同时打开的热度/复盘视图。{ex.Message}";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Desktop refresh failed. SectorHeatVisible={SectorHeatVisible} ConceptHeatVisible={ConceptHeatVisible} ResearchVisible={ResearchVisible}", _showSectorHeat, _showConceptHeat, _showResearchPage);
            FooterText.Text = $"后端不可用：{ex.Message}";
        }
    }

    private async Task StartRealtimeClientAsync()
    {
        Logger.Info("Desktop realtime client starting.");
        try
        {
            _realtimeClient.MessageReceived += async (_, _) =>
            {
                await Dispatcher.InvokeAsync(async () => await RefreshAsync());
            };

            await _realtimeClient.StartAsync(CancellationToken.None);
            Logger.Info("Desktop realtime client connected.");
            FooterText.Text = "实时推送已连接。";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Desktop realtime client unavailable; polling remains active.");
            FooterText.Text = $"实时推送不可用，已使用轮询刷新：{ex.Message}";
        }
    }

    private async Task RefreshSelectedOpportunityAsync(CancellationToken cancellationToken)
    {
        if (_selectedOpportunityId is null)
        {
            return;
        }

        var refreshed = OpportunityListBox.Items
            .OfType<OpportunityDisplay>()
            .FirstOrDefault(item => item.Id == _selectedOpportunityId.Value);

        var detail = await _apiClient.GetOpportunityDetailAsync(refreshed?.Id ?? _selectedOpportunityId.Value, cancellationToken);
        if (detail is not null)
        {
            await ApplySnapshotAsync(detail, cancellationToken, refreshKLine: false);
        }
    }

    private static string FormatTime(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToLocalTime().ToString("HH:mm:ss") : "--";
    }

    private static string FormatDateTime(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "--";
    }

    private void ApplyStatus(MonitorStatusDto? status, MarketDataStatusDto? marketDataStatus)
    {
        if (status is null)
        {
            StatusText.Text = "市场: --  |  监控: --  |  数据源: --  |  上次扫描: --  |  下次扫描: --";
            SummaryText.Text = "活跃机会: --   今日新增: --   消失: --   重点跟踪: --";
            return;
        }

        StatusText.Text = $"市场: {TranslateMarketStatus(status.MarketStatus)}  |  监控: {TranslateMonitorStatus(status.MonitorStatus)}  |  数据源: {BuildMarketDataLabel(marketDataStatus)}  |  上次扫描: {FormatTime(status.LastScanTime)}  |  下次扫描: {FormatTime(status.NextScanTime)}";
        SummaryText.Text = $"活跃机会: {status.ActiveOpportunityCount}   今日新增: {status.TodayNewCount}   消失: {status.DisappearedCount}   重点跟踪: {status.FocusedCount}";
    }

    private void ApplyHistoricalDataUpdateStatus(HistoricalDataUpdateStatusDto? status)
    {
        if (status is null)
        {
            HistoricalDataStatusText.Text = "历史数据：未检测";
            HistoricalDataMissingText.Text = "缺口：--";
            return;
        }

        var runState = status.IsRunning ? "更新中" : "空闲";
        var latestDate = status.LatestTradingDate.HasValue
            ? status.LatestTradingDate.Value.ToString("yyyy-MM-dd")
            : "--";
        var lastRun = status.LastFinishedAt.HasValue
            ? status.LastFinishedAt.Value.ToString("HH:mm:ss")
            : "--";

        HistoricalDataStatusText.Text = $"历史数据：{runState} | 最新交易日 {latestDate} | 上次完成 {lastRun}";
        HistoricalDataMissingText.Text = status.MissingTradingDates.Length == 0
            ? "缺口：无"
            : $"缺口：{string.Join(',', status.MissingTradingDates.Select(item => item.ToString("MM-dd")))}";
    }

    private async Task RefreshBackgroundJobStatusAsync(CancellationToken cancellationToken = default)
    {
        var job = _latestBackgroundJobId.HasValue
            ? await _apiClient.GetJobAsync(_latestBackgroundJobId.Value, cancellationToken)
            : await _apiClient.GetLatestJobAsync(null, cancellationToken);
        ApplyBackgroundJobs([], job);
    }

    private void ApplyBackgroundJobs(IReadOnlyList<BackgroundJobDto> activeJobs, BackgroundJobDto? latestJob)
    {
        var displayJob = activeJobs
            .OrderByDescending(item => item.StartedAt ?? item.CreatedAt)
            .FirstOrDefault()
            ?? latestJob;
        if (displayJob is null)
        {
            BackgroundJobStatusText.Text = string.Empty;
            BackgroundJobStatusText.ToolTip = "后台任务：空闲";
            BackgroundJobProgressBar.Value = 100;
            BackgroundJobLogButton.IsEnabled = false;
            return;
        }

        _latestBackgroundJobId = displayJob.Id;
        BackgroundJobLogButton.IsEnabled = true;
        BackgroundJobProgressBar.Value = displayJob.ProgressPercent;
        var status = TranslateJobStatus(displayJob.Status);
        BackgroundJobStatusText.Text = string.Empty;
        BackgroundJobStatusText.ToolTip = $"后台任务：{displayJob.Title} | {status} {displayJob.ProgressPercent}% | {displayJob.CurrentStep}";

        if (string.Equals(displayJob.Type, "next-day-prediction", StringComparison.OrdinalIgnoreCase)
            && string.Equals(displayJob.Status, "Succeeded", StringComparison.OrdinalIgnoreCase)
            && _showPredictionReview
            && _lastAutoRefreshedPredictionJobId != displayJob.Id)
        {
            _lastAutoRefreshedPredictionJobId = displayJob.Id;
            _ = Dispatcher.InvokeAsync(async () => await RefreshPredictionReviewAsync());
        }
    }

    private void ShowBackgroundJobLogDialog(BackgroundJobDto? job, IReadOnlyList<BackgroundJobLogDto> logs)
    {
        var builder = new StringBuilder();
        if (job is not null)
        {
            builder.AppendLine($"{job.Title} | {TranslateJobStatus(job.Status)} | {job.ProgressPercent}%");
            builder.AppendLine($"阶段：{job.CurrentStep}");
            if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
            {
                builder.AppendLine();
                builder.AppendLine("错误：");
                builder.AppendLine(job.ErrorMessage);
            }

            if (!string.IsNullOrWhiteSpace(job.FixSuggestion))
            {
                builder.AppendLine();
                builder.AppendLine("修复建议：");
                builder.AppendLine(job.FixSuggestion);
            }

            builder.AppendLine();
        }

        builder.AppendLine("日志：");
        foreach (var log in logs)
        {
            builder.AppendLine($"[{log.CreatedAt:HH:mm:ss}] {log.Stream}: {log.Message}");
        }

        var textBox = new TextBox
        {
            Text = builder.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(10)
        };
        var dialog = new Window
        {
            Title = "后台任务日志",
            Owner = this,
            Content = textBox,
            Width = 860,
            Height = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.ShowDialog();
    }

    private static string TranslateJobStatus(string status)
    {
        return status switch
        {
            "Queued" => "排队中",
            "Running" => "运行中",
            "Succeeded" => "已完成",
            "Failed" => "失败",
            "Canceled" => "已取消",
            _ => status
        };
    }

    private void ApplyMarketMappingUpdateStatus(MarketMappingUpdateStatusDto? status)
    {
        if (status is null)
        {
            MappingUpdateStatusText.Text = "概念行业：未检测";
            MappingPageStateText.Visibility = Visibility.Collapsed;
            MappingPageSummaryText.Text = "行业映射 -- | 概念映射 -- | 尚无更新时间";
            MappingPageErrorText.Visibility = Visibility.Collapsed;
            return;
        }

        var runState = status.IsRunning ? "更新中" : "空闲";
        var lastRun = status.LastFinishedAt.HasValue
            ? status.LastFinishedAt.Value.ToString("HH:mm:ss")
            : "--";
        MappingUpdateStatusText.Text =
            $"概念行业：{runState} | 行业 {status.SectorMappingCount} | 概念 {status.ConceptMappingCount} | 上次 {lastRun}";
        MappingPageStateText.Text = status.IsRunning ? "更新中" : string.IsNullOrWhiteSpace(status.LastError) ? string.Empty : "更新失败";
        MappingPageStateText.Visibility = status.IsRunning || !string.IsNullOrWhiteSpace(status.LastError)
            ? Visibility.Visible
            : Visibility.Collapsed;
        var updateTime = status.IsRunning ? status.LastStartedAt : status.LastFinishedAt;
        MappingPageSummaryText.Text =
            $"行业映射 {status.SectorMappingCount:N0} | 概念映射 {status.ConceptMappingCount:N0} | 更新时间 {updateTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "--"}";
        MappingPageErrorText.Text = status.LastError ?? string.Empty;
        MappingPageErrorText.Visibility = string.IsNullOrWhiteSpace(status.LastError)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ApplyMarketSentiment(MarketSentimentSnapshotDto? sentiment)
    {
        if (sentiment is null)
        {
            MarketSentimentScoreText.Text = "--";
            MarketSentimentLevelText.Text = "等待";
            MarketSentimentSummaryText.Text = "A股情绪：等待后端数据";
            MarketSentimentStrategyText.Text = "策略环境：等待情绪数据刷新。";
            MarketSentimentWarningText.Text = "接口未返回情绪快照。";
            MarketSentimentHeatFill.Width = 0;
            MarketSentimentAlertFill.Width = 0;
            MarketSentimentPageScoreText.Text = "--";
            MarketSentimentPageLevelText.Text = "等待";
            MarketSentimentPageSummaryText.Text = "等待实时情绪快照。";
            MarketSentimentPageStrategyText.Text = "等待情绪数据刷新。";
            MarketSentimentPageNeedle.SetValue(Canvas.LeftProperty, 0d);
            ResetMarketSentimentCategory(BreadthCard, BreadthScoreText, BreadthDescriptionText, BreadthLevelText, BreadthBarFill);
            ResetMarketSentimentCategory(TradingCard, TradingScoreText, TradingDescriptionText, TradingLevelText, TradingBarFill);
            ResetMarketSentimentCategory(RiskCard, RiskScoreText, RiskDescriptionText, RiskLevelText, RiskBarFill);
            ResetMarketSentimentCategory(CapitalCard, CapitalScoreText, CapitalDescriptionText, CapitalLevelText, CapitalBarFill);
            ResetMarketSentimentCategory(ExternalCard, ExternalScoreText, ExternalDescriptionText, ExternalLevelText, ExternalBarFill);
            return;
        }

        var temperatureScore = (double)sentiment.TemperatureScore;
        var sentimentBrush = GetSentimentBrush(temperatureScore);
        var sentimentLightBrush = GetSentimentLightBrush(temperatureScore);
        MarketSentimentScoreText.Text = sentiment.TemperatureScore.ToString("F0");
        MarketSentimentScoreText.Foreground = sentimentBrush;
        MarketSentimentLevelText.Text = sentiment.Level;
        MarketSentimentLevelText.Foreground = sentimentBrush;
        MarketSentimentLevelBadge.Background = sentimentLightBrush;
        MarketSentimentHeatFill.Width = ScaleScoreWidth(temperatureScore, 186);
        MarketSentimentHeatFill.Background = sentimentBrush;
        MarketSentimentPageScoreText.Text = sentiment.TemperatureScore.ToString("F0");
        MarketSentimentPageScoreText.Foreground = sentimentBrush;
        MarketSentimentPageLevelText.Text = sentiment.Level;
        MarketSentimentPageLevelText.Foreground = sentimentBrush;
        MarketSentimentPageLevelBadge.Background = sentimentLightBrush;
        MarketSentimentPageLevelBadge.BorderBrush = sentimentBrush;
        MarketSentimentPageSummaryText.Text = $"{sentiment.ProviderName} | {sentiment.SnapshotTime.ToLocalTime():HH:mm:ss} | {sentiment.Summary}";
        MarketSentimentPageStrategyText.Text = BuildMarketSentimentFilterAdvice(sentiment.Level, sentiment.TemperatureScore);
        MarketSentimentPageNeedle.SetValue(Canvas.LeftProperty, ScaleScoreWidth(temperatureScore, 360));
        MarketSentimentAlertFill.Width = ScaleScoreWidth(temperatureScore, 286);
        MarketSentimentAlertFill.Background = sentimentBrush;
        MarketSentimentAlertCard.Background = sentimentLightBrush;
        MarketSentimentAlertCard.BorderBrush = sentimentBrush;
        MarketSentimentSummaryText.Text = $"{sentiment.ProviderName} | {sentiment.SnapshotTime.ToLocalTime():HH:mm:ss} | {sentiment.DataQuality}";

        ApplyMarketSentimentCategory(sentiment, "breadth", BreadthCard, BreadthScoreText, BreadthDescriptionText, BreadthLevelText, BreadthBarFill);
        ApplyMarketSentimentCategory(sentiment, "trading", TradingCard, TradingScoreText, TradingDescriptionText, TradingLevelText, TradingBarFill);
        ApplyMarketSentimentCategory(sentiment, "risk", RiskCard, RiskScoreText, RiskDescriptionText, RiskLevelText, RiskBarFill);
        ApplyMarketSentimentCategory(sentiment, "capital", CapitalCard, CapitalScoreText, CapitalDescriptionText, CapitalLevelText, CapitalBarFill);
        ApplyMarketSentimentCategory(sentiment, "external", ExternalCard, ExternalScoreText, ExternalDescriptionText, ExternalLevelText, ExternalBarFill);

        MarketSentimentStrategyText.Text = $"{sentiment.Level}：{BuildMarketSentimentStrategyHint(sentiment.Level)}";
        MarketSentimentStrategyText.Foreground = temperatureScore >= 75 ? Brushes.Firebrick : Brushes.Black;
        MarketSentimentWarningText.Text = sentiment.Warnings.Count == 0
            ? sentiment.Summary
            : sentiment.Warnings[0];
    }

    private static void ApplyMarketSentimentCategory(
        MarketSentimentSnapshotDto sentiment,
        string code,
        Border card,
        TextBlock scoreText,
        TextBlock descriptionText,
        TextBlock levelText,
        Border barFill)
    {
        var category = sentiment.Categories.FirstOrDefault(item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
        if (category is null)
        {
            ResetMarketSentimentCategory(card, scoreText, descriptionText, levelText, barFill);
            return;
        }

        var categoryScore = (double)category.Score;
        var scoreBrush = GetSentimentBrush(categoryScore);
        scoreText.Text = category.Score.ToString("F0");
        scoreText.Foreground = scoreBrush;
        descriptionText.Text = category.Description;
        descriptionText.ToolTip = category.Description;
        levelText.Text = GetSentimentScoreLabel(categoryScore);
        levelText.Foreground = scoreBrush;
        barFill.Width = ScaleScoreWidth(categoryScore, 132);
        barFill.Background = scoreBrush;
        card.Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        card.BorderBrush = new SolidColorBrush(Color.FromRgb(222, 227, 234));
    }

    private void ApplyMarketSentimentPhaseTwo(
        MarketSentimentSnapshotDto? sentiment,
        IReadOnlyList<MarketSentimentDataSourceStatusDto> dataSources,
        IReadOnlyList<MarketSentimentRegimeDto> regimes)
    {
        if (sentiment is null)
        {
            MarketSentimentPositionText.Text = "仓位建议：--";
            MarketSentimentFilterText.Text = "策略过滤：等待情绪数据。";
            MarketSentimentDataSourceItemsControl.ItemsSource = Array.Empty<MarketSentimentDataSourceDisplay>();
            MarketSentimentMetricItemsControl.ItemsSource = Array.Empty<MarketSentimentMetricDisplay>();
            MarketSentimentRegimeItemsControl.ItemsSource = Array.Empty<MarketSentimentRegimeDisplay>();
            return;
        }

        MarketSentimentPositionText.Text = BuildMarketSentimentPositionAdvice(sentiment.Level, sentiment.TemperatureScore);
        MarketSentimentFilterText.Text = BuildMarketSentimentFilterAdvice(sentiment.Level, sentiment.TemperatureScore);
        MarketSentimentDataSourceItemsControl.ItemsSource = dataSources
            .Select(item => new MarketSentimentDataSourceDisplay(
                item.Code,
                TranslateSourceStatus(item.Status),
                GetSourceStatusBrush(item.Status)))
            .ToArray();
        MarketSentimentMetricItemsControl.ItemsSource = sentiment.Metrics
            .Where(item => item.CategoryCode is "breadth" or "trading" or "risk" or "capital" or "external")
            .Take(12)
            .Select(item => new MarketSentimentMetricDisplay(
                item.Name,
                item.IsAvailable ? item.DisplayValue : "暂无",
                TranslateMetricSourceStatus(item.SourceStatus)))
            .ToArray();
        MarketSentimentRegimeItemsControl.ItemsSource = regimes
            .Take(6)
            .Select(item => new MarketSentimentRegimeDisplay(
                item.Label,
                $"{item.StartScore:F0}->{item.EndScore:F0}",
                $"{item.StartTime.ToLocalTime():MM-dd HH:mm} 至 {item.EndTime.ToLocalTime():MM-dd HH:mm}，{item.SnapshotCount} 次快照",
                GetSentimentBrush((double)item.EndScore)))
            .ToArray();
    }

    private static string BuildMarketSentimentPositionAdvice(string level, decimal score)
    {
        return level switch
        {
            "冰点" => $"仓位建议：0-2成，温度 {score:F1}，只观察修复。",
            "偏冷" => $"仓位建议：2-4成，温度 {score:F1}，控制试错。",
            "中性" => $"仓位建议：4-6成，温度 {score:F1}，正常筛选。",
            "偏热" => $"仓位建议：5-7成，温度 {score:F1}，聚焦主线。",
            "过热" => $"仓位建议：降高位、控回撤，温度 {score:F1}。",
            _ => $"仓位建议：等待确认，温度 {score:F1}。"
        };
    }

    private static string BuildMarketSentimentFilterAdvice(string level, decimal score)
    {
        return level switch
        {
            "冰点" => "策略过滤：平台突破和追高信号降级观察，等待情绪修复。",
            "偏冷" => "策略过滤：进攻型信号降权，优先低位承接和防守形态。",
            "中性" => "策略过滤：按原策略运行，以板块强弱和个股结构排序。",
            "偏热" => "策略过滤：主线共振、强趋势策略加权，跟踪拥挤度。",
            "过热" when score >= 80m => "策略过滤：高位进攻降权，炸板/回落风险提示增强。",
            "过热" => "策略过滤：高位信号降权，优先兑现和控制回撤。",
            _ => "策略过滤：等待情绪数据。"
        };
    }

    private static string TranslateMetricSourceStatus(string sourceStatus)
    {
        return sourceStatus switch
        {
            "Configured" => "外部配置",
            "Realtime" => "实时行情",
            "Estimated" => "估算",
            "Unavailable" => "暂未接入",
            "Disabled" => "未启用",
            _ => sourceStatus
        };
    }

    private static string TranslateSourceStatus(string status)
    {
        return status switch
        {
            "Available" => "可用",
            "Configured" => "已配置",
            "Unavailable" => "暂无",
            "Disabled" => "未启用",
            _ => status
        };
    }

    private static Brush GetSourceStatusBrush(string status)
    {
        return status switch
        {
            "Available" or "Configured" => new SolidColorBrush(Color.FromRgb(15, 122, 58)),
            "Unavailable" => new SolidColorBrush(Color.FromRgb(154, 91, 0)),
            _ => Brushes.Gray
        };
    }

    private static void ResetMarketSentimentCategory(
        Border card,
        TextBlock scoreText,
        TextBlock descriptionText,
        TextBlock levelText,
        Border barFill)
    {
        scoreText.Text = "--";
        scoreText.Foreground = Brushes.Black;
        descriptionText.Text = "暂无数据";
        descriptionText.ToolTip = null;
        levelText.Text = "--";
        levelText.Foreground = new SolidColorBrush(Color.FromRgb(134, 145, 160));
        barFill.Width = 0;
        card.Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        card.BorderBrush = new SolidColorBrush(Color.FromRgb(227, 234, 241));
    }

    private static double ScaleScoreWidth(double score, double maxWidth)
    {
        return Math.Clamp(score, 0, 100) / 100d * maxWidth;
    }

    private static string GetSentimentScoreLabel(double score)
    {
        return score switch
        {
            >= 90 => "爆表",
            >= 75 => "过热",
            >= 60 => "偏热",
            >= 45 => "中性",
            >= 30 => "偏冷",
            _ => "冰点"
        };
    }

    private static SolidColorBrush GetSentimentBrush(double score)
    {
        var color = score switch
        {
            >= 90 => Color.FromRgb(220, 38, 38),
            >= 75 => Color.FromRgb(245, 92, 0),
            >= 60 => Color.FromRgb(255, 149, 0),
            >= 45 => Color.FromRgb(0, 122, 255),
            >= 30 => Color.FromRgb(52, 120, 246),
            _ => Color.FromRgb(90, 112, 138)
        };

        return new SolidColorBrush(color);
    }

    private static SolidColorBrush GetSentimentLightBrush(double score)
    {
        var color = score switch
        {
            >= 90 => Color.FromRgb(255, 241, 242),
            >= 75 => Color.FromRgb(255, 247, 237),
            >= 60 => Color.FromRgb(255, 251, 235),
            >= 45 => Color.FromRgb(239, 246, 255),
            >= 30 => Color.FromRgb(244, 247, 251),
            _ => Color.FromRgb(248, 250, 252)
        };

        return new SolidColorBrush(color);
    }

    private static string BuildMarketSentimentStrategyHint(string level)
    {
        return level switch
        {
            "冰点" => "市场恐慌或极弱，优先观察修复信号，降低追高权重。",
            "偏冷" => "谨慎观察，主线共振需要更高确认度。",
            "中性" => "结构行情，按板块强弱和个股形态分层筛选。",
            "偏热" => "赚钱效应较好，主线共振优先级提高，注意拥挤回撤。",
            "过热" => "情绪过热，控制仓位，关注炸板和高位回撤风险。",
            _ => "等待更多市场数据确认。"
        };
    }

    private static string BuildMarketDataLabel(MarketDataStatusDto? status)
    {
        if (status is null)
        {
            return "--";
        }

        return status.ConfiguredProvider == status.ActiveProvider
            ? status.ActiveProvider
            : $"{status.ConfiguredProvider}->{status.ActiveProvider}";
    }

    private static void ApplyHeatBoard(
        IReadOnlyList<HeatBoardItemDto> items,
        TextBlock summaryText,
        ItemsControl itemsControl,
        string title)
    {
        summaryText.Text = $"{title} {items.Count} 个 | 最近刷新 {DateTime.Now:HH:mm:ss}";
        itemsControl.ItemsSource = BuildHeatBoardDisplays(items);
    }

    private static HeatBoardDisplay[] BuildHeatBoardDisplays(IReadOnlyList<HeatBoardItemDto> items)
    {
        return items
            .Select(item => new HeatBoardDisplay(
                item.Name,
                $"热度 {item.HeatScore:F1}",
                $"均涨 {item.AverageChangePercent:F2}%   上涨 {item.RisingCount}/{item.StockCount}   成交额 {item.TotalAmount / 100_000_000m:F1} 亿",
                BuildHeatLeaderText(item.Leaders)))
            .ToArray();
    }

    private void MappingHeatSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyMappingHeatFilters();
    }

    private void ApplyMappingHeatFilters()
    {
        var sectorQuery = MappingSectorSearchBox?.Text?.Trim() ?? string.Empty;
        var conceptQuery = MappingConceptSearchBox?.Text?.Trim() ?? string.Empty;
        MappingSectorHeatItemsControl.ItemsSource = BuildHeatBoardDisplays(_mappingSectorHeatItems
            .Where(item => string.IsNullOrWhiteSpace(sectorQuery) || item.Name.Contains(sectorQuery, StringComparison.OrdinalIgnoreCase))
            .ToArray());
        MappingConceptHeatItemsControl.ItemsSource = BuildHeatBoardDisplays(_mappingConceptHeatItems
            .Where(item => string.IsNullOrWhiteSpace(conceptQuery) || item.Name.Contains(conceptQuery, StringComparison.OrdinalIgnoreCase))
            .ToArray());
    }

    private void ApplyHistoryStats(
        IReadOnlyList<AShareRadar.Contracts.History.HistoricalSignalDto> signals,
        IReadOnlyList<AShareRadar.Contracts.History.StrategyPerformanceDto> strategies)
    {
        HistorySummaryText.Text = $"历史信号 {signals.Count} 条 | 策略 {strategies.Count} 个 | {BuildHistoryFilterText()} | 最近刷新 {DateTime.Now:HH:mm:ss}";
        HistoryStrategyListBox.ItemsSource = strategies
            .Select(item => new StrategyPerformanceDisplay(
                item.StrategyName,
                item.HitCount,
                item.AverageScore,
                item.MaxScore,
                item.LastHitTime.HasValue ? item.LastHitTime.Value.ToLocalTime().ToString("MM-dd HH:mm") : "--",
                $"命中 {item.HitCount} 次   均分 {item.AverageScore:F2}   最高 {item.MaxScore:F2}"))
            .ToArray();
        HistorySignalListBox.ItemsSource = signals
            .Select(item => new HistoricalSignalDisplay(
                item.EventTime.ToLocalTime().ToString("MM-dd HH:mm"),
                TranslateEventType(item.EventType),
                item.Symbol,
                item.Name,
                item.StrategyName,
                $"分数 {item.Score:F2}",
                $"命中策略 {item.StrategyHitCount} 个",
                item.Reason))
            .ToArray();
    }

    private void ApplyStrategyCenter(IReadOnlyList<StrategyDefinitionDto> strategies)
    {
        StrategyCenterSummaryText.Text = $"已启用策略 {strategies.Count} 个 | 最近刷新 {DateTime.Now:HH:mm:ss}";
        StrategyDefinitionListBox.ItemsSource = strategies
            .Select(item => new StrategyDefinitionDisplay(
                item.Code,
                item.Name,
                TranslateStrategyStage(item.Stage),
                TranslateStrategyAction(item.DefaultAction),
                BuildDataRequirementText(item.DataRequirement),
                BuildStrategyParameterText(item.Parameters),
                item.Description))
            .ToArray();
    }

    private void ApplyStrategySentimentRules(MarketSentimentStrategyRulesDto? rules)
    {
        if (rules is null)
        {
            StrategySentimentRulesText.Text = "情绪规则：未加载";
            return;
        }

        StrategySentimentRulesText.Text =
            $"情绪规则：{(rules.Enabled ? "启用" : "关闭")} | 快照有效 {rules.MaxSnapshotAgeMinutes} 分钟 | " +
            $"低于 {rules.DemoteAggressiveBelowTemperature:F0} 分进攻信号降级：{(rules.EnableActionDemotion ? "启用" : "关闭")} | " +
            $"过热风险阈值 {rules.OverheatedRiskTemperature:F0} | " +
            $"冰点进攻 {rules.Frozen.Aggressive:+0;-0;0}，偏冷进攻 {rules.Cold.Aggressive:+0;-0;0}，偏热主线 {rules.Hot.MainlineOrTrend:+0;-0;0}，过热进攻 {rules.Overheated.Aggressive:+0;-0;0}";
    }

    private async Task RunBacktestAsync()
    {
        var startDate = BacktestStartDatePicker.SelectedDate.HasValue
            ? DateOnly.FromDateTime(BacktestStartDatePicker.SelectedDate.Value)
            : DateOnly.FromDateTime(DateTime.Today.AddDays(-60));
        var endDate = BacktestEndDatePicker.SelectedDate.HasValue
            ? DateOnly.FromDateTime(BacktestEndDatePicker.SelectedDate.Value)
            : DateOnly.FromDateTime(DateTime.Today);
        var symbols = ParseCsv(BacktestSymbolsTextBox.Text)
            .Select(NormalizeHistorySymbol)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var stockPool = GetSelectedBacktestStockPool();

        if (stockPool == "Manual" && symbols.Length == 0)
        {
            FooterText.Text = "请至少输入一个股票代码，例如 300059,600000。";
            return;
        }

        var strategyCodes = ParseCsv(BacktestStrategiesTextBox.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var maxSymbols = int.TryParse(BacktestMaxSymbolsTextBox.Text, out var parsedMaxSymbols)
            ? Math.Clamp(parsedMaxSymbols, 1, 100)
            : 30;

        await RunUiActionAsync(async token =>
        {
            BacktestSummaryText.Text = "正在回放策略...";
            _lastBacktestResult = null;
            BacktestStrategySummaryListBox.ItemsSource = null;
            BacktestResultListBox.ItemsSource = null;
            BacktestUsedSymbolsText.Text = "实际股票：正在计算...";
            var result = await _apiClient.ReplayBacktestAsync(
                new BacktestReplayRequest(
                    startDate,
                    endDate,
                    symbols,
                    strategyCodes.Length == 0 ? null : strategyCodes,
                    LookbackDays: 80,
                    StockPool: stockPool,
                    MaxSymbols: maxSymbols),
                token);
            _lastBacktestResult = result;
            ApplyBacktestResult(result);
        });
    }

    private string GetSelectedBacktestStockPool()
    {
        return BacktestStockPoolComboBox.SelectedIndex switch
        {
            1 => "Historical",
            2 => "RecentActive",
            _ => "Manual"
        };
    }

    private void ApplyBacktestResult(BacktestReplayResultDto? result)
    {
        if (result is null)
        {
            BacktestSummaryText.Text = "暂无回放结果";
            BacktestStrategySummaryListBox.ItemsSource = null;
            BacktestSentimentSummaryListBox.ItemsSource = null;
            BacktestResultListBox.ItemsSource = null;
            BacktestUsedSymbolsText.Text = "实际股票：--";
            return;
        }

        BacktestSummaryText.Text = $"区间 {result.StartDate:yyyy-MM-dd} 至 {result.EndDate:yyyy-MM-dd} | 股票 {result.Symbols.Count} 只 | 策略 {result.StrategyCodes.Count} 个 | 命中 {result.SignalCount} 条 | 耗时 {result.ElapsedMilliseconds} ms | {result.DataSourceStatus} | {result.Message}";
        BacktestUsedSymbolsText.Text = $"实际股票：{BuildUsedSymbolsText(result.Symbols)}";

        var summaries = result.StrategySummaries
            .OrderByDescending(item => item.AverageReturn5Day ?? decimal.MinValue)
            .ThenByDescending(item => item.WinRate5Day ?? decimal.MinValue)
            .ThenByDescending(item => item.SignalCount)
            .ToArray();
        if (BacktestPositive5DayCheckBox.IsChecked == true)
        {
            summaries = summaries
                .Where(item => item.AverageReturn5Day > 0)
                .ToArray();
        }
        var visibleStrategyCodes = summaries
            .Select(item => item.StrategyCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        BacktestStrategySummaryListBox.ItemsSource = summaries
            .Select(item => new BacktestStrategySummaryDisplay(
                item.StrategyName,
                $"命中 {item.SignalCount} 次",
                $"胜率 1日{FormatPercent(item.WinRate1Day)}   3日{FormatPercent(item.WinRate3Day)}   5日{FormatPercent(item.WinRate5Day)}",
                $"平均收益 1日{FormatPercent(item.AverageReturn1Day)}   3日{FormatPercent(item.AverageReturn3Day)}   5日{FormatPercent(item.AverageReturn5Day)}   5日最好{FormatPercent(item.BestReturn5Day)} / 最差{FormatPercent(item.WorstReturn5Day)}",
                GetWinRateBrush(item.WinRate5Day),
                GetReturnBrush(item.AverageReturn5Day)))
            .ToArray();
        BacktestSentimentSummaryListBox.ItemsSource = result.SentimentSummaries
            .Select(item => new BacktestSentimentSummaryDisplay(
                item.SentimentLevel,
                $"命中 {item.SignalCount} 条",
                $"胜率 1日{FormatPercent(item.WinRate1Day)}   3日{FormatPercent(item.WinRate3Day)}   5日{FormatPercent(item.WinRate5Day)}",
                $"平均收益 1日{FormatPercent(item.AverageReturn1Day)}   3日{FormatPercent(item.AverageReturn3Day)}   5日{FormatPercent(item.AverageReturn5Day)}",
                GetWinRateBrush(item.WinRate5Day),
                GetReturnBrush(item.AverageReturn5Day)))
            .ToArray();
        BacktestResultListBox.ItemsSource = result.Signals
            .Where(item => BacktestPositive5DayCheckBox.IsChecked != true || visibleStrategyCodes.Contains(item.StrategyCode))
            .Select(item => new BacktestSignalDisplay(
                item.TradingDate.ToString("MM-dd"),
                item.Symbol,
                item.Name,
                item.StrategyCode,
                item.StrategyName,
                item.TradingDate,
                item.Action,
                TranslateStrategyAction(item.Action),
                item.Confidence,
                TranslateSignalConfidence(item.Confidence),
                $"分数 {item.Score:F2}",
                FormatBacktestReturns(item),
                item.Reason,
                item.Risk,
                item.Score,
                item.Price,
                item.Return1Day,
                item.Return3Day,
                item.Return5Day))
            .ToArray();
    }

    private async Task RefreshPredictionReviewAsync()
    {
        await RunUiActionAsync(async token =>
        {
            UpdatePredictionDateFromPicker();
            PredictionSummaryText.Text = "正在读取次日预测数据...";
            PredictionWaitText.Text = "正在调用后端接口，请稍候...";
            var review = await _apiClient.GetPredictionReviewAsync(_predictionDate, token);
            ApplyPredictionReview(review);
        });
    }

    private async Task RefreshLongTermTrackingAsync(CancellationToken cancellationToken = default)
    {
        await RunUiActionAsync(async token =>
        {
            var activeToken = cancellationToken == default ? token : cancellationToken;
            LongTermTrackingSummaryText.Text = "正在读取长期跟踪数据...";
            var result = await _apiClient.GetLongTermTrackingAsync(
                LongTermTrackingFromDatePicker.SelectedDate.HasValue
                    ? DateOnly.FromDateTime(LongTermTrackingFromDatePicker.SelectedDate.Value)
                    : null,
                LongTermTrackingToDatePicker.SelectedDate.HasValue
                    ? DateOnly.FromDateTime(LongTermTrackingToDatePicker.SelectedDate.Value)
                    : null,
                string.IsNullOrWhiteSpace(LongTermTrackingSymbolTextBox.Text)
                    ? null
                    : NormalizeHistorySymbol(LongTermTrackingSymbolTextBox.Text),
                string.IsNullOrWhiteSpace(LongTermTrackingStrategyCodeTextBox.Text)
                    ? null
                    : LongTermTrackingStrategyCodeTextBox.Text.Trim(),
                GetComboBoxTag(LongTermTrackingStatusComboBox),
                GetComboBoxTag(LongTermTrackingSortComboBox) ?? "LastHitAt",
                descending: true,
                count: 1000,
                activeToken);
            ApplyLongTermTracking(result);
        });
    }

    private void ApplyLongTermTracking(LongTermTrackingQueryResultDto? result)
    {
        if (result is null)
        {
            LongTermTrackingSummaryText.Text = "暂无长期跟踪数据。";
            LongTermTrackingDataGrid.ItemsSource = null;
            return;
        }

        LongTermTrackingSummaryText.Text = result.TotalCount == 0
            ? "暂无标的 / 策略组合"
            : $"{result.TotalCount} 个标的 / 策略组合";
        LongTermTrackingDataGrid.ItemsSource = result.Items
            .Select(item => new LongTermTrackingDisplay(
                item.Id,
                item.Symbol,
                item.Name,
                item.StrategyCode,
                item.StrategyName,
                FormatNullableDecimal(item.CurrentPrice),
                FormatPercent(item.ReturnFromHit),
                GetAshareReturnBrush(item.ReturnFromHit),
                $"最新分 {item.LatestScore:F1} / 最高分 {item.BestScore:F1}",
                item.HitCount.ToString(),
                BuildLongTermHitRangeText(item.FirstHitAt, item.LastHitAt),
                BuildLongTermTrackingTags(item),
                item.LatestRisk ?? "--"))
            .ToArray();
    }

    private static string BuildLongTermHitRangeText(DateTimeOffset firstHitAt, DateTimeOffset lastHitAt)
    {
        return $"{firstHitAt.ToLocalTime():MM-dd HH:mm} ~ {lastHitAt.ToLocalTime():MM-dd HH:mm}";
    }

    private static IReadOnlyList<LongTermTrackingTagDisplay> BuildLongTermTrackingTags(LongTermTrackingItemDto item)
    {
        var tags = new List<LongTermTrackingTagDisplay>();
        var risk = item.LatestRisk ?? string.Empty;
        if (!risk.Contains("仍明显低于MA20", StringComparison.OrdinalIgnoreCase)
            && !risk.Contains("低于MA20", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add(LongTermTrackingTagDisplay.Positive("修复中"));
        }

        if (risk.Contains("低于MA20", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add(LongTermTrackingTagDisplay.Warning("低于MA20"));
        }

        if (risk.Contains("承接", StringComparison.OrdinalIgnoreCase)
            || risk.Contains("下影", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add(LongTermTrackingTagDisplay.Danger("承接弱"));
        }

        if (risk.Contains("情绪偏热", StringComparison.OrdinalIgnoreCase)
            || risk.Contains("拥挤", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add(LongTermTrackingTagDisplay.Warning("情绪偏热"));
        }

        if (item.StrategyName.Contains("主线", StringComparison.OrdinalIgnoreCase)
            || item.LatestReason.Contains("主线", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add(LongTermTrackingTagDisplay.Info("主线信号"));
        }

        if (tags.Count == 0)
        {
            tags.Add(LongTermTrackingTagDisplay.Info(TranslateLongTermStatus(item.Status)));
        }

        return tags.Take(5).ToArray();
    }

    private static string? GetComboBoxTag(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem { Tag: string tag } && !string.IsNullOrWhiteSpace(tag)
            ? tag
            : null;
    }

    private static string TranslateLongTermStatus(string status)
    {
        return status switch
        {
            "Focus" => "重点",
            "GiveUp" => "放弃",
            "Archived" => "归档",
            _ => "观察"
        };
    }

    private static string FormatNullableDecimal(decimal? value)
    {
        return value.HasValue ? value.Value.ToString("F2") : "--";
    }

    private void UpdatePredictionDateFromPicker()
    {
        _predictionDate = PredictionDatePicker.SelectedDate.HasValue
            ? DateOnly.FromDateTime(PredictionDatePicker.SelectedDate.Value)
            : DateOnly.FromDateTime(DateTime.Today);
    }

    private void ApplyPredictionReview(PredictionReviewDto? review)
    {
        if (review is null)
        {
            PredictionSummaryText.Text = "暂无预测数据";
            PredictionWaitText.Text = "后端没有返回预测数据。可以点击“生成预测”，或等待历史策略扫描完成后再刷新。";
            PredictionRecordListBox.ItemsSource = Array.Empty<PredictionRecordDisplay>();
            return;
        }

        PredictionSummaryText.Text =
            $"信号日 {review.SignalDate:yyyy-MM-dd} | 预测 {review.PredictionCount} 只 | 看涨 {review.UpPredictionCount} 只 | 已验证 {review.VerifiedCount} 只 | 收盘成功率 {FormatPercent(review.CloseSuccessRate)} | 盘中成功率 {FormatPercent(review.IntradaySuccessRate)} | 次日平均收盘 {FormatPercent(review.AverageNextCloseReturn)} | {review.Message}";
        var qlibPredictionBrush = (Brush)FindResource("DangerBrush");
        var defaultPredictionBrush = (Brush)FindResource("PrimaryBrush");
        var records = review.Records
            .Select(item => new PredictionRecordDisplay(
                item.Symbol,
                item.Name,
                $"{item.PredictionDirection} {item.PredictionScore:F1}",
                IsQlibNextDayPrediction(item) ? qlibPredictionBrush : defaultPredictionBrush,
                $"策略：{item.StrategyNames} | 信号 {item.SignalCount} 次 | 策略命中 {item.StrategyHitCount} 次 | 最高分 {item.BestScore:F2}",
                BuildPredictionVerifyText(item),
                item.PredictionReason,
                IsQlibNextDayPrediction(item) ? qlibPredictionBrush : (Brush)FindResource("SubtleTextBrush"),
                item.RiskNote,
                $"{item.PredictionScore:F2}",
                BuildPrimaryStrategyText(item.StrategyNames),
                $"{item.SignalCount}次",
                $"{item.StrategyHitCount}次",
                BuildPredictionUpProbabilityText(item),
                BuildPredictionDownProbabilityText(item),
                CalculatePredictionUpProbabilityWidth(item),
                BuildPredictionConfidenceText(item),
                BuildPredictionExtraTagText(item),
                string.IsNullOrWhiteSpace(BuildPredictionExtraTagText(item)) ? Visibility.Collapsed : Visibility.Visible,
                BuildPredictionVerifyBadgeText(item)))
            .ToArray();
        PredictionRecordListBox.ItemsSource = records;
        PredictionRecordListBox.SelectedIndex = records.Length > 0 ? 0 : -1;
        PredictionWaitText.Text = records.Length > 0
            ? "左侧已选中第一条预测记录，可查看验证细节。"
            : "当前日期没有预测明细。可以换一个信号日期，或等待历史策略扫描产生结果后再生成。";
    }

    private static string BuildPredictionVerifyText(PredictionRecordDto item)
    {
        if (!item.VerifyDate.HasValue)
        {
            return $"验证：{item.VerifyStatus}";
        }

        return $"验证：{item.VerifyStatus} | 验证日 {item.VerifyDate:yyyy-MM-dd} | 开盘 {FormatPercent(item.NextOpenReturn)} | 收盘 {FormatPercent(item.NextCloseReturn)} | 最高 {FormatPercent(item.NextHighReturn)} | 最低 {FormatPercent(item.NextLowReturn)}";
    }

    private static string BuildPrimaryStrategyText(string strategyNames)
    {
        var first = strategyNames
            .Split(['|', ',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? "策略命中" : first;
    }

    private static string BuildPredictionUpProbabilityText(PredictionRecordDto item)
    {
        return $"上涨 {ExtractPredictionProbability(item.PredictionReason, "上涨概率", item.PredictionScore):F2}%";
    }

    private static string BuildPredictionDownProbabilityText(PredictionRecordDto item)
    {
        var fallback = Math.Clamp(100m - item.PredictionScore, 0m, 100m);
        return $"下跌 {ExtractPredictionProbability(item.PredictionReason, "下跌概率", fallback):F2}%";
    }

    private static double CalculatePredictionUpProbabilityWidth(PredictionRecordDto item)
    {
        var up = ExtractPredictionProbability(item.PredictionReason, "上涨概率", item.PredictionScore);
        return (double)Math.Clamp(up, 0m, 100m) / 100d * 274d;
    }

    private static decimal ExtractPredictionProbability(string text, string label, decimal fallback)
    {
        var match = Regex.Match(text, label + @"\s*([0-9]+(?:\.[0-9]+)?)%");
        return match.Success && decimal.TryParse(match.Groups[1].Value, out var value)
            ? value
            : fallback;
    }

    private static string BuildPredictionConfidenceText(PredictionRecordDto item)
    {
        if (item.RiskNote.Contains("置信度低", StringComparison.OrdinalIgnoreCase)
            || item.PredictionReason.Contains("置信度 低", StringComparison.OrdinalIgnoreCase))
        {
            return "置信度低 · 单一策略命中";
        }

        if (item.PredictionReason.Contains("置信度 高", StringComparison.OrdinalIgnoreCase))
        {
            return "置信度高";
        }

        return item.StrategyHitCount > 1 ? "多策略命中" : "单一策略命中";
    }

    private static string BuildPredictionExtraTagText(PredictionRecordDto item)
    {
        if (item.RiskNote.Contains("盘中出现加强", StringComparison.OrdinalIgnoreCase)
            || item.PredictionReason.Contains("盘中出现加强", StringComparison.OrdinalIgnoreCase))
        {
            return "盘中加强";
        }

        if (item.PredictionReason.Contains("重新命中", StringComparison.OrdinalIgnoreCase))
        {
            return "重新命中";
        }

        return string.Empty;
    }

    private static string BuildPredictionVerifyBadgeText(PredictionRecordDto item)
    {
        return item.VerifyStatus switch
        {
            "成功" => "已成功",
            "失败" => "失败",
            "待验证" => "待验证",
            _ => item.VerifyStatus
        };
    }

    private static bool IsQlibNextDayPrediction(PredictionRecordDto item)
    {
        return item.PredictionReason.Contains("Qlib 明日预测", StringComparison.OrdinalIgnoreCase)
            || item.StrategyCodes.Contains("qlib-next-day-direction", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyWorkspaceVisibility()
    {
        _showMarketSentiment = false;
        var isDailyHitResearchView = _showResearchPage && _showStockPools;
        var isRealtimeObservationView = !_showResearchPage && !_showMarketSentiment;
        var showDailyHitKLine = false;
        var isFullWorkspaceView = _showResearchPage && !isDailyHitResearchView;
        WorkspaceContentGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        WorkspaceContentGrid.RowDefinitions[1].Height = new GridLength(0);
        Grid.SetRow(WorkspaceListHost, 0);
        Grid.SetColumn(WorkspaceListHost, 0);
        Grid.SetColumnSpan(WorkspacePanel, isRealtimeObservationView ? 2 : 1);
        Grid.SetRow(KLinePanel, 0);
        Grid.SetColumn(KLinePanel, 2);
        SignalEventListBox.Visibility = Visibility.Collapsed;
        SectorHeatPanel.Visibility = Visibility.Collapsed;
        ConceptHeatPanel.Visibility = Visibility.Collapsed;
        MarketSentimentPanel.Visibility = Visibility.Collapsed;
        HistoryStatsPanel.Visibility = _showHistory ? Visibility.Visible : Visibility.Collapsed;
        PredictionReviewPanel.Visibility = _showPredictionReview ? Visibility.Visible : Visibility.Collapsed;
        StrategyCenterPanel.Visibility = _showStrategyCenter ? Visibility.Visible : Visibility.Collapsed;
        LongTermTrackingPanel.Visibility = _showLongTermTracking ? Visibility.Visible : Visibility.Collapsed;
        BacktestPanel.Visibility = _showBacktest ? Visibility.Visible : Visibility.Collapsed;
        StockPoolsPanel.Visibility = _showStockPools ? Visibility.Visible : Visibility.Collapsed;

        OpportunityPanel.Visibility = _showResearchPage ? Visibility.Collapsed : Visibility.Visible;
        SnapshotPanel.Visibility = _showResearchPage || isRealtimeObservationView ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceListHost.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        ObservationPoolPanel.Visibility = isRealtimeObservationView ? Visibility.Visible : Visibility.Collapsed;
        KLinePanel.Visibility = isRealtimeObservationView || isFullWorkspaceView || (isDailyHitResearchView && !showDailyHitKLine)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OpportunityColumn.Width = _showResearchPage ? new GridLength(0) : new GridLength(430);
        SnapshotColumn.Width = _showResearchPage || isRealtimeObservationView ? new GridLength(0) : new GridLength(320);
        WorkspaceListColumn.Width = _showResearchPage
            ? showDailyHitKLine ? new GridLength(300) : new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        WorkspaceKLineGapColumn.Width = _showResearchPage
            ? showDailyHitKLine ? new GridLength(12) : new GridLength(0)
            : new GridLength(0);
        WorkspaceKLineColumn.Width = _showResearchPage
            ? showDailyHitKLine ? new GridLength(1, GridUnitType.Star) : new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        WorkspacePanel.Margin = _showResearchPage ? new Thickness(0) : new Thickness(14, 0, 0, 0);
        WorkspaceTitleText.Text = _showResearchPage ? "研究复盘" : "实时工作台";
        WorkspaceCaptionText.Text = _showResearchPage
            ? "历史统计、次日预测、长期跟踪和策略中心已集中到研究页。"
            : "左侧显示实时策略机会，右侧显示概念与行业映射更新结果。";
        SignalStreamViewButton.Visibility = Visibility.Collapsed;
        SectorHeatViewButton.Visibility = Visibility.Collapsed;
        ConceptHeatViewButton.Visibility = Visibility.Collapsed;
        MarketSentimentViewButton.Visibility = Visibility.Collapsed;
        HistoryStatsViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        PredictionReviewViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        LongTermTrackingViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        StrategyCenterViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        BacktestViewButton.Visibility = Visibility.Collapsed;
        StockPoolsViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;

        RealtimePageButton.Style = _showResearchPage
            ? (Style)FindResource("PageTabButtonStyle")
            : (Style)FindResource("PrimaryButtonStyle");
        ResearchPageButton.Style = _showResearchPage
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");

        SignalStreamViewButton.Style = !_showResearchPage && !_showSectorHeat && !_showConceptHeat
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        SectorHeatViewButton.Style = _showSectorHeat
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        ConceptHeatViewButton.Style = _showConceptHeat
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        MarketSentimentViewButton.Style = _showMarketSentiment
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        HistoryStatsViewButton.Style = _showHistory
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        PredictionReviewViewButton.Style = _showPredictionReview
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        LongTermTrackingViewButton.Style = _showLongTermTracking
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        StrategyCenterViewButton.Style = _showStrategyCenter
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        BacktestViewButton.Style = _showBacktest
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        StockPoolsViewButton.Style = _showStockPools
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
    }

    private void ApplyKLineButtonStyles()
    {
        var selectedStyle = (Style)FindResource("KLineToolbarSelectedButtonStyle");
        var normalStyle = (Style)FindResource("KLineToolbarButtonStyle");

        SetSelectionStyle(MinutePeriodButton, _kLinePeriod == "minute", selectedStyle, normalStyle);
        SetSelectionStyle(FiveDayPeriodButton, _kLinePeriod == "five-day", selectedStyle, normalStyle);
        SetSelectionStyle(M1PeriodButton, _kLinePeriod == "m1", selectedStyle, normalStyle);
        SetSelectionStyle(M5PeriodButton, _kLinePeriod == "m5", selectedStyle, normalStyle);
        SetSelectionStyle(M15PeriodButton, _kLinePeriod == "m15", selectedStyle, normalStyle);
        SetSelectionStyle(M30PeriodButton, _kLinePeriod == "m30", selectedStyle, normalStyle);
        SetSelectionStyle(M60PeriodButton, _kLinePeriod == "m60", selectedStyle, normalStyle);
        SetSelectionStyle(DayPeriodButton, _kLinePeriod == "day", selectedStyle, normalStyle);
        SetSelectionStyle(WeekPeriodButton, _kLinePeriod == "week", selectedStyle, normalStyle);
        SetSelectionStyle(MonthPeriodButton, _kLinePeriod == "month", selectedStyle, normalStyle);

        SetSelectionStyle(MacdIndicatorButton, _indicatorMode == "MACD", selectedStyle, normalStyle);
        SetSelectionStyle(KdjIndicatorButton, _indicatorMode == "KDJ", selectedStyle, normalStyle);
        SetSelectionStyle(RsiIndicatorButton, _indicatorMode == "RSI", selectedStyle, normalStyle);
    }

    private static void SetSelectionStyle(Button button, bool selected, Style selectedStyle, Style normalStyle)
    {
        button.Style = selected ? selectedStyle : normalStyle;
    }

    private async Task ApplySnapshotAsync(OpportunityDetailDto detail, CancellationToken cancellationToken, bool refreshKLine = true)
    {
        var opportunity = detail.Opportunity;
        _selectedOpportunityId = opportunity.Id;
        _selectedSymbol = opportunity.Symbol;
        _selectedName = opportunity.Name;
        SnapshotTitleText.Text = $"{opportunity.Symbol} {opportunity.Name}";
        ChartTitleText.Text = $"{opportunity.Symbol} {opportunity.Name} K线分析";
        ChartCaptionText.Text = $"状态：{TranslateOpportunityStatus(opportunity.Status)}   当前分：{opportunity.CurrentScore:F2}   命中：{opportunity.HitCount} 次   周期：{TranslateKLinePeriod(_kLinePeriod)}";
        SnapshotStatusText.Text = $"状态：{TranslateOpportunityStatus(opportunity.Status)} | 当前分：{opportunity.CurrentScore:F2} | 最高分：{opportunity.BestScore:F2} | 命中：{opportunity.HitCount} 次";
        SnapshotTimeText.Text = $"首次出现：{FormatTime(opportunity.FirstSeenTime)}\n最近出现：{FormatTime(opportunity.LastSeenTime)}";
        KLineChart.SymbolName = $"{opportunity.Symbol} {opportunity.Name}";
        KLineChart.TradeMarkers = BuildTradeMarkers(detail.LatestEvent);
        if (refreshKLine)
        {
            await RefreshKLineAsync(cancellationToken);
        }

        ManualTagText.Text = $"人工标记：{TranslateManualTag(opportunity.ManualTag)}";
        DecisionNoteTextBox.Text = opportunity.Note ?? string.Empty;
        SnapshotReasonText.Text = detail.LatestEvent?.Reason ?? "--";
        SnapshotSentimentText.Text = BuildSignalEventSentimentText(detail.LatestEvent);
        SnapshotSentimentPanel.Visibility = SnapshotSentimentText.Text == "--" ? Visibility.Collapsed : Visibility.Visible;
        SnapshotRiskText.Text = string.IsNullOrWhiteSpace(detail.LatestEvent?.Risk) ? "--" : detail.LatestEvent.Risk;
        StrategyHitItemsControl.ItemsSource = detail.LatestEvent?.StrategyHits
            .Select(item => new StrategyHitDisplay(
                item.StrategyName,
                item.Score,
                BuildStrategyHitSentimentText(item.Metrics, item.Tags),
                item.Reason,
                string.IsNullOrWhiteSpace(item.Risk) ? "--" : item.Risk,
                BuildConditionText("满足", item.PassedConditions),
                BuildConditionText("待确认", item.FailedConditions),
                BuildTradePlanText(item.StopLossPrice, item.TakeProfitPrice),
                BuildTagsText(item.Tags),
                BuildMetricsText(item.Metrics)))
            .ToArray() ?? [];
    }

    private void ClearSnapshot()
    {
        _selectedOpportunityId = null;
        _selectedSymbol = null;
        _selectedName = null;
        SnapshotTitleText.Text = "未选择机会";
        ChartTitleText.Text = "K线分析";
        ChartCaptionText.Text = "选择左侧机会后显示对应个股走势、均线、成交量、MACD 与筹码分布。";
        SnapshotStatusText.Text = "请先在左侧选择一个机会。";
        SnapshotTimeText.Text = string.Empty;
        KLineChart.SymbolName = "未选择";
        KLineChart.Candles = [];
        KLineChart.IndicatorSeries = null;
        KLineChart.TradeMarkers = [];
        ManualTagText.Text = "人工标记：--";
        DecisionNoteTextBox.Text = string.Empty;
        SnapshotReasonText.Text = "--";
        SnapshotSentimentText.Text = "--";
        SnapshotSentimentPanel.Visibility = Visibility.Collapsed;
        SnapshotRiskText.Text = "--";
        StrategyHitItemsControl.ItemsSource = null;
    }

    private async Task SaveDecisionAsync(string decisionType)
    {
        if (_selectedOpportunityId is null)
        {
            FooterText.Text = "请先选择一个机会，再保存人工判断。";
            return;
        }

        await RunUiActionAsync(async token =>
        {
            await _apiClient.SaveDecisionAsync(
                _selectedOpportunityId.Value,
                decisionType,
                DecisionNoteTextBox.Text,
                token);
            _opportunityView = "Current";
            OpportunityFilterText.Text = "主线板块共振 / 主线低开高走 · 按综合分排序";
            await RefreshAsync(token);
            FooterText.Text = $"已保存人工判断：{TranslateManualTag(decisionType)}";
        });
    }

    private async Task SetKLinePeriodAsync(string period)
    {
        _kLinePeriod = period;
        KLineChart.PeriodName = TranslateKLinePeriod(period);
        ApplyKLineButtonStyles();
        await RunUiActionAsync(async token => await RefreshKLineAsync(token));
    }

    private async Task SetIndicatorModeAsync(string mode)
    {
        _indicatorMode = mode;
        KLineChart.IndicatorMode = mode;
        ApplyKLineButtonStyles();
        ChartCaptionText.Text = string.IsNullOrWhiteSpace(_selectedSymbol)
            ? $"选择左侧机会后显示对应个股走势、均线、成交量、{mode} 与筹码分布。"
            : $"状态：已选择   周期：{TranslateKLinePeriod(_kLinePeriod)}   副图：{mode}";
        await RunUiActionAsync(async token => await RefreshIndicatorAsync(token));
    }

    private async Task RefreshKLineAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_selectedSymbol))
        {
            KLineChart.SymbolName = "未选择";
            KLineChart.Candles = [];
            KLineChart.IndicatorSeries = null;
            KLineChart.TradeMarkers = [];
            return;
        }

        var count = GetKLineCount(_kLinePeriod);
        var bars = await _apiClient.GetKLineAsync(_selectedSymbol, _kLinePeriod, count, cancellationToken);
        KLineChart.SymbolName = $"{_selectedSymbol} {_selectedName}";
        KLineChart.PeriodName = TranslateKLinePeriod(_kLinePeriod);
        KLineChart.Candles = bars
            .Select(item => new KLineCandle(
                item.TradingTime,
                item.Open,
                item.High,
                item.Low,
                item.Close,
                item.Volume,
                item.Amount,
                item.TurnoverRate))
            .ToArray();
        await RefreshIndicatorAsync(cancellationToken);
    }

    private async Task RefreshIndicatorAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_selectedSymbol))
        {
            KLineChart.IndicatorSeries = null;
            return;
        }

        try
        {
            var series = await _apiClient.GetIndicatorsAsync(
                _selectedSymbol,
                _kLinePeriod,
                _indicatorMode,
                GetKLineCount(_kLinePeriod),
                cancellationToken);

            KLineChart.IndicatorSeries = series is null
                ? null
                : new KLineIndicatorSeries(
                    series.Type,
                    series.Points
                        .Select(item => new KLineIndicatorPoint(
                            item.TradingTime,
                            item.Value1,
                            item.Value2,
                            item.Value3,
                            item.BarValue))
                        .ToArray());
        }
        catch
        {
            KLineChart.IndicatorSeries = null;
        }
    }

    private int GetSelectedIntervalSeconds()
    {
        if (IntervalComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out var seconds))
        {
            return seconds;
        }

        return 30;
    }

    private static IReadOnlyList<KLineCandle> CreatePreviewCandles(string symbol, decimal score)
    {
        var seed = symbol.Aggregate(0, (current, ch) => current + ch);
        var random = new Random(seed);
        var candles = new List<KLineCandle>();
        var close = 32m + seed % 9 + score / 35m;

        for (var i = 0; i < 78; i++)
        {
            var pressure = i > 45 ? -0.06m : 0.02m;
            var volatility = 0.7m + score / 170m;
            var open = close + (decimal)(random.NextDouble() - 0.5) * volatility;
            close = open + (decimal)(random.NextDouble() - 0.48) * volatility * 1.8m + pressure;
            var high = Math.Max(open, close) + (decimal)random.NextDouble() * volatility;
            var low = Math.Min(open, close) - (decimal)random.NextDouble() * volatility;
            var volume = random.Next(90000, 850000) * (1m + score / 160m);
            candles.Add(new KLineCandle(
                DateTime.Today.AddDays(i - 78),
                Math.Round(open, 2),
                Math.Round(high, 2),
                Math.Round(low, 2),
                Math.Round(close, 2),
                Math.Round(volume, 0)));
        }

        return candles;
    }

    private static int GetKLineCount(string period)
    {
        return period switch
        {
            "minute" => 96,
            "five-day" => 240,
            "m1" => 240,
            "m5" => 240,
            "m15" => 192,
            "m30" => 160,
            "m60" => 120,
            "week" => 120,
            "month" => 96,
            _ => 140
        };
    }

    private static string TranslateKLinePeriod(string period)
    {
        return period switch
        {
            "minute" => "分时",
            "five-day" => "5日",
            "m1" => "1分钟",
            "m5" => "5分钟",
            "m15" => "15分钟",
            "m30" => "30分钟",
            "m60" => "60分钟",
            "week" => "周线",
            "month" => "月线",
            _ => "日线"
        };
    }

    private static string BuildStrategySummary(SignalEventDto signalEvent)
    {
        if (signalEvent.StrategyHits.Count <= 1)
        {
            return signalEvent.StrategyName;
        }

        var names = string.Join("、", signalEvent.StrategyHits.Select(item => item.StrategyName));
        return $"{signalEvent.StrategyHits.Count} 个策略共振：{names}";
    }

    private static OpportunityDisplay MapOpportunityDisplay(OpportunityDto item)
    {
        return new OpportunityDisplay(
            item.Id,
            item.Symbol,
            item.Name,
            TranslateOpportunityStatus(item.Status),
            item.CurrentScore,
            item.CurrentScore.ToString("F1"),
            item.HitCount,
            item.StrategySummary,
            $"首次命中 {FormatTime(item.FirstSeenTime)}   最近命中 {FormatTime(item.LastSeenTime)}",
            item.StrategyExplanation);
    }

    private static bool IsRealtimePoolOpportunity(OpportunityDisplay item)
    {
        return ContainsRealtimePoolStrategy(item.StrategySummary)
            || ContainsRealtimePoolStrategy(item.StrategyExplanation);
    }

    private static bool ContainsRealtimePoolStrategy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("主线板块共振", StringComparison.OrdinalIgnoreCase)
            || value.Contains("主线低开高走", StringComparison.OrdinalIgnoreCase)
            || value.Contains("main-sector-resonance", StringComparison.OrdinalIgnoreCase)
            || value.Contains("main-sector-gap-recovery", StringComparison.OrdinalIgnoreCase);
    }

    private static string TranslateOpportunityStatus(string? value)
    {
        return value switch
        {
            "New" => "新机会",
            "Continued" => "持续",
            "ReHit" => "再次命中",
            "Strengthened" => "增强",
            "Weakened" => "减弱",
            "Disappeared" => "消失",
            "Focused" => "重点跟踪",
            "Candidate" => "候选",
            "Watch" => "观察",
            "GivenUp" => "已放弃",
            _ when string.IsNullOrWhiteSpace(value) => "--",
            _ => value
        };
    }

    private static string TranslateEventType(string? value)
    {
        return value switch
        {
            "New" => "新信号",
            "Continued" => "持续",
            "ReHit" => "再次命中",
            "Strengthened" => "增强",
            "Weakened" => "减弱",
            "Disappeared" => "消失",
            "ManualMarked" => "人工标记",
            _ when string.IsNullOrWhiteSpace(value) => "--",
            _ => value
        };
    }

    private static string TranslateMonitorStatus(string? value)
    {
        return value switch
        {
            "NotStarted" => "未开始",
            "Running" => "运行中",
            "Paused" => "已暂停",
            "Scanning" => "扫描中",
            "Failed" => "失败",
            _ when string.IsNullOrWhiteSpace(value) => "--",
            _ => value
        };
    }

    private static string TranslateMarketStatus(string? value)
    {
        return value switch
        {
            "Unknown" => "未知",
            "Simulation" => "模拟",
            "Open" => "交易中",
            "Closed" => "休市",
            "PreOpen" => "盘前",
            "LunchBreak" => "午间休市",
            _ when string.IsNullOrWhiteSpace(value) => "--",
            _ => value
        };
    }

    private static string TranslateManualTag(string? value)
    {
        return value switch
        {
            "Watch" => "观察",
            "Focus" => "重点",
            "WaitPullback" => "等回踩",
            "GiveUp" => "放弃",
            "PaperBuy" => "模拟买入",
            _ when string.IsNullOrWhiteSpace(value) => "--",
            _ => value
        };
    }

    private static string TranslateOpportunityView(string value)
    {
        return value switch
        {
            "Current" => "实时机会",
            "Focused" => "重点",
            "Candidate" => "候选",
            "Watch" => "观察",
            "WaitPullback" => "等待回踩",
            "GivenUp" => "已放弃",
            "All" => "全部归档",
            _ => value
        };
    }

    private async Task RunUiActionAsync(
        Func<CancellationToken, Task> action,
        TimeSpan? timeoutOverride = null,
        [CallerMemberName] string operation = "Unknown")
    {
        var timeoutValue = timeoutOverride ?? TimeSpan.FromSeconds(150);
        var stopwatch = Stopwatch.StartNew();
        Logger.Info("UI action started. Operation={Operation} TimeoutSeconds={TimeoutSeconds}", operation, timeoutValue.TotalSeconds);
        SetActionsEnabled(false);
        try
        {
            using var timeout = new CancellationTokenSource(timeoutValue);
            await action(timeout.Token);
            Logger.Info("UI action completed. Operation={Operation} ElapsedMs={ElapsedMs}", operation, stopwatch.ElapsedMilliseconds);
        }
        catch (TaskCanceledException ex)
        {
            Logger.Warn(ex, "UI action timed out or was canceled. Operation={Operation} ElapsedMs={ElapsedMs}", operation, stopwatch.ElapsedMilliseconds);
            FooterText.Text = "操作仍在等待后端响应，已超过前端等待时间。请稍后刷新查看结果。";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "UI action failed. Operation={Operation} ElapsedMs={ElapsedMs}", operation, stopwatch.ElapsedMilliseconds);
            FooterText.Text = $"操作失败：{ex.Message}";
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        Cursor = enabled ? Cursors.Arrow : Cursors.Wait;
        UpdateHistoryDataButton.IsEnabled = enabled;
        UpdateMarketMappingButton.IsEnabled = enabled;
        WatchButton.IsEnabled = enabled;
        FocusButton.IsEnabled = enabled;
        WaitPullbackButton.IsEnabled = enabled;
        GiveUpButton.IsEnabled = enabled;
        SignalStreamViewButton.IsEnabled = enabled;
        SectorHeatViewButton.IsEnabled = enabled;
        ConceptHeatViewButton.IsEnabled = enabled;
        MarketSentimentViewButton.IsEnabled = enabled;
        HistoryStatsViewButton.IsEnabled = enabled;
        PredictionReviewViewButton.IsEnabled = enabled;
        LongTermTrackingViewButton.IsEnabled = enabled;
        StrategyCenterViewButton.IsEnabled = enabled;
        BacktestViewButton.IsEnabled = enabled;
        StockPoolsViewButton.IsEnabled = enabled;
        RealtimePageButton.IsEnabled = enabled;
        ResearchPageButton.IsEnabled = enabled;
        ApplyHistoryFilterButton.IsEnabled = enabled;
        UseSelectedSymbolFilterButton.IsEnabled = enabled;
        ClearHistoryFilterButton.IsEnabled = enabled;
        PredictionDatePicker.IsEnabled = enabled;
        GeneratePredictionButton.IsEnabled = enabled;
        VerifyPredictionButton.IsEnabled = enabled;
        RefreshPredictionButton.IsEnabled = enabled;
        RunBacktestButton.IsEnabled = enabled;
        UseSelectedSymbolBacktestButton.IsEnabled = enabled;
        ExportBacktestButton.IsEnabled = enabled;
        BacktestScale20Button.IsEnabled = enabled;
        BacktestScale50Button.IsEnabled = enabled;
        BacktestScale100Button.IsEnabled = enabled;
        BacktestPositive5DayCheckBox.IsEnabled = enabled;
        BacktestStockPoolComboBox.IsEnabled = enabled;
        BacktestMaxSymbolsTextBox.IsEnabled = enabled;
        RefreshLongTermTrackingButton.IsEnabled = enabled;
        BackfillLongTermTrackingButton.IsEnabled = enabled;
        LongTermTrackingFromDatePicker.IsEnabled = enabled;
        LongTermTrackingToDatePicker.IsEnabled = enabled;
        LongTermTrackingSymbolTextBox.IsEnabled = enabled;
        LongTermTrackingStrategyCodeTextBox.IsEnabled = enabled;
        LongTermTrackingStatusComboBox.IsEnabled = enabled;
        LongTermTrackingSortComboBox.IsEnabled = enabled;
        RefreshDailyHitsButton.IsEnabled = enabled;
        StockPoolHitDatePicker.IsEnabled = enabled;
        MinutePeriodButton.IsEnabled = enabled;
        FiveDayPeriodButton.IsEnabled = enabled;
        M1PeriodButton.IsEnabled = enabled;
        M5PeriodButton.IsEnabled = enabled;
        M15PeriodButton.IsEnabled = enabled;
        M30PeriodButton.IsEnabled = enabled;
        M60PeriodButton.IsEnabled = enabled;
        DayPeriodButton.IsEnabled = enabled;
        WeekPeriodButton.IsEnabled = enabled;
        MonthPeriodButton.IsEnabled = enabled;
        MacdIndicatorButton.IsEnabled = enabled;
        KdjIndicatorButton.IsEnabled = enabled;
        RsiIndicatorButton.IsEnabled = enabled;
    }

    private string BuildHistoryFilterText()
    {
        var parts = new List<string>();
        if (_historyTradingDate.HasValue)
        {
            parts.Add($"日期 {_historyTradingDate.Value:yyyy-MM-dd}");
        }

        if (!string.IsNullOrWhiteSpace(_historySymbol))
        {
            parts.Add($"股票 {_historySymbol}");
        }

        if (!string.IsNullOrWhiteSpace(_historyStrategyCode))
        {
            parts.Add($"策略 {_historyStrategyCode}");
        }

        return parts.Count == 0 ? "全部历史" : string.Join(" | ", parts);
    }

    private static string[] ParseCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split([',', '，', ';', '；', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string FormatBacktestReturns(BacktestSignalDto item)
    {
        return $"1日{FormatPercent(item.Return1Day)}   3日{FormatPercent(item.Return3Day)}   5日{FormatPercent(item.Return5Day)}";
    }

    private static string FormatPercent(decimal? value)
    {
        return value.HasValue ? $"{value.Value:F2}%" : "--";
    }

    private static string FormatAmountYi(decimal? value)
    {
        return value.HasValue ? $"{value.Value:F2}亿" : "--";
    }

    private static decimal? Rate(int count, int total)
    {
        return total <= 0 ? null : count * 100m / total;
    }

    private static decimal? AverageNullable(IEnumerable<decimal?> values)
    {
        var available = values
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        return available.Length == 0 ? null : available.Average();
    }

    private static string FormatNullableNumber(decimal? value)
    {
        return value.HasValue ? value.Value.ToString("F4") : string.Empty;
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string BuildUsedSymbolsText(IReadOnlyList<string> symbols)
    {
        if (symbols.Count == 0)
        {
            return "--";
        }

        var preview = string.Join(",", symbols.Take(20));
        return symbols.Count > 20 ? $"{preview} ... 共 {symbols.Count} 只" : preview;
    }

    private static Brush GetWinRateBrush(decimal? value)
    {
        if (!value.HasValue)
        {
            return Brushes.Gray;
        }

        return value.Value >= 55m
            ? Brushes.ForestGreen
            : value.Value >= 45m
                ? Brushes.DarkOrange
                : Brushes.Firebrick;
    }

    private static Brush GetReturnBrush(decimal? value)
    {
        if (!value.HasValue)
        {
            return Brushes.Gray;
        }

        return value.Value > 0m ? Brushes.ForestGreen : value.Value < 0m ? Brushes.Firebrick : Brushes.DarkOrange;
    }

    private static Brush GetAshareReturnBrush(decimal? value)
    {
        if (!value.HasValue)
        {
            return Brushes.Gray;
        }

        return value.Value > 0m ? Brushes.Firebrick : value.Value < 0m ? Brushes.ForestGreen : Brushes.DarkOrange;
    }

    private static string BuildTagsText(IReadOnlyList<string>? tags)
    {
        return tags is null || tags.Count == 0
            ? string.Empty
            : $"标签：{string.Join(" / ", tags)}";
    }

    private static IReadOnlyList<KLineTradeMarker> BuildTradeMarkers(SignalEventDto? latestEvent)
    {
        if (latestEvent is null)
        {
            return [];
        }

        var bestHit = latestEvent.StrategyHits
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        if (bestHit is null)
        {
            return [];
        }

        var markerTime = latestEvent.EventTime.LocalDateTime;
        var markers = new List<KLineTradeMarker>();
        if (bestHit.Price is { } buyPrice && buyPrice > 0)
        {
            markers.Add(new KLineTradeMarker(markerTime, buyPrice, "Buy", "买点"));
        }

        if (bestHit.StopLossPrice is { } stopLoss && stopLoss > 0)
        {
            markers.Add(new KLineTradeMarker(markerTime, stopLoss, "StopLoss", "止损"));
        }

        if (bestHit.TakeProfitPrice is { } takeProfit && takeProfit > 0)
        {
            markers.Add(new KLineTradeMarker(markerTime, takeProfit, "TakeProfit", "止盈"));
        }

        return markers;
    }

    private static string BuildHeatLeaderText(IReadOnlyList<HeatLeaderDto> leaders)
    {
        if (leaders.Count == 0)
        {
            return "前排：--";
        }

        var text = string.Join("   ", leaders
            .Take(3)
            .Select(item => $"{item.Rank}.{item.Symbol} {item.Name} {item.ChangePercent:F2}%"));
        return $"前排：{text}";
    }

    private static string BuildConditionText(string title, IReadOnlyList<string>? conditions)
    {
        return conditions is null || conditions.Count == 0
            ? string.Empty
            : $"{title}：{string.Join("；", conditions)}";
    }

    private static string BuildTradePlanText(decimal? stopLossPrice, decimal? takeProfitPrice)
    {
        if (!stopLossPrice.HasValue && !takeProfitPrice.HasValue)
        {
            return string.Empty;
        }

        var stopLoss = stopLossPrice.HasValue ? $"{stopLossPrice.Value:F2}" : "--";
        var takeProfit = takeProfitPrice.HasValue ? $"{takeProfitPrice.Value:F2}" : "--";
        return $"参考：止损 {stopLoss} / 止盈 {takeProfit}";
    }

    private static string BuildMetricsText(IReadOnlyDictionary<string, decimal>? metrics)
    {
        if (metrics is null || metrics.Count == 0)
        {
            return string.Empty;
        }

        var text = string.Join("   ", metrics
            .Where(item => !item.Key.StartsWith("market_sentiment_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key)
            .Take(8)
            .Select(item => $"{TranslateMetricName(item.Key)} {FormatMetricValue(item.Value)}"));
        return string.IsNullOrWhiteSpace(text) ? string.Empty : $"诊断：{text}";
    }

    private static string BuildSignalEventSentimentText(SignalEventDto? signalEvent)
    {
        var hit = signalEvent?.StrategyHits.FirstOrDefault(item =>
            item.Metrics is not null &&
            item.Metrics.ContainsKey("market_sentiment_temperature"));
        return hit is null ? "--" : BuildStrategyHitSentimentText(hit.Metrics, hit.Tags);
    }

    private static string BuildStrategyHitSentimentText(
        IReadOnlyDictionary<string, decimal>? metrics,
        IReadOnlyList<string>? tags)
    {
        if (metrics is null || !metrics.TryGetValue("market_sentiment_temperature", out var temperature))
        {
            return string.Empty;
        }

        var adjustment = metrics.TryGetValue("market_sentiment_adjustment", out var value) ? value : 0m;
        var levelTag = tags?.FirstOrDefault(item => item.StartsWith("情绪:", StringComparison.OrdinalIgnoreCase));
        var level = string.IsNullOrWhiteSpace(levelTag) ? "--" : levelTag["情绪:".Length..];
        var action = adjustment switch
        {
            > 0 => "加权",
            < 0 => "降权",
            _ => "保持"
        };
        return $"情绪 {temperature:F1} / {level}，{action} {adjustment:+0.##;-0.##;0} 分";
    }

    private static string TranslateMetricName(string key)
    {
        return key switch
        {
            "change_percent" => "涨幅",
            "volume_ratio" => "量比",
            "daily_volume_ratio" => "日线量比",
            "platform_high" => "平台上沿",
            "platform_low" => "平台下沿",
            "platform_range_percent" => "平台振幅",
            "recent_range_percent" => "近期振幅",
            "resistance_touch_count" => "压力触碰",
            "breakout_percent" => "突破距离",
            "average_volume_20" => "20日均量",
            "above_latest_close_percent" => "较昨收",
            "above_ma20_percent" => "较MA20",
            "amount" => "成交额",
            "market_average_change" => "市场均值",
            "relative_strength_percent" => "相对强度",
            "ma5" => "MA5",
            "ma10" => "MA10",
            "ma20" => "MA20",
            "ma30" => "MA30",
            "ma60" => "MA60",
            "ma120" => "MA120",
            "ma250" => "MA250",
            "support_line" => "支撑线",
            "trend_line" => "趋势线",
            "life_line" => "生命线",
            "heart_line" => "心线",
            "trend_strength_percent" => "趋势强度",
            "ma20_slope_percent" => "MA20斜率",
            "pullback_low" => "回踩低点",
            "pullback_distance_percent" => "回踩距离",
            "pullback_volume_ratio" => "回踩量比",
            "close_breakdown_percent" => "收盘破位",
            "price_above_support_percent" => "较支撑",
            "price_above_ma5_percent" => "较MA5",
            "price_above_ma20_percent" => "较MA20",
            "recent_5d_change_percent" => "5日涨幅",
            "recent_high_break_percent" => "突破近高",
            "trend_age_days" => "趋势天数",
            "drawdown_20d_percent" => "20日回撤",
            "drawdown_60d_percent" => "60日回撤",
            "distance_from_support_percent" => "距支撑",
            "distance_from_60d_low_percent" => "距60日低点",
            "distance_from_long_support_percent" => "距长线支撑",
            "distance_from_ma120_percent" => "距MA120",
            "distance_from_ma250_percent" => "距MA250",
            "distance_from_trend_line_percent" => "距趋势线",
            "pullback_depth_percent" => "回踩深度",
            "heart_line_distance_percent" => "距心线",
            "breakout_room_percent" => "突破空间",
            "repair_from_low_percent" => "低点修复",
            "repair_from_3d_low_percent" => "3日低点修复",
            "repair_from_5d_low_percent" => "5日低点修复",
            "lower_shadow_percent" => "下影线",
            "trend_recovery_percent" => "趋势修复",
            "intraday_drawdown_percent" => "盘中下探",
            "price_above_open_percent" => "较开盘",
            "close_position_percent" => "收盘位置",
            "upper_shadow_percent" => "上影线",
            "sector_heat_score" => "行业热度",
            "sector_average_change" => "行业均涨",
            "sector_rising_ratio" => "行业上涨占比",
            "sector_total_amount" => "行业成交额",
            "sector_leader_rank" => "行业排名",
            "concept_heat_score" => "概念热度",
            "concept_average_change" => "概念均涨",
            "concept_rising_ratio" => "概念上涨占比",
            "concept_total_amount" => "概念成交额",
            "concept_leader_rank" => "概念排名",
            "market_sentiment_temperature" => "情绪温度",
            "market_sentiment_adjustment" => "情绪调分",
            _ => key
        };
    }

    private static string FormatMetricValue(decimal value)
    {
        return Math.Abs(value) >= 1_000_000m
            ? $"{value / 100_000_000m:F2}亿"
            : $"{value:F2}";
    }
    private static string NormalizeHistorySymbol(string symbol)
    {
        var value = symbol.Trim().ToLowerInvariant();
        if ((value.StartsWith("sh") || value.StartsWith("sz")) && value.Length == 8)
        {
            return value[2..];
        }

        return value;
    }

    private static string BuildDataRequirementText(StrategyDataRequirementDto requirement)
    {
        var parts = new List<string>();
        if (requirement.RequiresRealtimeQuote)
        {
            parts.Add("实时行情");
        }

        if (requirement.RequiresDailyKLine)
        {
            parts.Add($"日线 {requirement.MinDailyBarCount} 根");
        }

        if (requirement.RequiresMinuteKLine)
        {
            parts.Add("分钟线");
        }

        if (requirement.RequiresSectorData)
        {
            parts.Add("板块数据");
        }

        if (requirement.RequiresCapitalFlow)
        {
            parts.Add("资金流");
        }

        return parts.Count == 0 ? "数据要求：无特殊要求" : $"数据要求：{string.Join(" / ", parts)}";
    }

    private static string BuildStrategyParameterText(IReadOnlyDictionary<string, string> parameters)
    {
        return parameters.Count == 0
            ? "参数：无"
            : $"参数：{string.Join("   ", parameters.Select(item => $"{TranslateParameterName(item.Key)}={item.Value}"))}";
    }

    private static string TranslateStrategyStage(string value)
    {
        return value switch
        {
            "CandidateRanking" => "阶段：初筛排序",
            "PatternValidation" => "阶段：结构验证",
            "TriggerConfirmation" => "阶段：触发确认",
            "ReviewOnly" => "阶段：仅复盘",
            _ => $"阶段：{value}"
        };
    }

    private static string TranslateStrategyAction(string value)
    {
        return value switch
        {
            "Watch" => "观察",
            "Candidate" => "候选",
            "PullbackWait" => "等回踩",
            "Confirm" => "确认",
            "Reject" => "排除",
            _ => value
        };
    }

    private static string TranslateSignalConfidence(string value)
    {
        return value switch
        {
            "High" => "高",
            "Medium" => "中",
            "Low" => "低",
            _ => value
        };
    }

    private static string NormalizeSymbolKey(string value)
    {
        var text = value.Trim();
        var dotIndex = text.IndexOf('.');
        return dotIndex > 0 ? text[..dotIndex] : text;
    }

    private static string TranslateParameterName(string key)
    {
        return key switch
        {
            "min_change_percent" => "最小涨幅",
            "min_volume_ratio" => "最小量比",
            "max_result_count" => "最大结果数",
            "min_amount" => "最小成交额",
            "ma_short" => "短均线",
            "ma_long" => "长均线",
            "pullback_lookback" => "回踩窗口",
            "max_distance_from_ma20_percent" => "距MA20上限",
            _ => key
        };
    }

    private sealed record SignalEventDisplay(
        string EventTime,
        string EventType,
        string Symbol,
        string Name,
        string StrategyName,
        string ScoreText,
        string Reason);

    private sealed record HeatBoardDisplay(
        string Name,
        string HeatText,
        string BreadthText,
        string LeaderText);

    private sealed record OpportunityDisplay(
        Guid Id,
        string Symbol,
        string Name,
        string StatusText,
        decimal CurrentScore,
        string ScoreText,
        int HitCount,
        string StrategySummary,
        string HitTimeText,
        string StrategyExplanation);

    private sealed record StrategyHitDisplay(
        string StrategyName,
        decimal Score,
        string SentimentText,
        string Reason,
        string Risk,
        string PassedConditionsText,
        string FailedConditionsText,
        string TradePlanText,
        string TagsText,
        string MetricsText);

    private sealed record MarketSentimentDataSourceDisplay(
        string Code,
        string StatusText,
        Brush StatusBrush);

    private sealed record MarketSentimentMetricDisplay(
        string Name,
        string ValueText,
        string SourceText);

    private sealed record MarketSentimentRegimeDisplay(
        string Label,
        string ScoreText,
        string TimeText,
        Brush AccentBrush);

    private sealed record StrategyDefinitionDisplay(
        string Code,
        string Name,
        string StageText,
        string DefaultActionText,
        string DataRequirementText,
        string ParameterText,
        string Description);

    private sealed record HistoricalSignalDisplay(
        string EventTime,
        string EventType,
        string Symbol,
        string Name,
        string StrategyName,
        string ScoreText,
        string HitText,
        string Reason);

    private sealed record PredictionRecordDisplay(
        string Symbol,
        string Name,
        string PredictionText,
        Brush PredictionBrush,
        string StrategyText,
        string VerifyText,
        string ReasonText,
        Brush ReasonBrush,
        string RiskText,
        string ScoreBadgeText,
        string MainStrategyText,
        string SignalCountText,
        string StrategyHitCountText,
        string UpProbabilityText,
        string DownProbabilityText,
        double UpProbabilityWidth,
        string ConfidenceText,
        string ExtraTagText,
        Visibility ExtraTagVisibility,
        string VerifyBadgeText);

    private sealed record LongTermTrackingDisplay(
        Guid Id,
        string Symbol,
        string Name,
        string StrategyCode,
        string StrategyName,
        string CurrentPriceText,
        string ReturnFromHitText,
        Brush ReturnFromHitBrush,
        string ScoreText,
        string HitCountText,
        string HitRangeText,
        IReadOnlyList<LongTermTrackingTagDisplay> StatusTags,
        string Risk);

    private sealed record LongTermTrackingTagDisplay(
        string Text,
        Brush Foreground,
        Brush Background)
    {
        public static LongTermTrackingTagDisplay Positive(string text) =>
            new(text, new SolidColorBrush(Color.FromRgb(22, 137, 85)), new SolidColorBrush(Color.FromRgb(228, 248, 237)));

        public static LongTermTrackingTagDisplay Warning(string text) =>
            new(text, new SolidColorBrush(Color.FromRgb(190, 111, 20)), new SolidColorBrush(Color.FromRgb(255, 242, 222)));

        public static LongTermTrackingTagDisplay Danger(string text) =>
            new(text, new SolidColorBrush(Color.FromRgb(209, 78, 78)), new SolidColorBrush(Color.FromRgb(255, 232, 232)));

        public static LongTermTrackingTagDisplay Info(string text) =>
            new(text, new SolidColorBrush(Color.FromRgb(47, 112, 220)), new SolidColorBrush(Color.FromRgb(232, 241, 255)));
    }

    private sealed record BacktestSignalDisplay(
        string TradingDate,
        string Symbol,
        string Name,
        string StrategyCode,
        string StrategyName,
        DateOnly TradingDateValue,
        string Action,
        string ActionText,
        string Confidence,
        string ConfidenceText,
        string ScoreText,
        string ReturnText,
        string Reason,
        string? Risk,
        decimal Score,
        decimal? Price,
        decimal? Return1Day,
        decimal? Return3Day,
        decimal? Return5Day);

    private sealed record BacktestStrategySummaryDisplay(
        string StrategyName,
        string SignalCountText,
        string WinRateText,
        string ReturnText,
        Brush WinRateBrush,
        Brush ReturnBrush);

    private sealed record BacktestSentimentSummaryDisplay(
        string SentimentLevel,
        string SignalCountText,
        string WinRateText,
        string ReturnText,
        Brush WinRateBrush,
        Brush ReturnBrush);

    private sealed record DailyHitDisplay(
        string Symbol,
        string Name,
        DateTimeOffset EventTime,
        string StrategyName,
        IReadOnlyList<string> StrategyNames,
        int StrategyCount,
        int HitCount,
        decimal Score,
        decimal? Price,
        string Reason,
        string? Risk,
        string ScoreText,
        string PriceText,
        string LatestHitTimeText,
        string EventTypeText,
        string ManualStatusText,
        string StrategyText,
        string SignalText,
        string ReviewText,
        string DetailTitle,
        string RiskText);

    private sealed record StrategyPerformanceDisplay(
        string StrategyName,
        int HitCount,
        decimal AverageScore,
        decimal MaxScore,
        string LastHitTime,
        string SummaryText);
}



