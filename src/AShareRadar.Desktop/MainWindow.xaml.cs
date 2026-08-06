using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AShareRadar.Desktop.Controls;
using AShareRadar.Contracts.Backtesting;
using AShareRadar.Contracts.History;
using AShareRadar.Contracts.MarketData;
using AShareRadar.Contracts.Monitoring;
using AShareRadar.Contracts.Opportunities;
using AShareRadar.Contracts.Qlib;
using AShareRadar.Contracts.Review;
using AShareRadar.Contracts.Strategies;
using AShareRadar.Contracts.StrategyTraining;
using AShareRadar.Desktop.Services;
using Microsoft.Win32;

namespace AShareRadar.Desktop;

public partial class MainWindow : Window
{
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
    private bool _showStrategyTraining;
    private bool _showBacktest;
    private bool _showStockPools;
    private bool _showQlibStrategy;
    private bool _showResearchPage;
    private bool _isRefreshingOpportunityList;
    private int _dailyHitCount;
    private DateOnly? _historyTradingDate;
    private DateOnly _predictionDate = DateOnly.FromDateTime(DateTime.Today);
    private string? _historySymbol;
    private string? _historyStrategyCode;
    private BacktestReplayResultDto? _lastBacktestResult;
    private bool _isResearchDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        HistoryDatePicker.SelectedDate = null;
        PredictionDatePicker.SelectedDate = DateTime.Today;
        BacktestStartDatePicker.SelectedDate = DateTime.Today.AddDays(-60);
        BacktestEndDatePicker.SelectedDate = DateTime.Today;
        StrategyTrainingStartDatePicker.SelectedDate = DateTime.Today.AddDays(-60);
        StrategyTrainingEndDatePicker.SelectedDate = DateTime.Today;
        StockPoolHitDatePicker.SelectedDate = DateTime.Today;
        StrategyTrainingStrategyComboBox.ItemsSource = new[]
        {
            new StrategyTrainingOptionDisplay(null, "全部策略")
        };
        StrategyTrainingStrategyComboBox.SelectedIndex = 0;
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

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            await _apiClient.StartAsync(GetSelectedIntervalSeconds(), token);
            await RefreshAsync(token);
        });
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            await _apiClient.PauseAsync(token);
            await RefreshAsync(token);
        });
    }

    private async void ScanOnceButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            await _apiClient.ScanOnceAsync(token);
            await RefreshAsync(token);
        });
    }

    private async void UpdateHistoryDataButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            HistoricalDataStatusText.Text = "历史数据：正在启动更新...";
            var status = await _apiClient.TriggerHistoricalDataUpdateAsync(token);
            ApplyHistoricalDataUpdateStatus(status);
        });
    }

    private async void UpdateMarketMappingButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            MappingUpdateStatusText.Text = "概念行业：正在启动更新...";
            var status = await _apiClient.TriggerMarketMappingUpdateAsync(token);
            ApplyMarketMappingUpdateStatus(status);
        });
    }

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
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
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
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
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
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
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
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
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
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
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
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
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
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
        ApplyWorkspaceVisibility();
        await RefreshAsync();
    }

    private async void StrategyTrainingViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showStrategyTraining = true;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
        ApplyWorkspaceVisibility();
        await LoadStrategyTrainingOptionsAsync();
    }

    private void BacktestViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showStrategyTraining = false;
        _showBacktest = true;
        _showStockPools = false;
        _showQlibStrategy = false;
        ApplyWorkspaceVisibility();
    }

    private async void QlibStrategyViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = true;
        ApplyWorkspaceVisibility();
        await RefreshQlibStrategyAsync();
    }

    private async void StockPoolsViewButton_Click(object sender, RoutedEventArgs e)
    {
        _showResearchPage = true;
        _showMarketSentiment = false;
        _showHistory = false;
        _showPredictionReview = false;
        _showStrategyCenter = false;
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = true;
        _showQlibStrategy = false;
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
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
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
        _showStrategyTraining = false;
        _showBacktest = false;
        _showStockPools = false;
        _showQlibStrategy = false;
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
            _showStrategyTraining = false;
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
            PredictionSummaryText.Text = "正在生成次日预测...";
            PredictionWaitText.Text = "正在调用后端生成预测，请稍候...";
            PredictionRecordListBox.ItemsSource = Array.Empty<PredictionRecordDisplay>();
            var review = await _apiClient.GeneratePredictionReviewAsync(_predictionDate, token);
            ApplyPredictionReview(review);
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

    private async void RefreshQlibStrategyButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshQlibStrategyAsync();
    }

    private async void ImportQlibSeedsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async token =>
        {
            QlibStrategySummaryText.Text = "正在导入低位星火策略候选清单...";
            var result = await _apiClient.ImportQlibR013SeedsAsync(token);
            QlibStrategySummaryText.Text = result is null
                ? "导入完成，但后端没有返回明细。"
                : $"已导入 {result.ImportedCount} 条候选 | 信号日 {result.SignalDate:yyyy-MM-dd} | 来源实验 {result.SourceExperimentId}";
            await RefreshQlibStrategyAsync(token);
        });
    }

    private async void QlibCandidateDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QlibCandidateDataGrid.SelectedItem is QlibCandidateDisplay item)
        {
            ApplyQlibCandidateDetail(item);
            _selectedSymbol = item.Code;
            _selectedName = item.Name;
            ChartTitleText.Text = $"{item.Code} {item.Name} K线分析";
            ChartCaptionText.Text = $"低位星火策略候选 | 排名 {item.ModelRank} | 实时状态：{item.RealtimeStateText}";
            await RunUiActionAsync(async token => await RefreshKLineAsync(token));
        }
    }

    private async void RefreshDailyHitsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDailyHitsAsync();
    }
    private async void DailyHitListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DailyHitListBox.SelectedItem is DailyHitDisplay item)
        {
            await SelectDailyHitAsync(item);
        }
    }

    private async void RunBacktestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBacktestAsync();
    }

    private async void BuildStrategyTrainingDatasetButton_Click(object sender, RoutedEventArgs e)
    {
        await BuildStrategyTrainingDatasetAsync();
    }

    private async void RunStrategyTrainingButton_Click(object sender, RoutedEventArgs e)
    {
        await RunStrategyTrainingAsync();
    }

    private async void SaveStrategyParameterProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: StrategyTrainingResultDisplay result })
        {
            await SaveStrategyParameterProfileAsync(result, activate: false);
        }
    }

    private async void ActivateStrategyParameterProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: StrategyTrainingResultDisplay result })
        {
            await SaveStrategyParameterProfileAsync(result, activate: true);
        }
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
            var manualStatusBySymbol = opportunitiesTask.Result
                .GroupBy(item => NormalizeHistorySymbol(item.Symbol), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => BuildDailyHitReviewText(group.OrderByDescending(item => item.LastSeenTime).First()),
                    StringComparer.OrdinalIgnoreCase);
            var dailyHits = signalsTask.Result
                .GroupBy(item => NormalizeHistorySymbol(item.Symbol), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var ordered = group
                        .OrderByDescending(item => item.Score)
                        .ThenByDescending(item => item.EventTime)
                        .ToArray();
                    var best = ordered[0];
                    var strategyNames = ordered
                        .Select(item => item.StrategyName)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray();
                    manualStatusBySymbol.TryGetValue(NormalizeHistorySymbol(best.Symbol), out var manualStatus);

                    return new DailyHitDisplay(
                        NormalizeHistorySymbol(best.Symbol),
                        best.Name,
                        best.EventTime,
                        best.StrategyName,
                        best.Score,
                        best.Price,
                        best.Reason,
                        best.Risk,
                        $"强度 {best.Score:F2}",
                        strategyNames.Length == 0 ? "命中策略：--" : $"命中策略：{string.Join(" / ", strategyNames)}",
                        $"最近命中 {FormatTime(best.EventTime)} | 策略 {ordered.Length} 条 | 事件 {TranslateEventType(best.EventType)}",
                        $"人工操作：{manualStatus ?? "未处理"}");
                })
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.EventTime)
                .ToArray();

            DailyHitListBox.ItemsSource = dailyHits;
            _dailyHitCount = dailyHits.Length;
            DailyHitSummaryText.Text = dailyHits.Length == 0
                ? $"{tradingDate:yyyy-MM-dd} 当前日期暂无每日命中，可切换日期或等待扫描。"
                : $"{tradingDate:yyyy-MM-dd} 命中股票 {dailyHits.Length} 只，点击左侧股票在右侧查看 K 线。";
            if (dailyHits.Length == 0)
            {
                DailyHitListBox.SelectedItem = null;
                ClearDailyHitKLine();
            }

            ApplyWorkspaceVisibility();
            if (dailyHits.Length > 0 && DailyHitListBox.SelectedItem is null)
            {
                DailyHitListBox.SelectedItem = dailyHits[0];
            }
        }
        catch (Exception ex)
        {
            DailyHitListBox.ItemsSource = null;
            _dailyHitCount = 0;
            ClearDailyHitKLine();
            DailyHitSummaryText.Text = $"每日命中加载失败：{ex.Message}";
            ApplyWorkspaceVisibility();
        }
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

    private async Task SelectDailyHitAsync(DailyHitDisplay item)
    {
        _selectedOpportunityId = null;
        _selectedSymbol = item.Symbol;
        _selectedName = item.Name;
        SnapshotTitleText.Text = $"{item.Symbol} {item.Name}";
        ChartTitleText.Text = $"{item.Symbol} {item.Name} K线复盘";
        ChartCaptionText.Text = $"{item.StrategyText} | {item.SignalText} | {item.ReviewText}";
        KLineChart.SymbolName = $"{item.Symbol} {item.Name}";
        KLineChart.TradeMarkers = item.Price is { } price && price > 0
            ? [new KLineTradeMarker(item.EventTime.LocalDateTime, price, "Buy", "信号")]
            : [];
        await RunUiActionAsync(async token => await RefreshKLineAsync(token));
        KLineChart.TradeMarkers = item.Price is { } markerPrice && markerPrice > 0
            ? [new KLineTradeMarker(item.EventTime.LocalDateTime, markerPrice, "Buy", "信号")]
            : [];
        FooterText.Text = $"已打开每日命中 K 线：{item.Symbol} {item.Name}，{item.StrategyName}，强度 {item.Score:F2}";
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

        if (OpportunityListBox.SelectedItem is not OpportunityDisplay opportunity)
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

            await ApplySnapshotAsync(detail, token);
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

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var statusTask = _apiClient.GetMonitorStatusAsync(cancellationToken);
            var marketDataStatusTask = _apiClient.GetMarketDataStatusAsync(cancellationToken);
            var historicalDataStatusTask = _apiClient.GetHistoricalDataUpdateStatusAsync(cancellationToken);
            var marketMappingStatusTask = _apiClient.GetMarketMappingUpdateStatusAsync(cancellationToken);
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
            var sectorHeatTask = !_showResearchPage && _showSectorHeat
                ? _apiClient.GetSectorHeatAsync(12, cancellationToken)
                : Task.FromResult<IReadOnlyList<HeatBoardItemDto>>([]);
            var conceptHeatTask = !_showResearchPage && _showConceptHeat
                ? _apiClient.GetConceptHeatAsync(12, cancellationToken)
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
                marketMappingStatusTask,
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
            ApplyMarketMappingUpdateStatus(marketMappingStatusTask.Result);
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
                _isRefreshingOpportunityList = true;
                try
                {
                    OpportunityListBox.ItemsSource = opportunityDisplays;
                    OpportunityListBox.SelectedItem = selectedOpportunityId.HasValue
                        ? opportunityDisplays.FirstOrDefault(item => item.Id == selectedOpportunityId.Value)
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
            FooterText.Text = $"后端响应较慢：接口超过等待时间。服务仍可访问，请稍后刷新或减少同时打开的热度/复盘视图。{ex.Message}";
        }
        catch (Exception ex)
        {
            FooterText.Text = $"后端不可用：{ex.Message}";
        }
    }

    private async Task StartRealtimeClientAsync()
    {
        try
        {
            _realtimeClient.MessageReceived += async (_, _) =>
            {
                await Dispatcher.InvokeAsync(async () => await RefreshAsync());
            };

            await _realtimeClient.StartAsync(CancellationToken.None);
            FooterText.Text = "实时推送已连接。";
        }
        catch (Exception ex)
        {
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
            StatusText.Text = "市场：--   监控：--   数据源：--   上次扫描：--   下次扫描：--";
            SummaryText.Text = "活跃机会：--   今日新增：--   消失：--   重点跟踪：--";
            return;
        }

        StatusText.Text = $"市场：{TranslateMarketStatus(status.MarketStatus)}   监控：{TranslateMonitorStatus(status.MonitorStatus)}   数据源：{BuildMarketDataLabel(marketDataStatus)}   上次扫描：{FormatTime(status.LastScanTime)}   下次扫描：{FormatTime(status.NextScanTime)}";
        SummaryText.Text = $"活跃机会：{status.ActiveOpportunityCount}   今日新增：{status.TodayNewCount}   消失：{status.DisappearedCount}   重点跟踪：{status.FocusedCount}   历史策略：{TranslateMonitorStatus(status.HistoricalStrategyScanStatus)} {status.HistoricalStrategyScanSignalCount} 条 / {status.HistoricalStrategyScanSymbolCount} 只   上次：{FormatTime(status.LastHistoricalStrategyScanTime)}   下次：{FormatTime(status.NextHistoricalStrategyScanTime)}";
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

    private void ApplyMarketMappingUpdateStatus(MarketMappingUpdateStatusDto? status)
    {
        if (status is null)
        {
            MappingUpdateStatusText.Text = "概念行业：未检测";
            return;
        }

        var runState = status.IsRunning ? "更新中" : "空闲";
        var lastRun = status.LastFinishedAt.HasValue
            ? status.LastFinishedAt.Value.ToString("HH:mm:ss")
            : "--";
        MappingUpdateStatusText.Text =
            $"概念行业：{runState} | 行业 {status.SectorMappingCount} | 概念 {status.ConceptMappingCount} | 上次 {lastRun}";
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
        itemsControl.ItemsSource = items
            .Select(item => new HeatBoardDisplay(
                item.Name,
                $"热度 {item.HeatScore:F1}",
                $"均涨 {item.AverageChangePercent:F2}%   上涨 {item.RisingCount}/{item.StockCount}   成交额 {item.TotalAmount / 100_000_000m:F1} 亿",
                BuildHeatLeaderText(item.Leaders)))
            .ToArray();
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

    private async Task BuildStrategyTrainingDatasetAsync()
    {
        var request = BuildStrategyTrainingDatasetRequest();
        await RunUiActionAsync(async token =>
        {
            StrategyTrainingSummaryText.Text = request.ForceRebuild
                ? "正在强制重新生成训练样本，长日期区间可能需要数分钟..."
                : "正在生成训练样本...";
            StrategyTrainingResultListBox.ItemsSource = null;
            StrategyTrainingSampleListBox.ItemsSource = null;
            var dataset = await _apiClient.BuildStrategyTrainingDatasetAsync(request, token);
            ApplyStrategyTrainingDataset(dataset);
        }, GetStrategyTrainingTimeout(request));
    }

    private async Task RunStrategyTrainingAsync()
    {
        var request = BuildStrategyTrainingRunRequest();
        await RunUiActionAsync(async token =>
        {
            StrategyTrainingSummaryText.Text = request.ForceRebuild
                ? "正在强制重新生成样本并运行参数网格训练，长日期区间可能需要数分钟..."
                : "正在运行参数网格训练...";
            StrategyTrainingResultListBox.ItemsSource = null;
            var run = await _apiClient.RunStrategyTrainingAsync(request, token);
            ApplyStrategyTrainingRun(run);
        }, GetStrategyTrainingTimeout(request));
    }

    private static TimeSpan GetStrategyTrainingTimeout(StrategyTrainingDatasetRequest request)
    {
        return GetStrategyTrainingTimeout(request.StartDate, request.EndDate, request.ForceRebuild);
    }

    private static TimeSpan GetStrategyTrainingTimeout(StrategyTrainingRunRequest request)
    {
        return GetStrategyTrainingTimeout(request.StartDate, request.EndDate, request.ForceRebuild);
    }

    private static TimeSpan GetStrategyTrainingTimeout(DateOnly startDate, DateOnly endDate, bool forceRebuild)
    {
        var daySpan = endDate.DayNumber - startDate.DayNumber;
        return forceRebuild
            ? TimeSpan.FromMinutes(daySpan > 370 ? 15 : 5)
            : TimeSpan.FromMinutes(3);
    }

    private async Task SaveStrategyParameterProfileAsync(StrategyTrainingResultDisplay result, bool activate)
    {
        if (!result.CanApply || string.IsNullOrWhiteSpace(result.StrategyCode))
        {
            MessageBox.Show("请先选择单个策略运行训练，再保存或应用参数方案。", "策略参数", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunUiActionAsync(async token =>
        {
            var profileName = $"{result.StrategyCode} Top{result.Rank} {DateTime.Now:MMdd-HHmm}";
            var profile = await _apiClient.SaveStrategyParameterProfileAsync(
                new SaveStrategyParameterProfileRequest(
                    result.StrategyCode,
                    profileName,
                    result.RunId,
                    result.MinScore,
                    result.MinAmountYi,
                    result.MinRelativeStrengthPercent,
                    result.MinHeatScore,
                    result.MaxOutputPerDay,
                    result.SampleCount,
                    result.SuccessRate,
                    result.AverageNextHighReturn,
                    result.AverageNextCloseReturn),
                token);

            if (activate && profile is not null)
            {
                profile = await _apiClient.ActivateStrategyParameterProfileAsync(profile.Id, token);
            }

            StrategyTrainingSummaryText.Text = activate
                ? $"已应用参数方案：{profile?.ProfileName ?? profileName}。实时扫描、历史扫描和回放将读取这组参数。"
                : $"已保存参数方案：{profile?.ProfileName ?? profileName}。点击“应用”后才会影响策略运行。";
        });
    }

    private async Task LoadStrategyTrainingOptionsAsync()
    {
        await RunUiActionAsync(async token =>
        {
            var selectedCode = StrategyTrainingStrategyComboBox.SelectedValue as string;
            var strategies = await _apiClient.GetStrategiesAsync(token);
            var options = new List<StrategyTrainingOptionDisplay>
            {
                new(null, "全部策略")
            };
            options.AddRange(strategies
                .OrderBy(item => item.Name)
                .Select(item => new StrategyTrainingOptionDisplay(item.Code, $"{item.Name} ({item.Code})")));

            StrategyTrainingStrategyComboBox.ItemsSource = options;
            var selected = options.FindIndex(item => string.Equals(item.Code, selectedCode, StringComparison.OrdinalIgnoreCase));
            StrategyTrainingStrategyComboBox.SelectedIndex = selected >= 0
                ? selected
                : Math.Max(0, options.FindIndex(item => string.Equals(item.Code, "main-sector-resonance", StringComparison.OrdinalIgnoreCase)));
        });
    }

    private StrategyTrainingDatasetRequest BuildStrategyTrainingDatasetRequest()
    {
        var startDate = StrategyTrainingStartDatePicker.SelectedDate.HasValue
            ? DateOnly.FromDateTime(StrategyTrainingStartDatePicker.SelectedDate.Value)
            : DateOnly.FromDateTime(DateTime.Today.AddDays(-60));
        var endDate = StrategyTrainingEndDatePicker.SelectedDate.HasValue
            ? DateOnly.FromDateTime(StrategyTrainingEndDatePicker.SelectedDate.Value)
            : DateOnly.FromDateTime(DateTime.Today);
        var strategyCode = StrategyTrainingStrategyComboBox.SelectedValue as string;
        strategyCode = string.IsNullOrWhiteSpace(strategyCode)
            ? null
            : strategyCode.Trim();
        var highReturn = decimal.TryParse(StrategyTrainingHighReturnTextBox.Text, out var parsedHighReturn)
            ? parsedHighReturn
            : 2m;

        return new StrategyTrainingDatasetRequest(
            startDate,
            endDate,
            strategyCode,
            highReturn,
            StrategyTrainingPositiveCloseCheckBox.IsChecked == true,
            StrategyTrainingForceRebuildCheckBox.IsChecked == true,
            ParseDecimalGrid(StrategyTrainingScoreThresholdsTextBox.Text),
            ParseDecimalGrid(StrategyTrainingAmountThresholdsTextBox.Text),
            ParseDecimalGrid(StrategyTrainingRelativeStrengthThresholdsTextBox.Text),
            ParseDecimalGrid(StrategyTrainingHeatThresholdsTextBox.Text),
            ParseIntGrid(StrategyTrainingOutputLimitsTextBox.Text));
    }

    private StrategyTrainingRunRequest BuildStrategyTrainingRunRequest()
    {
        var datasetRequest = BuildStrategyTrainingDatasetRequest();
        return new StrategyTrainingRunRequest(
            datasetRequest.StartDate,
            datasetRequest.EndDate,
            datasetRequest.StrategyCode,
            datasetRequest.SuccessHighReturnThreshold,
            datasetRequest.RequirePositiveClose,
            datasetRequest.ForceRebuild,
            datasetRequest.ScoreThresholds,
            datasetRequest.AmountThresholds,
            datasetRequest.RelativeStrengthThresholds,
            datasetRequest.HeatThresholds,
            datasetRequest.OutputLimits);
    }

    private static decimal[]? ParseDecimalGrid(string? value)
    {
        var items = (value ?? string.Empty)
            .Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => decimal.TryParse(item, out var parsed) ? parsed : (decimal?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .Distinct()
            .ToArray();
        return items.Length == 0 ? null : items;
    }

    private static int[]? ParseIntGrid(string? value)
    {
        var items = (value ?? string.Empty)
            .Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, out var parsed) ? parsed : (int?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .Distinct()
            .ToArray();
        return items.Length == 0 ? null : items;
    }

    private void ApplyStrategyTrainingDataset(StrategyTrainingDatasetDto? dataset)
    {
        if (dataset is null)
        {
            StrategyTrainingSummaryText.Text = "暂无训练样本结果";
            StrategyTrainingSampleListBox.ItemsSource = null;
            return;
        }

        StrategyTrainingSummaryText.Text =
            $"区间 {dataset.StartDate:yyyy-MM-dd} 至 {dataset.EndDate:yyyy-MM-dd} | 原始信号 {dataset.SourceSignalCount} 条 | 样本 {dataset.SampleCount} 条 | 成功 {dataset.SuccessCount} 条 | 成功率 {FormatPercent(dataset.SuccessRate)} | {dataset.Message}";
        StrategyTrainingResultTitleText.Text = "样本总结";
        StrategyTrainingResultListBox.ItemsSource = BuildStrategyTrainingDatasetSummary(dataset);
        StrategyTrainingSampleListBox.ItemsSource = dataset.Samples
            .Take(80)
            .Select(item => new StrategyTrainingSampleDisplay(
                item.SignalDate.ToString("yyyy-MM-dd"),
                item.Symbol,
                item.Name,
                $"{item.StrategyName}  分数 {item.Score:F2}",
                $"5日：首开 {FormatPercent(item.NextOpenReturn)} / 最高 {FormatPercent(item.NextHighReturn)} / 收 {FormatPercent(item.NextCloseReturn)}",
                BuildStrategyTrainingFactorText(item),
                item.IsSuccess ? "成功" : "未达标",
                item.IsSuccess ? Brushes.ForestGreen : Brushes.Gray))
            .ToArray();
    }

    private void ApplyStrategyTrainingRun(StrategyTrainingRunDto? run)
    {
        if (run is null)
        {
            StrategyTrainingSummaryText.Text = "暂无训练结果";
            StrategyTrainingResultListBox.ItemsSource = null;
            return;
        }

        StrategyTrainingSummaryText.Text =
            $"区间 {run.StartDate:yyyy-MM-dd} 至 {run.EndDate:yyyy-MM-dd} | 样本 {run.SampleCount} 条 | 参数组合 {run.ResultCount} 组 | {run.Message}";
        StrategyTrainingResultTitleText.Text = "最优参数";
        StrategyTrainingResultListBox.ItemsSource = run.Results
            .Select(item => new StrategyTrainingResultDisplay(
                item.Rank,
                $"命中 {item.HitCount} / 成功 {item.SuccessCount}",
                FormatPercent(item.SuccessRate),
                item.SuccessRate.GetValueOrDefault() >= 60m ? Brushes.ForestGreen : Brushes.DarkOrange,
                $"分数>={item.MinScore:F0} | 成交额>={item.MinAmountYi:F0}亿 | 相对强度>={item.MinRelativeStrengthPercent:F0}% | 热度>={item.MinHeatScore:F0} | 每日Top {item.MaxOutputPerDay}",
                $"5日：首开 {FormatPercent(item.AverageNextOpenReturn)} / 最高 {FormatPercent(item.AverageNextHighReturn)} / 收 {FormatPercent(item.AverageNextCloseReturn)} | 最差收 {FormatPercent(item.WorstNextCloseReturn)}",
                item.Summary,
                CanApply: !string.IsNullOrWhiteSpace(run.StrategyCode),
                StrategyCode: run.StrategyCode,
                RunId: run.RunId,
                MinScore: item.MinScore,
                MinAmountYi: item.MinAmountYi,
                MinRelativeStrengthPercent: item.MinRelativeStrengthPercent,
                MinHeatScore: item.MinHeatScore,
                MaxOutputPerDay: item.MaxOutputPerDay,
                SampleCount: item.HitCount,
                SuccessRate: item.SuccessRate,
                AverageNextHighReturn: item.AverageNextHighReturn,
                AverageNextCloseReturn: item.AverageNextCloseReturn))
            .ToArray();
    }

    private static IReadOnlyList<StrategyTrainingResultDisplay> BuildStrategyTrainingDatasetSummary(StrategyTrainingDatasetDto dataset)
    {
        var summaries = new List<StrategyTrainingResultDisplay>();
        if (dataset.Samples.Count == 0)
        {
            summaries.Add(new StrategyTrainingResultDisplay(
                1,
                "样本 0 / 成功 0",
                "--",
                Brushes.Gray,
                "当前区间没有可验证训练样本",
                "请确认历史信号或历史日线覆盖到下一交易日。",
                dataset.Message));
            return summaries;
        }

        summaries.Add(new StrategyTrainingResultDisplay(
            1,
            $"样本 {dataset.SampleCount} / 成功 {dataset.SuccessCount}",
            FormatPercent(dataset.SuccessRate),
            dataset.SuccessRate.GetValueOrDefault() >= 50m ? Brushes.ForestGreen : Brushes.DarkOrange,
            BuildTrainingDateSpanText(dataset.Samples),
            BuildTrainingReturnSummary(dataset.Samples),
            "整体样本统计：用于判断当前成功标准下，这个策略是否具备继续调参价值。"));

        summaries.AddRange(dataset.Samples
            .GroupBy(item => new { item.StrategyCode, item.StrategyName })
            .OrderByDescending(group => group.Count())
            .Take(8)
            .Select((group, index) =>
            {
                var items = group.ToArray();
                var successCount = items.Count(item => item.IsSuccess);
                var successRate = Rate(successCount, items.Length);
                return new StrategyTrainingResultDisplay(
                    index + 2,
                    $"{group.Key.StrategyName}  样本 {items.Length} / 成功 {successCount}",
                    FormatPercent(successRate),
                    successRate.GetValueOrDefault() >= 50m ? Brushes.ForestGreen : Brushes.DarkOrange,
                    group.Key.StrategyCode,
                    BuildTrainingReturnSummary(items),
                    $"平均分 {items.Average(item => item.Score):F2} | 最高分 {items.Max(item => item.Score):F2}");
            }));

        return summaries;
    }

    private static string BuildTrainingDateSpanText(IReadOnlyList<StrategyTrainingSampleDto> samples)
    {
        var minDate = samples.Min(item => item.SignalDate);
        var maxDate = samples.Max(item => item.SignalDate);
        return $"样本日期 {minDate:yyyy-MM-dd} 至 {maxDate:yyyy-MM-dd}";
    }

    private static string BuildTrainingReturnSummary(IReadOnlyList<StrategyTrainingSampleDto> samples)
    {
        return $"5日均值：首开 {FormatPercent(AverageNullable(samples.Select(item => item.NextOpenReturn)))} / 最高 {FormatPercent(AverageNullable(samples.Select(item => item.NextHighReturn)))} / 收 {FormatPercent(AverageNullable(samples.Select(item => item.NextCloseReturn)))}";
    }

    private static string BuildStrategyTrainingFactorText(StrategyTrainingSampleDto item)
    {
        var metrics = item.Metrics?
            .Where(metric => !metric.Key.StartsWith("market_sentiment_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(metric => metric.Key, metric => metric.Value, StringComparer.OrdinalIgnoreCase);
        if (metrics is { Count: > 0 })
        {
            var preferredKeys = item.StrategyCode switch
            {
                "strong-repair-rebound" => new[]
                {
                    "repair_from_low_percent",
                    "intraday_drawdown_percent",
                    "price_above_ma20_percent",
                    "close_position_percent",
                    "volume_ratio",
                    "upper_shadow_percent"
                },
                "main-sector-resonance" => new[]
                {
                    "amount",
                    "relative_strength_percent",
                    "sector_heat_score",
                    "concept_heat_score",
                    "sector_rising_ratio"
                },
                "platform-volume-breakout" => new[]
                {
                    "amount",
                    "breakout_percent",
                    "platform_range_percent",
                    "volume_ratio",
                    "relative_strength_percent"
                },
                "counter-trend-strength" => new[]
                {
                    "relative_strength_percent",
                    "market_average_change",
                    "volume_ratio",
                    "price_above_ma20_percent"
                },
                _ => Array.Empty<string>()
            };

            var selected = preferredKeys
                .Where(metrics.ContainsKey)
                .Select(key => new KeyValuePair<string, decimal>(key, metrics[key]))
                .Concat(metrics
                    .Where(metric => !preferredKeys.Contains(metric.Key, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(metric => metric.Key))
                .Take(5)
                .Select(metric => $"{TranslateMetricName(metric.Key)} {FormatMetricValue(metric.Value)}")
                .ToArray();
            if (selected.Length > 0)
            {
                return string.Join(" | ", selected);
            }
        }

        var heatScore = item.SectorHeatScore.HasValue || item.ConceptHeatScore.HasValue
            ? $"{Math.Max(item.SectorHeatScore ?? 0m, item.ConceptHeatScore ?? 0m):F1}"
            : "--";
        return $"成交额 {FormatAmountYi(item.AmountYi)} | 相对强度 {FormatPercent(item.RelativeStrengthPercent)} | 热度 {heatScore}";
    }

    private async Task RefreshQlibStrategyAsync(CancellationToken cancellationToken = default)
    {
        await RunUiActionAsync(async token =>
        {
            var activeToken = cancellationToken == default ? token : cancellationToken;
            QlibStrategySummaryText.Text = "正在读取低位星火策略数据...";
            var statusTask = _apiClient.GetQlibR013SignalStatusAsync(activeToken);
            var seedsTask = _apiClient.GetQlibR013SeedsAsync(null, 200, activeToken);
            var opportunitiesTask = _apiClient.GetOpportunitiesAsync("Current", activeToken);
            var latestTask = _apiClient.GetQlibR013LatestAsync(activeToken);
            var rebalanceTask = _apiClient.GetQlibR013RebalancePlanAsync(activeToken);

            await Task.WhenAll(statusTask, seedsTask, opportunitiesTask, latestTask, rebalanceTask);

            ApplyQlibStrategyPage(
                statusTask.Result,
                seedsTask.Result,
                opportunitiesTask.Result,
                latestTask.Result,
                rebalanceTask.Result);
        });
    }

    private void ApplyQlibStrategyPage(
        QlibSignalStatusDto? status,
        IReadOnlyList<QlibSignalSeedDto> seeds,
        IReadOnlyList<OpportunityDto> opportunities,
        QlibSignalSnapshotDto? latest,
        QlibSignalSnapshotDto? rebalancePlan)
    {
        var opportunitiesBySymbol = opportunities
            .GroupBy(item => NormalizeSymbolKey(item.Symbol), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CurrentScore).First(), StringComparer.OrdinalIgnoreCase);

        var latestSignalDate = seeds.Count > 0
            ? seeds.Max(item => item.SignalDate)
            : latest?.SignalDate ?? status?.SignalDate;
        var latestSeeds = latestSignalDate.HasValue
            ? seeds.Where(item => item.SignalDate == latestSignalDate.Value).OrderBy(item => item.ModelRank).ToArray()
            : seeds.OrderBy(item => item.ModelRank).ToArray();
        var displays = latestSeeds
            .Select(item =>
            {
                opportunitiesBySymbol.TryGetValue(NormalizeSymbolKey(item.Code), out var opportunity);
                var actionText = TranslateQlibAction(item.Action);
                return new QlibCandidateDisplay(
                    item.Code,
                    item.Symbol,
                    item.Name,
                    item.SignalDate,
                    item.ModelRank,
                    $"Top {item.ModelRank}",
                    item.ModelScore100,
                    item.ModelScore100.ToString("F2"),
                    actionText,
                    GetQlibActionBrush(actionText),
                    $"{item.TargetWeight:P2}",
                    opportunity is null ? "未触发" : TranslateOpportunityStatus(opportunity.Status),
                    opportunity is null ? "--" : opportunity.CurrentScore.ToString("F2"),
                    string.IsNullOrWhiteSpace(item.Risk) ? "--" : item.Risk!,
                    item.SourceExperimentId,
                    item.Reason,
                    opportunity?.StrategySummary ?? "--",
                    opportunity?.StrategyExplanation ?? "--",
                    opportunity?.LastSeenTime,
                    item.ImportedAt);
            })
            .ToArray();

        var confirmedCount = displays.Count(item => item.RealtimeStateText is "候选" or "重点" or "加强" or "新命中");
        QlibSignalDateText.Text = latestSignalDate.HasValue ? latestSignalDate.Value.ToString("yyyy-MM-dd") : "--";
        QlibSeedCountText.Text = displays.Length.ToString();
        QlibConfirmedCountText.Text = confirmedCount.ToString();
        QlibImportedAtText.Text = displays.Length == 0 ? "--" : displays.Max(item => item.ImportedAt).ToLocalTime().ToString("HH:mm:ss");
        QlibCandidateHintText.Text = status is null
            ? "无法读取 Qlib 状态，请确认后端已启动。"
            : status.FileExists
                ? $"共享文件存在，文件记录 {status.RecordCount} 条，最后写入 {FormatDateTime(status.LastWriteTime)}。"
                : $"共享文件缺失：{status.WatchlistPath}";
        QlibStrategySummaryText.Text = displays.Length == 0
            ? "尚未导入候选种子。点击“导入候选”后，会把 latest_watchlist.csv 写入 qlib_signal_seeds。"
            : $"候选 {displays.Length} 只 | 实时确认 {confirmedCount} 只 | 来源实验 {displays[0].SourceExperimentId}";
        QlibCandidateDataGrid.ItemsSource = displays;
        ApplyQlibRebalancePlan(rebalancePlan);
        if (displays.Length > 0 && QlibCandidateDataGrid.SelectedItem is null)
        {
            QlibCandidateDataGrid.SelectedIndex = 0;
        }
        else if (displays.Length == 0)
        {
            ApplyQlibCandidateDetail(null);
        }
    }

    private void ApplyQlibRebalancePlan(QlibSignalSnapshotDto? rebalancePlan)
    {
        var records = rebalancePlan?.Records ?? Array.Empty<QlibSignalRecordDto>();
        var displays = records
            .OrderBy(item => QlibActionOrder(item.Action))
            .ThenBy(item => item.ModelRank)
            .Select(item =>
            {
                var actionText = TranslateQlibAction(item.Action);
                return new QlibRebalanceDisplay(
                    item.Code,
                    item.Symbol,
                    item.Name,
                    item.SignalDate,
                    item.ModelRank,
                    item.ModelRank > 0 ? $"Top {item.ModelRank}" : "--",
                    item.ModelScore100.ToString("F2"),
                    actionText,
                    GetQlibActionBrush(actionText),
                    $"{item.TargetWeight:P2}",
                    string.IsNullOrWhiteSpace(item.Risk) ? "--" : item.Risk!,
                    item.Reason);
            })
            .ToArray();

        QlibRebalancePlanDataGrid.ItemsSource = displays;
        var signalDateText = rebalancePlan is null ? "--" : rebalancePlan.SignalDate.ToString("yyyy-MM-dd");
        if (displays.Length == 0)
        {
            QlibRebalanceSummaryText.Text = $"信号日 {signalDateText} | 无调仓记录";
            return;
        }

        var buyCount = displays.Count(item => item.ActionText == "买入");
        var sellCount = displays.Count(item => item.ActionText == "卖出");
        var holdCount = displays.Count(item => item.ActionText == "继续持有");
        QlibRebalanceSummaryText.Text = $"信号日 {signalDateText} | 买入 {buyCount} | 卖出 {sellCount} | 继续持有 {holdCount}";
    }

    private void ApplyQlibCandidateDetail(QlibCandidateDisplay? item)
    {
        if (item is null)
        {
            QlibDetailTitleText.Text = "未选择股票";
            QlibDetailStateText.Text = "从左侧选择一只候选股。";
            QlibDetailSignalText.Text = "--";
            QlibDetailRealtimeText.Text = "--";
            QlibDetailReasonText.Text = "--";
            return;
        }

        QlibDetailTitleText.Text = $"{item.Code} {item.Name}";
        QlibDetailStateText.Text = $"实时状态：{item.RealtimeStateText} | 机会分：{item.OpportunityScoreText}";
        QlibDetailSignalText.Text = $"信号日 {item.SignalDate:yyyy-MM-dd} | 排名 Top {item.ModelRank} | 模型分 {item.ScoreText} | 目标权重 {item.WeightText} | 来源实验 {item.SourceExperimentId}";
        QlibDetailRealtimeText.Text = item.LastSeenTime.HasValue
            ? $"最近实时命中：{FormatDateTime(item.LastSeenTime)} | {item.OpportunitySummary}`n{item.OpportunityExplanation}"
            : "当前机会池尚未触发。需要等待盘中成交额、涨跌幅、量比等条件确认。";
        QlibDetailReasonText.Text = $"Qlib 理由：{item.Reason}`n风险：{item.RiskText}";
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
                item.RiskNote))
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

    private static bool IsQlibNextDayPrediction(PredictionRecordDto item)
    {
        return item.PredictionReason.Contains("Qlib 明日预测", StringComparison.OrdinalIgnoreCase)
            || item.StrategyCodes.Contains("qlib-next-day-direction", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyWorkspaceVisibility()
    {
        _showMarketSentiment = false;
        var isDailyHitResearchView = _showResearchPage && _showStockPools;
        var isQlibResearchView = _showResearchPage && _showQlibStrategy;
        var showDailyHitKLine = isDailyHitResearchView && _dailyHitCount > 0;
        var isFullWorkspaceView = _showResearchPage && !isDailyHitResearchView && !isQlibResearchView;
        WorkspaceContentGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        WorkspaceContentGrid.RowDefinitions[1].Height = new GridLength(0);
        Grid.SetRow(WorkspaceListHost, 0);
        Grid.SetColumn(WorkspaceListHost, 0);
        Grid.SetRow(KLinePanel, 0);
        Grid.SetColumn(KLinePanel, 2);
        SignalEventListBox.Visibility = Visibility.Collapsed;
        SectorHeatPanel.Visibility = Visibility.Collapsed;
        ConceptHeatPanel.Visibility = Visibility.Collapsed;
        MarketSentimentPanel.Visibility = Visibility.Collapsed;
        HistoryStatsPanel.Visibility = _showHistory ? Visibility.Visible : Visibility.Collapsed;
        PredictionReviewPanel.Visibility = _showPredictionReview ? Visibility.Visible : Visibility.Collapsed;
        StrategyCenterPanel.Visibility = _showStrategyCenter ? Visibility.Visible : Visibility.Collapsed;
        StrategyTrainingPanel.Visibility = _showStrategyTraining ? Visibility.Visible : Visibility.Collapsed;
        BacktestPanel.Visibility = _showBacktest ? Visibility.Visible : Visibility.Collapsed;
        StockPoolsPanel.Visibility = _showStockPools ? Visibility.Visible : Visibility.Collapsed;
        QlibStrategyPanel.Visibility = isQlibResearchView ? Visibility.Visible : Visibility.Collapsed;

        OpportunityPanel.Visibility = _showResearchPage ? Visibility.Collapsed : Visibility.Visible;
        SnapshotPanel.Visibility = _showResearchPage ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceListHost.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        KLinePanel.Visibility = isFullWorkspaceView || isQlibResearchView || (isDailyHitResearchView && !showDailyHitKLine)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OpportunityColumn.Width = _showResearchPage ? new GridLength(0) : new GridLength(300);
        SnapshotColumn.Width = _showResearchPage ? new GridLength(0) : new GridLength(320);
        WorkspaceListColumn.Width = _showResearchPage
            ? showDailyHitKLine ? new GridLength(300) : new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        WorkspaceKLineGapColumn.Width = _showResearchPage
            ? showDailyHitKLine ? new GridLength(12) : new GridLength(0)
            : new GridLength(0);
        WorkspaceKLineColumn.Width = _showResearchPage
            ? showDailyHitKLine ? new GridLength(1, GridUnitType.Star) : new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        WorkspacePanel.Margin = _showResearchPage ? new Thickness(0) : new Thickness(14, 0, 14, 0);
        WorkspaceTitleText.Text = _showResearchPage ? "研究复盘" : "实时工作台";
        WorkspaceCaptionText.Text = _showResearchPage
            ? "历史统计、次日预测和策略中心已集中到研究页。"
            : "机会池与行情快照保持在左侧，K 线区域独立显示。";
        SignalStreamViewButton.Visibility = Visibility.Collapsed;
        SectorHeatViewButton.Visibility = Visibility.Collapsed;
        ConceptHeatViewButton.Visibility = Visibility.Collapsed;
        MarketSentimentViewButton.Visibility = Visibility.Collapsed;
        HistoryStatsViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        PredictionReviewViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        StrategyCenterViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        StrategyTrainingViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
        BacktestViewButton.Visibility = Visibility.Collapsed;
        QlibStrategyViewButton.Visibility = _showResearchPage ? Visibility.Visible : Visibility.Collapsed;
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
        StrategyCenterViewButton.Style = _showStrategyCenter
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        StrategyTrainingViewButton.Style = _showStrategyTraining
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        BacktestViewButton.Style = _showBacktest
            ? (Style)FindResource("PrimaryButtonStyle")
            : (Style)FindResource("PageTabButtonStyle");
        QlibStrategyViewButton.Style = _showQlibStrategy
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
            OpportunityFilterText.Text = "仅显示系统当前命中的股票；人工判断在右侧个股快照处理。";
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
                item.Volume))
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

    private async Task RunUiActionAsync(Func<CancellationToken, Task> action, TimeSpan? timeoutOverride = null)
    {
        SetActionsEnabled(false);
        try
        {
            using var timeout = new CancellationTokenSource(timeoutOverride ?? TimeSpan.FromSeconds(150));
            await action(timeout.Token);
        }
        catch (TaskCanceledException)
        {
            FooterText.Text = "操作仍在等待后端响应，已超过前端等待时间。请稍后刷新查看结果。";
        }
        catch (Exception ex)
        {
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
        StartButton.IsEnabled = enabled;
        PauseButton.IsEnabled = enabled;
        ScanOnceButton.IsEnabled = enabled;
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
        StrategyCenterViewButton.IsEnabled = enabled;
        StrategyTrainingViewButton.IsEnabled = enabled;
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
        BuildStrategyTrainingDatasetButton.IsEnabled = enabled;
        RunStrategyTrainingButton.IsEnabled = enabled;
        StrategyTrainingStartDatePicker.IsEnabled = enabled;
        StrategyTrainingEndDatePicker.IsEnabled = enabled;
        StrategyTrainingStrategyComboBox.IsEnabled = enabled;
        StrategyTrainingHighReturnTextBox.IsEnabled = enabled;
        StrategyTrainingPositiveCloseCheckBox.IsEnabled = enabled;
        StrategyTrainingForceRebuildCheckBox.IsEnabled = enabled;
        StrategyTrainingScoreThresholdsTextBox.IsEnabled = enabled;
        StrategyTrainingAmountThresholdsTextBox.IsEnabled = enabled;
        StrategyTrainingRelativeStrengthThresholdsTextBox.IsEnabled = enabled;
        StrategyTrainingHeatThresholdsTextBox.IsEnabled = enabled;
        StrategyTrainingOutputLimitsTextBox.IsEnabled = enabled;
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

    private static string TranslateQlibAction(string value)
    {
        return value switch
        {
            "Confirm" => "确认",
            "Candidate" => "候选",
            "Watch" => "观察",
            "Buy" => "买入",
            "Sell" => "卖出",
            "Hold" => "继续持有",
            "Target" => "目标持仓",
            _ => value
        };
    }

    private static int QlibActionOrder(string value)
    {
        return TranslateQlibAction(value) switch
        {
            "卖出" => 0,
            "买入" => 1,
            "继续持有" => 2,
            "目标持仓" => 3,
            "确认" => 4,
            "候选" => 5,
            "观察" => 6,
            "弱市空仓" => 7,
            _ => 9
        };
    }

    private static Brush GetQlibActionBrush(string value)
    {
        return value switch
        {
            "买入" => Brushes.Firebrick,
            "卖出" => Brushes.ForestGreen,
            "继续持有" => Brushes.SteelBlue,
            "目标持仓" => Brushes.SteelBlue,
            "弱市空仓" => Brushes.Gray,
            "确认" => Brushes.Firebrick,
            "候选" => Brushes.DarkOrange,
            "观察" => Brushes.DimGray,
            "等回踩" => Brushes.SteelBlue,
            "排除" => Brushes.Gray,
            _ => Brushes.DimGray
        };
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
        string RiskText);

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

    private sealed record StrategyTrainingResultDisplay(
        int Rank,
        string HitText,
        string SuccessRateText,
        Brush SuccessRateBrush,
        string ParameterText,
        string ReturnText,
        string Summary,
        bool CanApply = false,
        string? StrategyCode = null,
        Guid? RunId = null,
        decimal MinScore = 0m,
        decimal MinAmountYi = 0m,
        decimal MinRelativeStrengthPercent = 0m,
        decimal MinHeatScore = 0m,
        int MaxOutputPerDay = 0,
        int SampleCount = 0,
        decimal? SuccessRate = null,
        decimal? AverageNextHighReturn = null,
        decimal? AverageNextCloseReturn = null);

    private sealed record StrategyTrainingOptionDisplay(
        string? Code,
        string Label);

    private sealed record StrategyTrainingSampleDisplay(
        string SignalDate,
        string Symbol,
        string Name,
        string StrategyText,
        string ReturnText,
        string FactorText,
        string ResultText,
        Brush ResultBrush);

    private sealed record QlibCandidateDisplay(
        string Code,
        string Symbol,
        string Name,
        DateOnly SignalDate,
        int ModelRank,
        string RankText,
        decimal ModelScore,
        string ScoreText,
        string ActionText,
        Brush ActionBrush,
        string WeightText,
        string RealtimeStateText,
        string OpportunityScoreText,
        string RiskText,
        string SourceExperimentId,
        string Reason,
        string OpportunitySummary,
        string OpportunityExplanation,
        DateTimeOffset? LastSeenTime,
        DateTimeOffset ImportedAt);

    private sealed record QlibRebalanceDisplay(
        string Code,
        string Symbol,
        string Name,
        DateOnly SignalDate,
        int ModelRank,
        string RankText,
        string ScoreText,
        string ActionText,
        Brush ActionBrush,
        string WeightText,
        string RiskText,
        string Reason);

    private sealed record DailyHitDisplay(
        string Symbol,
        string Name,
        DateTimeOffset EventTime,
        string StrategyName,
        decimal Score,
        decimal? Price,
        string Reason,
        string? Risk,
        string ScoreText,
        string StrategyText,
        string SignalText,
        string ReviewText);

    private sealed record StrategyPerformanceDisplay(
        string StrategyName,
        int HitCount,
        decimal AverageScore,
        decimal MaxScore,
        string LastHitTime,
        string SummaryText);
}



