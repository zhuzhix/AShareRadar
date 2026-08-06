using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AShareRadar.Contracts.MarketData;
using AShareRadar.Desktop.Services;

namespace AShareRadar.Desktop;

public partial class MarketSentimentDetailWindow : Window
{
    private readonly RadarApiClient _apiClient;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<MarketSentimentSnapshotDto> _history = [];
    private IReadOnlyList<ChartPointDisplay> _chartPoints = [];

    public MarketSentimentDetailWindow(RadarApiClient apiClient)
    {
        _apiClient = apiClient;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync(refreshSnapshot: false);
        Closed += (_, _) => _lifetime.Cancel();
        SentimentChartCanvas.MouseMove += SentimentChartCanvas_MouseMove;
        SentimentChartCanvas.MouseLeave += (_, _) => SentimentChartCanvas.ToolTip = null;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(refreshSnapshot: true);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task LoadAsync(bool refreshSnapshot)
    {
        try
        {
            HeaderStatusText.Text = refreshSnapshot ? "正在实时刷新情绪快照..." : "正在读取情绪详情...";
            var token = _lifetime.Token;
            var snapshotTask = refreshSnapshot
                ? _apiClient.RefreshMarketSentimentAsync(token)
                : _apiClient.GetMarketSentimentAsync(token);
            var statusTask = _apiClient.GetMarketSentimentStatusAsync(token);
            var historyTask = _apiClient.GetMarketSentimentHistoryAsync(DateOnly.FromDateTime(DateTime.Today), 240, token);
            var dataSourcesTask = _apiClient.GetMarketSentimentDataSourcesAsync(token);

            await Task.WhenAll(snapshotTask, statusTask, historyTask, dataSourcesTask);

            var snapshot = snapshotTask.Result;
            _history = historyTask.Result
                .OrderBy(item => item.SnapshotTime)
                .ToArray();

            ApplySnapshot(snapshot);
            ApplyStatus(statusTask.Result);
            ApplyDataSources(dataSourcesTask.Result);
            ApplyHistory(_history);
            DrawChart();
            HeaderStatusText.Text = $"已刷新：{DateTime.Now:HH:mm:ss}";
        }
        catch (TaskCanceledException)
        {
            HeaderStatusText.Text = "读取已取消。";
        }
        catch (Exception ex)
        {
            HeaderStatusText.Text = $"读取失败：{ex.Message}";
        }
    }

    private void ApplySnapshot(MarketSentimentSnapshotDto? snapshot)
    {
        if (snapshot is null)
        {
            ScoreText.Text = "--";
            ScoreText.Foreground = Brushes.Black;
            LevelText.Text = "等待";
            LevelText.Foreground = Brushes.Gray;
            LevelBadge.Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            TemperatureFill.Width = 0;
            SummaryText.Text = "接口未返回情绪快照。";
            DataQualityText.Text = "--";
            SnapshotTimeText.Text = "--";
            LevelGuideTitleText.Text = "等级提示：等待数据";
            LevelGuideText.Text = "情绪刷新后显示策略环境和风险提示。";
            StrategyEnvironmentText.Text = "策略环境：--";
            WarningsItemsControl.ItemsSource = new[] { "暂无情绪数据。" };
            CategoriesItemsControl.ItemsSource = Array.Empty<CategoryDisplay>();
            MetricsGroupsItemsControl.ItemsSource = Array.Empty<MetricGroupDisplay>();
            DataSourceStatusItemsControl.ItemsSource = Array.Empty<DataSourceStatusDisplay>();
            return;
        }

        var temperatureScore = (double)snapshot.TemperatureScore;
        var accentBrush = GetSentimentBrush(temperatureScore);
        var lightBrush = GetSentimentLightBrush(temperatureScore);

        ScoreText.Text = snapshot.TemperatureScore.ToString("F0");
        ScoreText.Foreground = accentBrush;
        LevelText.Text = snapshot.Level;
        LevelText.Foreground = accentBrush;
        LevelBadge.Background = lightBrush;
        TemperatureFill.Width = ScaleScoreWidth(temperatureScore, 298);
        TemperatureFill.Background = accentBrush;
        LevelGuideCard.Background = lightBrush;
        LevelGuideCard.BorderBrush = accentBrush;
        LevelGuideTitleText.Text = $"等级提示：{snapshot.Level} / {GetSentimentScoreLabel(temperatureScore)}";
        LevelGuideTitleText.Foreground = accentBrush;
        LevelGuideText.Text = BuildLevelGuide(snapshot.Level);
        StrategyEnvironmentText.Text = BuildStrategyEnvironment(snapshot.Level);
        SummaryText.Text = snapshot.Summary;
        DataQualityText.Text = $"{snapshot.ProviderName} / {snapshot.DataQuality}";
        SnapshotTimeText.Text = snapshot.SnapshotTime.ToLocalTime().ToString("MM-dd HH:mm:ss");
        WarningsItemsControl.ItemsSource = snapshot.Warnings.Count == 0
            ? new[] { "暂无数据质量提示。" }
            : snapshot.Warnings;

        CategoriesItemsControl.ItemsSource = snapshot.Categories
            .Select(item =>
            {
                var score = (double)item.Score;
                return new CategoryDisplay(
                    item.Name,
                    item.Score.ToString("F0"),
                    GetSentimentScoreLabel(score),
                    item.Status,
                    item.Description,
                    ScaleScoreWidth(score, 252),
                    GetSentimentBrush(score),
                    GetSentimentLightBrush(score),
                    GetSentimentBadgeBrush(score));
            })
            .ToArray();

        MetricsGroupsItemsControl.ItemsSource = BuildMetricGroups(snapshot);
    }

    private void ApplyStatus(MarketSentimentStatusDto? status)
    {
        if (status is null)
        {
            WorkerStatusText.Text = "--";
            NextRunText.Text = "--";
            return;
        }

        WorkerStatusText.Text = status.IsEnabled
            ? status.IsRunning ? "运行中" : status.LastStatus
            : "未启用";
        NextRunText.Text = status.NextRunAt.HasValue
            ? status.NextRunAt.Value.ToLocalTime().ToString("HH:mm:ss")
            : "--";
    }

    private void ApplyDataSources(IReadOnlyList<MarketSentimentDataSourceStatusDto> statuses)
    {
        DataSourceStatusItemsControl.ItemsSource = statuses
            .Select(item => new DataSourceStatusDisplay(
                item.Code,
                TranslateSourceStatus(item.Status),
                GetSourceStatusBrush(item.Status)))
            .ToArray();
    }

    private void ApplyHistory(IReadOnlyList<MarketSentimentSnapshotDto> history)
    {
        ChartCaptionText.Text = history.Count == 0
            ? "今日暂无历史快照"
            : $"今日 {history.Count} 次快照 | {history[0].SnapshotTime.ToLocalTime():HH:mm} - {history[^1].SnapshotTime.ToLocalTime():HH:mm}";
        RecentSnapshotsListBox.ItemsSource = history
            .OrderByDescending(item => item.SnapshotTime)
            .Take(5)
            .Select(item =>
            {
                var score = (double)item.TemperatureScore;
                return new RecentSnapshotDisplay(
                    item.SnapshotTime.ToLocalTime().ToString("HH:mm:ss"),
                    item.TemperatureScore.ToString("F0"),
                    $"{item.Level} / {GetSentimentScoreLabel(score)}",
                    item.Summary,
                    GetSentimentBrush(score),
                    item);
            })
            .ToArray();
    }

    private void RecentSnapshotsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentSnapshotsListBox.SelectedItem is not RecentSnapshotDisplay display)
        {
            return;
        }

