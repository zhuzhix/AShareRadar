using System.Windows;
using System.Windows.Controls;
using AShareRadar.Contracts.Opportunities;
using AShareRadar.Desktop.Controls;
using AShareRadar.Desktop.Services;

namespace AShareRadar.Desktop;

public partial class KLineFloatingWindow : Window
{
    private readonly RadarApiClient _apiClient;
    private string? _symbol;
    private string? _name;
    private SignalEventDto? _latestEvent;
    private string _period = "day";
    private string _indicatorMode = "MACD";
    private bool _showTrainingMarkers;

    public KLineFloatingWindow(RadarApiClient apiClient)
    {
        InitializeComponent();
        _apiClient = apiClient;
        KLineChart.PeriodName = TranslateKLinePeriod(_period);
        KLineChart.IndicatorMode = _indicatorMode;
        ApplyButtonStyles();
    }

    public async Task LoadSymbolAsync(
        string symbol,
        string? name,
        SignalEventDto? latestEvent,
        CancellationToken cancellationToken)
    {
        _symbol = symbol;
        _name = name;
        _latestEvent = latestEvent;
        SymbolTitleText.Text = string.IsNullOrWhiteSpace(name) ? symbol : $"{symbol} {name}";
        KLineChart.SymbolName = SymbolTitleText.Text;
        KLineChart.TradeMarkers = BuildTradeMarkers(latestEvent);
        IntradayChart.SymbolName = SymbolTitleText.Text;
        IntradayChart.TradeMarkers = BuildTradeMarkers(latestEvent);
        await RefreshKLineAsync(cancellationToken);
    }

    private async Task SetPeriodAsync(string period)
    {
        _period = period;
        if (IsIntradayPeriod(period))
        {
            _indicatorMode = "MACD";
        }
        KLineChart.PeriodName = TranslateKLinePeriod(period);
        ApplyChartMode();
        ApplyButtonStyles();
        await RunWindowActionAsync(RefreshKLineAsync);
    }

    private async Task SetIndicatorModeAsync(string mode)
    {
        if (IsIntradayPeriod(_period) && mode != "MACD")
        {
            return;
        }

        _indicatorMode = mode;
        KLineChart.IndicatorMode = mode;
        ApplyButtonStyles();
        await RunWindowActionAsync(RefreshIndicatorAsync);
    }