        ApplySnapshot(display.Snapshot);
        HeaderStatusText.Text = $"已回放快照：{display.Snapshot.SnapshotTime.ToLocalTime():HH:mm:ss}";
    }

    private void SentimentChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart();
    }

    private void DrawChart()
    {
        SentimentChartCanvas.Children.Clear();
        _chartPoints = [];

        var width = SentimentChartCanvas.ActualWidth;
        var height = SentimentChartCanvas.ActualHeight;
        if (width < 80 || height < 80)
        {
            return;
        }

        const double left = 42;
        const double right = 18;
        const double top = 18;
        const double bottom = 28;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);

        DrawLevelLine(75, "过热");
        DrawLevelLine(60, "偏热");
        DrawLevelLine(45, "中性");
        DrawLevelLine(30, "偏冷");

        if (_history.Count == 0)
        {
            AddText("暂无今日情绪曲线", left + plotWidth / 2 - 58, top + plotHeight / 2 - 10, Brushes.Gray, 13);
            return;
        }

        var ordered = _history.OrderBy(item => item.SnapshotTime).ToArray();
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0, 122, 255)),
            StrokeThickness = 2.5
        };

        for (var i = 0; i < ordered.Length; i++)
        {
            var x = ordered.Length == 1
                ? left + plotWidth / 2
                : left + i * plotWidth / (ordered.Length - 1);
            var y = top + (100 - (double)ordered[i].TemperatureScore) * plotHeight / 100;
            polyline.Points.Add(new Point(x, y));
        }
        _chartPoints = ordered
            .Select((item, index) => new ChartPointDisplay(polyline.Points[index], item))
            .ToArray();

        SentimentChartCanvas.Children.Add(polyline);

        var last = ordered[^1];
        var lastPoint = polyline.Points[^1];
        var lastBrush = GetSentimentBrush((double)last.TemperatureScore);
        SentimentChartCanvas.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = lastBrush,
            RenderTransform = new TranslateTransform(lastPoint.X - 4, lastPoint.Y - 4)
        });
        AddText($"{last.TemperatureScore:F0} {last.Level}", Math.Min(lastPoint.X + 8, width - 76), Math.Max(2, lastPoint.Y - 18), lastBrush, 12);

        AddText(ordered[0].SnapshotTime.ToLocalTime().ToString("HH:mm"), left, height - 22, Brushes.Gray, 11);
        AddText(ordered[^1].SnapshotTime.ToLocalTime().ToString("HH:mm"), width - 58, height - 22, Brushes.Gray, 11);

        void DrawLevelLine(double value, string label)
        {
            var y = top + (100 - value) * plotHeight / 100;
            SentimentChartCanvas.Children.Add(new Line
            {
                X1 = left,
                X2 = left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(210, 214, 220)),
                StrokeThickness = 1,
                StrokeDashArray = [4, 4]
            });
            AddText($"{value:F0} {label}", 4, y - 8, Brushes.Gray, 11);
        }
    }

    private void SentimentChartCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_chartPoints.Count == 0)
        {
            SentimentChartCanvas.ToolTip = null;
            return;
        }

        var position = e.GetPosition(SentimentChartCanvas);
        var nearest = _chartPoints
            .OrderBy(item => Math.Abs(item.Point.X - position.X))
            .ThenBy(item => Math.Abs(item.Point.Y - position.Y))
            .First();
        if (Math.Abs(nearest.Point.X - position.X) > 24)
        {
            SentimentChartCanvas.ToolTip = null;
            return;
        }

        var snapshot = nearest.Snapshot;
        SentimentChartCanvas.ToolTip = $"{snapshot.SnapshotTime.ToLocalTime():HH:mm:ss}  {snapshot.TemperatureScore:F1} / {snapshot.Level}\n{snapshot.Summary}";
    }

    private void AddText(string text, double x, double y, Brush brush, double fontSize)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = fontSize
        };
        Canvas.SetLeft(textBlock, x);
        Canvas.SetTop(textBlock, y);
        SentimentChartCanvas.Children.Add(textBlock);
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

    private static string BuildLevelGuide(string level)
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

    private static string BuildStrategyEnvironment(string level)
    {
        return level switch
        {
            "冰点" => "策略环境：只观察，不主动追高；进攻信号降权。",
            "偏冷" => "策略环境：降低信号分，优先防守和等待确认。",
            "中性" => "策略环境：正常运行，按策略质量排序。",
            "偏热" => "策略环境：主线共振、强趋势策略加权。",
            "过热" => "策略环境：高位信号降权，拥挤风险提示增强。",
            _ => "策略环境：等待更多数据确认。"
        };
    }

    private static IReadOnlyList<MetricGroupDisplay> BuildMetricGroups(MarketSentimentSnapshotDto snapshot)
    {
        var categoryNames = snapshot.Categories.ToDictionary(
            item => item.Code,
            item => item.Name,
            StringComparer.OrdinalIgnoreCase);

        return snapshot.Metrics
            .Select(item => new MetricDisplay(
                item.Name,
                item.IsAvailable ? item.DisplayValue : "暂无",
                item.CategoryCode,
                TranslateMetricSourceStatus(item.SourceStatus)))
            .GroupBy(item => item.CategoryCode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => GetMetricGroupOrder(group.Key))
            .Select(group =>
            {
                var metrics = group.ToArray();
                var name = categoryNames.TryGetValue(group.Key, out var categoryName)
                    ? categoryName
                    : TranslateMetricCategory(group.Key);
                return new MetricGroupDisplay(name, $"{metrics.Length} 项", metrics);
            })
            .ToArray();
    }

    private static int GetMetricGroupOrder(string categoryCode)
    {
        return categoryCode switch
        {
            "breadth" => 1,
            "short-term" => 2,
            "trading" => 3,
            "risk" => 4,
            "capital" => 5,
            "external" => 6,
            _ => 99
        };
    }

    private static string TranslateMetricCategory(string categoryCode)
    {
        return categoryCode switch
        {
            "breadth" => "市场广度",
            "short-term" => "短线情绪",
            "trading" => "交易热度",
            "risk" => "风险偏好",
            "capital" => "资金情绪",
            "external" => "外部压力",
            _ => categoryCode
        };
    }

    private static string TranslateMetricSourceStatus(string sourceStatus)
    {
        return sourceStatus switch
        {
            "Configured" => "来源：外部配置",
            "Realtime" => "来源：实时行情",
            "Estimated" => "来源：估算",
            "Unavailable" => "来源：暂未接入",
            "Disabled" => "来源：未启用",
            _ => $"来源：{sourceStatus}"
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
            "Disabled" => Brushes.Gray,
            _ => Brushes.Gray
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

    private static SolidColorBrush GetSentimentBadgeBrush(double score)
    {
        var color = score switch
        {
            >= 90 => Color.FromRgb(254, 226, 226),
            >= 75 => Color.FromRgb(255, 237, 213),
            >= 60 => Color.FromRgb(254, 243, 199),
            >= 45 => Color.FromRgb(219, 234, 254),
            >= 30 => Color.FromRgb(229, 237, 249),
            _ => Color.FromRgb(241, 245, 249)
        };

        return new SolidColorBrush(color);
    }

    private sealed record CategoryDisplay(
        string Name,
        string ScoreText,
        string LevelText,
        string Status,
        string Description,
        double BarWidth,
        Brush AccentBrush,
        Brush Background,
        Brush BadgeBrush);

    private sealed record MetricDisplay(string Name, string DisplayValue, string CategoryCode, string SourceStatusText);

    private sealed record MetricGroupDisplay(string Name, string CountText, IReadOnlyList<MetricDisplay> Metrics);

    private sealed record DataSourceStatusDisplay(string Code, string StatusText, Brush StatusBrush);

    private sealed record RecentSnapshotDisplay(
        string TimeText,
        string ScoreText,
        string Level,
        string Summary,
        Brush AccentBrush,
        MarketSentimentSnapshotDto Snapshot);

    private sealed record ChartPointDisplay(Point Point, MarketSentimentSnapshotDto Snapshot);
}