    private async Task RefreshKLineAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_symbol))
        {
            KLineChart.SymbolName = "未选择";
            KLineChart.Candles = [];
            KLineChart.IndicatorSeries = null;
            KLineChart.TradeMarkers = [];
            IntradayChart.SymbolName = "未选择";
            IntradayChart.Candles = [];
            IntradayChart.IndicatorSeries = null;
            IntradayChart.TradeMarkers = [];
            StatusText.Text = "未选择股票";
            return;
        }

        StatusText.Text = $"正在加载 {_symbol} {TranslateKLinePeriod(_period)}...";
        var count = GetKLineCount(_period);
        var barsTask = _apiClient.GetKLineAsync(_symbol, _period, count, cancellationToken);
        var dailyTask = IsIntradayPeriod(_period)
            ? _apiClient.GetKLineAsync(_symbol, "day", 10, cancellationToken)
            : Task.FromResult<IReadOnlyList<AShareRadar.Contracts.MarketData.KLineBarDto>>([]);
        await Task.WhenAll(barsTask, dailyTask);
        var bars = await barsTask;
        var candles = bars
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
        KLineChart.SymbolName = string.IsNullOrWhiteSpace(_name) ? _symbol : $"{_symbol} {_name}";
        KLineChart.PeriodName = TranslateKLinePeriod(_period);
        KLineChart.TradeMarkers = BuildTradeMarkers(_latestEvent);
        KLineChart.Candles = candles;
        IntradayChart.SymbolName = KLineChart.SymbolName;
        IntradayChart.IsFiveDay = _period == "five-day";
        IntradayChart.TradeMarkers = KLineChart.TradeMarkers;
        IntradayChart.Candles = candles;
        IntradayChart.PreviousClose = ResolvePreviousClose(candles, await dailyTask);
        ApplyChartMode();
        await RefreshIndicatorAsync(cancellationToken);
        CaptionText.Text = $"周期：{TranslateKLinePeriod(_period)}   副图：{_indicatorMode}";
        StatusText.Text = $"已加载 {bars.Count} 根K线";
    }

    private async Task RefreshIndicatorAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_symbol))
        {
            KLineChart.IndicatorSeries = null;
            return;
        }

        try
        {
            var series = await _apiClient.GetIndicatorsAsync(
                _symbol,
                _period,
                _indicatorMode,
                GetKLineCount(_period),
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
            IntradayChart.IndicatorSeries = KLineChart.IndicatorSeries;
        }
        catch
        {
            KLineChart.IndicatorSeries = null;
            IntradayChart.IndicatorSeries = null;
        }
    }

    private async Task RunWindowActionAsync(Func<CancellationToken, Task> action)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "加载已取消或超时";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载失败：{ex.Message}";
        }
    }

    private void ApplyButtonStyles()
    {
        var selectedStyle = (Style)FindResource("ChartToolbarSelectedButtonStyle");
        var normalStyle = (Style)FindResource("ChartToolbarButtonStyle");
        SetSelectionStyle(MinutePeriodButton, _period == "minute", selectedStyle, normalStyle);
        SetSelectionStyle(FiveDayPeriodButton, _period == "five-day", selectedStyle, normalStyle);
        SetSelectionStyle(M1PeriodButton, _period == "m1", selectedStyle, normalStyle);
        SetSelectionStyle(M5PeriodButton, _period == "m5", selectedStyle, normalStyle);
        SetSelectionStyle(M15PeriodButton, _period == "m15", selectedStyle, normalStyle);
        SetSelectionStyle(M30PeriodButton, _period == "m30", selectedStyle, normalStyle);
        SetSelectionStyle(M60PeriodButton, _period == "m60", selectedStyle, normalStyle);
        SetSelectionStyle(DayPeriodButton, _period == "day", selectedStyle, normalStyle);
        SetSelectionStyle(WeekPeriodButton, _period == "week", selectedStyle, normalStyle);
        SetSelectionStyle(MonthPeriodButton, _period == "month", selectedStyle, normalStyle);
        SetSelectionStyle(MacdIndicatorButton, _indicatorMode == "MACD", selectedStyle, normalStyle);
        SetSelectionStyle(KdjIndicatorButton, _indicatorMode == "KDJ", selectedStyle, normalStyle);
        SetSelectionStyle(RsiIndicatorButton, _indicatorMode == "RSI", selectedStyle, normalStyle);
        SetSelectionStyle(TrainingMarkerButton, _showTrainingMarkers && IsTrainingMarkerPeriod(_period), selectedStyle, normalStyle);
        TrainingMarkerButton.IsEnabled = IsTrainingMarkerPeriod(_period);
        ApplyChartMode();
    }

    private static void SetSelectionStyle(Button button, bool selected, Style selectedStyle, Style normalStyle)
    {
        button.Style = selected ? selectedStyle : normalStyle;
    }

    private async void MinutePeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("minute");

    private async void FiveDayPeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("five-day");

    private async void M1PeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("m1");

    private async void M5PeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("m5");

    private async void M15PeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("m15");

    private async void M30PeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("m30");

    private async void M60PeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("m60");

    private async void DayPeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("day");

    private async void WeekPeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("week");

    private async void MonthPeriodButton_Click(object sender, RoutedEventArgs e) => await SetPeriodAsync("month");

    private async void MacdIndicatorButton_Click(object sender, RoutedEventArgs e) => await SetIndicatorModeAsync("MACD");

    private async void KdjIndicatorButton_Click(object sender, RoutedEventArgs e) => await SetIndicatorModeAsync("KDJ");

    private async void RsiIndicatorButton_Click(object sender, RoutedEventArgs e) => await SetIndicatorModeAsync("RSI");

    private void TrainingMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsTrainingMarkerPeriod(_period))
        {
            return;
        }

        _showTrainingMarkers = !_showTrainingMarkers;
        ApplyChartMode();
        ApplyButtonStyles();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RunWindowActionAsync(RefreshKLineAsync);

    private static int GetKLineCount(string period)
    {
        return period switch
        {
            "minute" => 240,
            "five-day" => 1200,
            "m1" => 240,
            "m5" => 240,
            "m15" => 180,
            "m30" => 160,
            "m60" => 120,
            "week" => 180,
            "month" => 120,
            _ => 120
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

    private void ApplyChartMode()
    {
        var intraday = IsIntradayPeriod(_period);
        IntradayChart.Visibility = intraday ? Visibility.Visible : Visibility.Collapsed;
        KLineChart.Visibility = intraday ? Visibility.Collapsed : Visibility.Visible;
        KdjIndicatorButton.Visibility = intraday ? Visibility.Collapsed : Visibility.Visible;
        RsiIndicatorButton.Visibility = intraday ? Visibility.Collapsed : Visibility.Visible;
        MacdIndicatorButton.IsEnabled = !intraday;
        var showTrainingMarkers = _showTrainingMarkers && IsTrainingMarkerPeriod(_period);
        KLineChart.ShowTrainingMarkers = showTrainingMarkers;
        IntradayChart.ShowTrainingMarkers = showTrainingMarkers;
    }

    private static bool IsIntradayPeriod(string period) => period is "minute" or "five-day";

    private static bool IsTrainingMarkerPeriod(string period) => period is not ("week" or "month");

    private static decimal ResolvePreviousClose(
        IReadOnlyList<KLineCandle> intradayBars,
        IReadOnlyList<AShareRadar.Contracts.MarketData.KLineBarDto> dailyBars)
    {
        if (intradayBars.Count == 0)
        {
            return 0m;
        }

        var referenceDate = intradayBars.Max(item => item.TradingTime.Date);
        var previous = dailyBars
            .Where(item => item.TradingTime.Date < referenceDate)
            .OrderBy(item => item.TradingTime)
            .LastOrDefault();
        if (previous?.Close > 0)
        {
            return previous.Close;
        }

        var firstBarOfReferenceDate = intradayBars
            .Where(item => item.TradingTime.Date == referenceDate)
            .OrderBy(item => item.TradingTime)
            .FirstOrDefault();
        return firstBarOfReferenceDate?.Open > 0 ? firstBarOfReferenceDate.Open : intradayBars[0].Open;
    }

    private static IReadOnlyList<KLineTradeMarker> BuildTradeMarkers(SignalEventDto? latestEvent)
    {
        if (latestEvent?.Price is not { } price || price <= 0)
        {
            return [];
        }

        var markers = new List<KLineTradeMarker>
        {
            new(latestEvent.EventTime.LocalDateTime, price, "Buy", "信号")
        };

        var bestHit = latestEvent.StrategyHits
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        if (bestHit?.StopLossPrice is { } stopLoss && stopLoss > 0)
        {
            markers.Add(new KLineTradeMarker(latestEvent.EventTime.LocalDateTime, stopLoss, "StopLoss", "止损"));
        }

        if (bestHit?.TakeProfitPrice is { } takeProfit && takeProfit > 0)
        {
            markers.Add(new KLineTradeMarker(latestEvent.EventTime.LocalDateTime, takeProfit, "TakeProfit", "止盈"));
        }

        return markers;
    }
}
