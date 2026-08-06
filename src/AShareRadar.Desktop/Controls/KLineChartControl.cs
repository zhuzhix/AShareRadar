using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AShareRadar.Desktop.Controls;

public sealed class KLineChartControl : FrameworkElement
{
    public static readonly DependencyProperty CandlesProperty = DependencyProperty.Register(
        nameof(Candles),
        typeof(IReadOnlyList<KLineCandle>),
        typeof(KLineChartControl),
        new FrameworkPropertyMetadata(
            Array.Empty<KLineCandle>(),
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnCandlesChanged));

    public static readonly DependencyProperty SymbolNameProperty = DependencyProperty.Register(
        nameof(SymbolName),
        typeof(string),
        typeof(KLineChartControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeriodNameProperty = DependencyProperty.Register(
        nameof(PeriodName),
        typeof(string),
        typeof(KLineChartControl),
        new FrameworkPropertyMetadata("日线", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IndicatorModeProperty = DependencyProperty.Register(
        nameof(IndicatorMode),
        typeof(string),
        typeof(KLineChartControl),
        new FrameworkPropertyMetadata("MACD", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IndicatorSeriesProperty = DependencyProperty.Register(
        nameof(IndicatorSeries),
        typeof(KLineIndicatorSeries),
        typeof(KLineChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TradeMarkersProperty = DependencyProperty.Register(
        nameof(TradeMarkers),
        typeof(IReadOnlyList<KLineTradeMarker>),
        typeof(KLineChartControl),
        new FrameworkPropertyMetadata(Array.Empty<KLineTradeMarker>(), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(11, 15, 20));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(38, 44, 52));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(154, 164, 176));
    private static readonly Brush RisingBrush = new SolidColorBrush(Color.FromRgb(255, 69, 58));
    private static readonly Brush FallingBrush = new SolidColorBrush(Color.FromRgb(0, 199, 190));
    private static readonly Brush Ma5Brush = new SolidColorBrush(Color.FromRgb(210, 203, 0));
    private static readonly Brush Ma10Brush = new SolidColorBrush(Color.FromRgb(244, 0, 222));
    private static readonly Brush Ma20Brush = new SolidColorBrush(Color.FromRgb(0, 178, 52));
    private static readonly Brush Ma60Brush = new SolidColorBrush(Color.FromRgb(150, 150, 150));
    private static readonly Brush ChipYellowBrush = new SolidColorBrush(Color.FromRgb(255, 196, 0));
    private static readonly Brush ChipOrangeBrush = new SolidColorBrush(Color.FromRgb(255, 122, 0));
    private static readonly Brush ChipPinkBrush = new SolidColorBrush(Color.FromRgb(255, 24, 142));
    private static readonly Brush CrosshairBrush = new SolidColorBrush(Color.FromRgb(185, 194, 204));
    private static readonly Brush TooltipBackgroundBrush = new SolidColorBrush(Color.FromArgb(232, 12, 16, 20));
    private static readonly Brush TooltipBorderBrush = new SolidColorBrush(Color.FromRgb(76, 88, 102));
    private static readonly Brush BuyMarkerBrush = new SolidColorBrush(Color.FromRgb(52, 199, 89));
    private static readonly Brush SellMarkerBrush = new SolidColorBrush(Color.FromRgb(255, 149, 0));
    private static readonly Brush StopMarkerBrush = new SolidColorBrush(Color.FromRgb(255, 69, 58));
    private const double ChartLeft = 8d;
    private const double HeaderHeight = 54d;
    private const double ChipPanelWidth = 190d;
    private const double ChartRightGap = 58d;
    private const double ChipPanelGap = 46d;

    private Point? _mousePosition;
    private Point? _dragStartPoint;
    private int _dragStartRightOffset;
    private int _visibleCount = 90;
    private int _rightOffset;
    private bool _isDragging;

    public KLineChartControl()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    public IReadOnlyList<KLineCandle> Candles
    {
        get => (IReadOnlyList<KLineCandle>)GetValue(CandlesProperty);
        set => SetValue(CandlesProperty, value);
    }

    public string SymbolName
    {
        get => (string)GetValue(SymbolNameProperty);
        set => SetValue(SymbolNameProperty, value);
    }

    public string PeriodName
    {
        get => (string)GetValue(PeriodNameProperty);
        set => SetValue(PeriodNameProperty, value);
    }

    public string IndicatorMode
    {
        get => (string)GetValue(IndicatorModeProperty);
        set => SetValue(IndicatorModeProperty, value);
    }

    public KLineIndicatorSeries? IndicatorSeries
    {
        get => (KLineIndicatorSeries?)GetValue(IndicatorSeriesProperty);
        set => SetValue(IndicatorSeriesProperty, value);
    }

    public IReadOnlyList<KLineTradeMarker> TradeMarkers
    {
        get => (IReadOnlyList<KLineTradeMarker>)GetValue(TradeMarkersProperty);
        set => SetValue(TradeMarkersProperty, value);
    }

    private static void OnCandlesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not KLineChartControl control)
        {
            return;
        }

        control._rightOffset = 0;
        control._mousePosition = null;
        control._isDragging = false;
        control.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        if (width < 240 || height < 180)
        {
            DrawText(dc, "K线区域过小", 12, 12, 12, TextBrush);
            return;
        }

        var (chartRect, volumeRect, macdRect, chipRect) = CreateLayout(width, height);
        if (Candles.Count == 0)
        {
            DrawEmptyState(dc, chartRect, volumeRect, macdRect, chipRect);
            return;
        }

        var candles = GetVisibleCandles(Candles);
        var priceRange = GetPriceRange(candles);
        var chipCandles = GetHitCandleIndex(candles, chartRect) is { } hitIndex
            ? candles.Take(hitIndex + 1).ToArray()
            : candles;
        DrawHeader(dc, candles);
        DrawGrid(dc, chartRect, 6, 7);
        DrawGrid(dc, volumeRect, 2, 7);
        DrawGrid(dc, macdRect, 2, 7);
        DrawCandles(dc, candles, chartRect);
        DrawMovingAverage(dc, candles, chartRect, 5, Ma5Brush);
        DrawMovingAverage(dc, candles, chartRect, 10, Ma10Brush);
        DrawMovingAverage(dc, candles, chartRect, 20, Ma20Brush);
        DrawMovingAverage(dc, candles, chartRect, 60, Ma60Brush);
        DrawTradeMarkers(dc, candles, chartRect);
        DrawVolume(dc, candles, volumeRect);
        DrawIndicatorPanel(dc, candles, macdRect);
        DrawChipDistribution(dc, chipCandles, chipRect, priceRange);
        DrawAxisLabels(dc, candles, chartRect, volumeRect, macdRect);
        DrawCrosshair(dc, candles, chartRect, volumeRect, macdRect);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mousePosition = e.GetPosition(this);
        if (_isDragging && _dragStartPoint is { } dragStart)
        {
            var candles = Candles;
            if (candles.Count == 0)
            {
                return;
            }

            var chartWidth = CreateLayout(ActualWidth, ActualHeight).ChartRect.Width;
            var step = chartWidth / Math.Max(1, Math.Min(_visibleCount, candles.Count));
            var deltaBars = (int)Math.Round((dragStart.X - _mousePosition.Value.X) / Math.Max(1, step));
            SetRightOffset(_dragStartRightOffset + deltaBars, candles.Count);
        }

        InvalidateVisual();
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mousePosition = null;
        InvalidateVisual();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var candles = Candles;
        if (candles.Count == 0)
        {
            return;
        }

        var oldVisible = Math.Clamp(_visibleCount, 30, Math.Max(30, candles.Count));
        var newVisible = e.Delta > 0
            ? Math.Max(30, oldVisible - 12)
            : Math.Min(candles.Count, oldVisible + 12);
        _visibleCount = newVisible;
        SetRightOffset(_rightOffset, candles.Count);
        InvalidateVisual();
        e.Handled = true;
        base.OnMouseWheel(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        _isDragging = true;
        _dragStartPoint = e.GetPosition(this);
        _dragStartRightOffset = _rightOffset;
        e.Handled = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _isDragging = false;
        _dragStartPoint = null;
        ReleaseMouseCapture();
        e.Handled = true;
        base.OnMouseLeftButtonUp(e);
    }

    private static (Rect ChartRect, Rect VolumeRect, Rect MacdRect, Rect ChipRect) CreateLayout(double width, double height)
    {
        var chartWidth = Math.Max(80, width - ChipPanelWidth - ChartRightGap);
        var chartHeight = Math.Max(72, height * 0.62 - 48);
        var chartRect = new Rect(ChartLeft, HeaderHeight, chartWidth, chartHeight);
        var volumeRect = new Rect(ChartLeft, chartRect.Bottom + 8, chartRect.Width, Math.Max(42, height * 0.18));
        var macdRect = new Rect(ChartLeft, volumeRect.Bottom + 8, chartRect.Width, Math.Max(48, height - volumeRect.Bottom - 14));
        var chipRect = new Rect(chartRect.Right + ChipPanelGap, chartRect.Top, ChipPanelWidth, chartRect.Height);
        return (chartRect, volumeRect, macdRect, chipRect);
    }

    private static void DrawEmptyState(DrawingContext dc, Rect chartRect, Rect volumeRect, Rect macdRect, Rect chipRect)
    {
        DrawGrid(dc, chartRect, 6, 7);
        DrawGrid(dc, volumeRect, 2, 7);
        DrawGrid(dc, macdRect, 2, 7);
        dc.DrawRectangle(null, new Pen(GridBrush, 1), chipRect);
        DrawText(dc, "暂无真实 K 线数据", chartRect.Left + 18, chartRect.Top + 24, 14, TextBrush);
        DrawText(dc, "请检查分钟行情数据源，当前不会使用模拟价格代替。", chartRect.Left + 18, chartRect.Top + 52, 12, TextBrush);
    }

    private void DrawHeader(DrawingContext dc, IReadOnlyList<KLineCandle> candles)
    {
        var latest = candles[^1];
        DrawText(dc, $"{SymbolName}（{PeriodName}）", 8, 7, 12, ChipYellowBrush);
        DrawText(dc, $"最新:{latest.Close:F2}  显示:{candles.Count}根  偏移:{_rightOffset}", Math.Max(8, ActualWidth - 242), 7, 12, latest.Close >= latest.Open ? RisingBrush : FallingBrush);
        DrawText(dc, $"MA5:{Average(candles, 5):F2}   MA10:{Average(candles, 10):F2}   MA20:{Average(candles, 20):F2}   MA60:{Average(candles, 60):F2}", 8, 29, 12, TextBrush);
    }

    private static void DrawGrid(DrawingContext dc, Rect rect, int rows, int columns)
    {
        var pen = new Pen(GridBrush, 1);
        dc.DrawRectangle(null, pen, rect);

        for (var i = 1; i < rows; i++)
        {
            var y = rect.Top + rect.Height * i / rows;
            dc.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));
        }

        for (var i = 1; i < columns; i++)
        {
            var x = rect.Left + rect.Width * i / columns;
            dc.DrawLine(pen, new Point(x, rect.Top), new Point(x, rect.Bottom));
        }
    }

    private static void DrawCandles(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect)
    {
        var (minPrice, maxPrice) = GetPriceRange(candles);
        var step = rect.Width / candles.Count;
        var candleWidth = Math.Clamp(step * 0.54, 3, 10);

        for (var i = 0; i < candles.Count; i++)
        {
            var item = candles[i];
            var x = rect.Left + step * i + step / 2;
            var highY = MapY(item.High, minPrice, maxPrice, rect);
            var lowY = MapY(item.Low, minPrice, maxPrice, rect);
            var openY = MapY(item.Open, minPrice, maxPrice, rect);
            var closeY = MapY(item.Close, minPrice, maxPrice, rect);
            var rising = item.Close >= item.Open;
            var brush = rising ? RisingBrush : FallingBrush;
            var pen = new Pen(brush, 1);

            dc.DrawLine(pen, new Point(x, highY), new Point(x, lowY));

            var top = Math.Min(openY, closeY);
            var bodyHeight = Math.Max(2, Math.Abs(openY - closeY));
            var body = new Rect(x - candleWidth / 2, top, candleWidth, bodyHeight);
            dc.DrawRectangle(brush, pen, body);
        }
    }

    private static void DrawMovingAverage(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect, int window, Brush brush)
    {
        if (candles.Count < window)
        {
            return;
        }

        var (minPrice, maxPrice) = GetPriceRange(candles);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var started = false;
            for (var i = window - 1; i < candles.Count; i++)
            {
                var avg = candles.Skip(i - window + 1).Take(window).Average(item => (double)item.Close);
                var x = rect.Left + rect.Width / candles.Count * i + rect.Width / candles.Count / 2;
                var y = MapY((decimal)avg, minPrice, maxPrice, rect);
                if (!started)
                {
                    ctx.BeginFigure(new Point(x, y), false, false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(new Point(x, y), true, false);
                }
            }
        }

        dc.DrawGeometry(null, new Pen(brush, 1), geometry);
    }

    private void DrawTradeMarkers(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect)
    {
        if (TradeMarkers.Count == 0 || candles.Count == 0)
        {
            return;
        }

        var (minPrice, maxPrice) = GetPriceRange(candles);
        var step = rect.Width / candles.Count;
        foreach (var marker in TradeMarkers)
        {
            var index = FindNearestCandleIndex(candles, marker.TradingTime);
            if (index < 0)
            {
                continue;
            }

            var x = rect.Left + step * index + step / 2;
            var y = MapY(marker.Price, minPrice, maxPrice, rect);
            var brush = marker.MarkerType switch
            {
                "Buy" => BuyMarkerBrush,
                "TakeProfit" => SellMarkerBrush,
                "StopLoss" => StopMarkerBrush,
                _ => TextBrush
            };
            var pen = new Pen(brush, 1.2);

            if (marker.MarkerType is "TakeProfit" or "StopLoss")
            {
                dc.DrawLine(new Pen(brush, 1) { DashStyle = DashStyles.Dash }, new Point(rect.Left, y), new Point(rect.Right, y));
            }

            var triangle = new StreamGeometry();
            using (var ctx = triangle.Open())
            {
                if (marker.MarkerType == "Buy")
                {
                    ctx.BeginFigure(new Point(x, y - 9), true, true);
                    ctx.LineTo(new Point(x - 6, y + 4), true, false);
                    ctx.LineTo(new Point(x + 6, y + 4), true, false);
                }
                else
                {
                    ctx.BeginFigure(new Point(x, y + 9), true, true);
                    ctx.LineTo(new Point(x - 6, y - 4), true, false);
                    ctx.LineTo(new Point(x + 6, y - 4), true, false);
                }
            }

            dc.DrawGeometry(brush, pen, triangle);
            dc.DrawLine(pen, new Point(rect.Right - 10, y), new Point(rect.Right + 4, y));
        }
    }

    private static void DrawVolume(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect)
    {
        var maxVolume = Math.Max(1, candles.Max(item => item.Volume));
        var step = rect.Width / candles.Count;
        var barWidth = Math.Clamp(step * 0.58, 3, 10);

        DrawText(dc, $"VOL  VOLUME:{candles[^1].Volume / 10000m:F2}万", rect.Left, rect.Top + 4, 12, TextBrush);

        for (var i = 0; i < candles.Count; i++)
        {
            var item = candles[i];
            var x = rect.Left + step * i + step / 2;
            var barHeight = (double)(item.Volume / maxVolume) * (rect.Height - 22);
            var brush = item.Close >= item.Open ? RisingBrush : FallingBrush;
            dc.DrawRectangle(brush, null, new Rect(x - barWidth / 2, rect.Bottom - barHeight, barWidth, barHeight));
        }
    }

    private void DrawIndicatorPanel(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect)
    {
        if (TryDrawExternalIndicatorPanel(dc, candles, rect))
        {
            return;
        }

        switch (IndicatorMode?.Trim().ToUpperInvariant())
        {
            case "KDJ":
                DrawKdjPanel(dc, candles, rect);
                break;
            case "RSI":
                DrawRsiPanel(dc, candles, rect);
                break;
            default:
                DrawMacdLikePanel(dc, candles, rect);
                break;
        }
    }

    private bool TryDrawExternalIndicatorPanel(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect)
    {
        var type = IndicatorMode?.Trim().ToUpperInvariant() ?? "MACD";
        if (IndicatorSeries is not { Points.Count: > 0 } series ||
            !string.Equals(series.IndicatorType, type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var points = GetVisibleIndicatorPoints(candles, series);
        if (points.Count == 0)
        {
            return false;
        }

        switch (type)
        {
            case "KDJ":
                DrawBoundedIndicatorPanel(dc, "KDJ(9,3,3)", points, rect, "K", "D", "J");
                break;
            case "RSI":
                DrawBoundedIndicatorPanel(dc, "RSI(6,12,24)", points, rect, "RSI6", "RSI12", "RSI24");
                break;
            default:
                DrawMacdIndicatorPanel(dc, points, rect);
                break;
        }

        return true;
    }

    private static IReadOnlyList<KLineIndicatorPoint> GetVisibleIndicatorPoints(
        IReadOnlyList<KLineCandle> candles,
        KLineIndicatorSeries series)
    {
        var visibleTimes = candles.Select(item => item.TradingTime).ToHashSet();
        return series.Points
            .Where(item => visibleTimes.Contains(item.TradingTime))
            .OrderBy(item => item.TradingTime)
            .ToArray();
    }

    private static void DrawMacdIndicatorPanel(DrawingContext dc, IReadOnlyList<KLineIndicatorPoint> points, Rect rect)
    {
        var latest = points.LastOrDefault(item => item.Value1.HasValue || item.Value2.HasValue || item.BarValue.HasValue);
        DrawText(
            dc,
            latest is null
                ? "MACD(12,26,9)"
                : $"MACD(12,26,9)  DIF:{latest.Value1 ?? 0m:F3}  DEA:{latest.Value2 ?? 0m:F3}  MACD:{latest.BarValue ?? 0m:F3}",
            rect.Left,
            rect.Top + 4,
            12,
            TextBrush);

        var values = points
            .SelectMany(item => new[] { item.Value1, item.Value2, item.BarValue })
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        if (values.Length == 0)
        {
            return;
        }

        var min = Math.Min(values.Min(), 0m);
        var max = Math.Max(values.Max(), 0m);
        var padding = Math.Max(0.01m, (max - min) * 0.12m);
        min -= padding;
        max += padding;

        var zeroY = MapRangeY(0m, min, max, rect);
        dc.DrawLine(new Pen(GridBrush, 1), new Point(rect.Left, zeroY), new Point(rect.Right, zeroY));

        var step = rect.Width / points.Count;
        var difPoints = new PointCollection();
        var deaPoints = new PointCollection();
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var x = rect.Left + step * i + step / 2;
            if (point.BarValue is { } bar)
            {
                var barY = MapRangeY(bar, min, max, rect);
                var brush = bar >= 0 ? FallingBrush : RisingBrush;
                dc.DrawLine(new Pen(brush, 1), new Point(x, zeroY), new Point(x, barY));
            }

            if (point.Value1 is { } dif)
            {
                difPoints.Add(new Point(x, MapRangeY(dif, min, max, rect)));
            }

            if (point.Value2 is { } dea)
            {
                deaPoints.Add(new Point(x, MapRangeY(dea, min, max, rect)));
            }
        }

        DrawPolyline(dc, difPoints, Ma60Brush);
        DrawPolyline(dc, deaPoints, Ma5Brush);
    }

    private static void DrawBoundedIndicatorPanel(
        DrawingContext dc,
        string title,
        IReadOnlyList<KLineIndicatorPoint> points,
        Rect rect,
        string label1,
        string label2,
        string label3)
    {
        var latest = points.LastOrDefault(item => item.Value1.HasValue || item.Value2.HasValue || item.Value3.HasValue);
        DrawText(
            dc,
            latest is null
                ? title
                : $"{title}  {label1}:{latest.Value1 ?? 0m:F2}  {label2}:{latest.Value2 ?? 0m:F2}  {label3}:{latest.Value3 ?? 0m:F2}",
            rect.Left,
            rect.Top + 4,
            12,
            TextBrush);
        DrawIndicatorReferenceLines(dc, rect);

        DrawExternalIndicatorLine(dc, points, rect, item => item.Value1, Ma5Brush);
        DrawExternalIndicatorLine(dc, points, rect, item => item.Value2, Ma10Brush);
        DrawExternalIndicatorLine(dc, points, rect, item => item.Value3, RisingBrush);
    }

    private static void DrawExternalIndicatorLine(
        DrawingContext dc,
        IReadOnlyList<KLineIndicatorPoint> points,
        Rect rect,
        Func<KLineIndicatorPoint, decimal?> selector,
        Brush brush)
    {
        var linePoints = new PointCollection();
        var step = rect.Width / points.Count;
        for (var i = 0; i < points.Count; i++)
        {
            if (selector(points[i]) is not { } value)
            {
                continue;
            }

            var x = rect.Left + step * i + step / 2;
            linePoints.Add(new Point(x, MapIndicatorY((double)value, rect)));
        }

        DrawPolyline(dc, linePoints, brush);
    }

    private static void DrawMacdLikePanel(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect)
    {
        DrawText(dc, "MACD(12,26,9)  DIF:-1.19  DEA:-1.15  MACD:-0.07", rect.Left, rect.Top + 4, 12, TextBrush);
        var mid = rect.Top + rect.Height * 0.55;
        var zeroPen = new Pen(GridBrush, 1);
        dc.DrawLine(zeroPen, new Point(rect.Left, mid), new Point(rect.Right, mid));

        var step = rect.Width / candles.Count;
        var dif = new PointCollection();
        var dea = new PointCollection();
        for (var i = 0; i < candles.Count; i++)
        {
            var x = rect.Left + step * i + step / 2;
            var wave = Math.Sin(i / 6.0) * 18 + Math.Cos(i / 11.0) * 10;
            var signal = Math.Sin((i + 5) / 7.0) * 14;
            var barBrush = wave >= 0 ? FallingBrush : RisingBrush;
            dc.DrawLine(new Pen(barBrush, 1), new Point(x, mid), new Point(x, mid - wave * 0.65));
            dif.Add(new Point(x, mid - wave));
            dea.Add(new Point(x, mid - signal));
        }

        DrawPolyline(dc, dif, Ma60Brush);
        DrawPolyline(dc, dea, Ma5Brush);
    }

    private static void DrawKdjPanel(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect)
    {
        DrawText(dc, "KDJ(9,3,3)  K:54.21  D:48.63  J:65.38", rect.Left, rect.Top + 4, 12, TextBrush);
        DrawIndicatorReferenceLines(dc, rect);

        var kPoints = new PointCollection();
        var dPoints = new PointCollection();
        var jPoints = new PointCollection();
        var step = rect.Width / candles.Count;
        var k = 50d;
        var d = 50d;

        for (var i = 0; i < candles.Count; i++)
        {
            var start = Math.Max(0, i - 8);
            var window = candles.Skip(start).Take(i - start + 1).ToArray();
            var high = (double)window.Max(item => item.High);
            var low = (double)window.Min(item => item.Low);
            var close = (double)candles[i].Close;
            var rsv = Math.Abs(high - low) < 0.0001 ? 50 : (close - low) / (high - low) * 100;
            k = k * 2 / 3 + rsv / 3;
            d = d * 2 / 3 + k / 3;
            var j = 3 * k - 2 * d;
            var x = rect.Left + step * i + step / 2;
            kPoints.Add(new Point(x, MapIndicatorY(k, rect)));
            dPoints.Add(new Point(x, MapIndicatorY(d, rect)));
            jPoints.Add(new Point(x, MapIndicatorY(j, rect)));
        }

        DrawPolyline(dc, kPoints, Ma5Brush);
        DrawPolyline(dc, dPoints, Ma10Brush);
        DrawPolyline(dc, jPoints, RisingBrush);
    }

    private static void DrawRsiPanel(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect)
    {
        DrawText(dc, "RSI(6,12,24)  RSI6:52.18  RSI12:49.34  RSI24:45.87", rect.Left, rect.Top + 4, 12, TextBrush);
        DrawIndicatorReferenceLines(dc, rect);
        DrawRsiLine(dc, candles, rect, 6, Ma5Brush);
        DrawRsiLine(dc, candles, rect, 12, Ma10Brush);
        DrawRsiLine(dc, candles, rect, 24, RisingBrush);
    }

    private static void DrawRsiLine(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect, int window, Brush brush)
    {
        if (candles.Count < 2)
        {
            return;
        }

        var points = new PointCollection();
        var step = rect.Width / candles.Count;
        for (var i = 1; i < candles.Count; i++)
        {
            var start = Math.Max(1, i - window + 1);
            var gains = 0m;
            var losses = 0m;
            for (var j = start; j <= i; j++)
            {
                var diff = candles[j].Close - candles[j - 1].Close;
                if (diff >= 0)
                {
                    gains += diff;
                }
                else
                {
                    losses += Math.Abs(diff);
                }
            }

            var rsi = losses == 0 ? 100d : (double)(100m - 100m / (1m + gains / losses));
            var x = rect.Left + step * i + step / 2;
            points.Add(new Point(x, MapIndicatorY(rsi, rect)));
        }

        DrawPolyline(dc, points, brush);
    }

    private static void DrawIndicatorReferenceLines(DrawingContext dc, Rect rect)
    {
        var pen = new Pen(GridBrush, 1);
        dc.DrawLine(pen, new Point(rect.Left, MapIndicatorY(80, rect)), new Point(rect.Right, MapIndicatorY(80, rect)));
        dc.DrawLine(pen, new Point(rect.Left, MapIndicatorY(50, rect)), new Point(rect.Right, MapIndicatorY(50, rect)));
        dc.DrawLine(pen, new Point(rect.Left, MapIndicatorY(20, rect)), new Point(rect.Right, MapIndicatorY(20, rect)));
        DrawText(dc, "80", rect.Right + 4, MapIndicatorY(80, rect) - 8, 11, TextBrush);
        DrawText(dc, "50", rect.Right + 4, MapIndicatorY(50, rect) - 8, 11, TextBrush);
        DrawText(dc, "20", rect.Right + 4, MapIndicatorY(20, rect) - 8, 11, TextBrush);
    }

    private static double MapIndicatorY(double value, Rect rect)
    {
        var clamped = Math.Clamp(value, 0, 100);
        return rect.Bottom - clamped / 100d * (rect.Height - 20);
    }

    private static void DrawChipDistribution(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect rect, (decimal Min, decimal Max) priceRange)
    {
        dc.DrawRectangle(BackgroundBrush, new Pen(GridBrush, 1), rect);

        if (candles.Count == 0)
        {
            DrawText(dc, "筹码", rect.Left + 8, rect.Top + 8, 12, TextBrush);
            return;
        }

        var distribution = BuildChipDistribution(candles, 72, priceRange.Min, priceRange.Max);
        var maxVolume = distribution.Max(item => item.Volume);
        if (maxVolume <= 0)
        {
            return;
        }

        var latestClose = candles[^1].Close;
        var totalVolume = distribution.Sum(item => item.Volume);
        var profitVolume = distribution
            .Where(item => item.Price <= latestClose)
            .Sum(item => item.Volume);
        var profitRatio = totalVolume == 0 ? 0 : profitVolume / totalVolume * 100;
        var averageCost = totalVolume == 0
            ? latestClose
            : distribution.Sum(item => item.Price * item.Volume) / totalVolume;
        var peak = distribution.OrderByDescending(item => item.Volume).First();
        var costRange = CalculateCostRange(distribution, totalVolume, 0.90m);
        const double labelWidth = 46d;
        var headerRect = new Rect(rect.Left + 5, rect.Top + 5, rect.Width - 10, 62);
        var footerRect = new Rect(rect.Left + 5, rect.Bottom - 45, rect.Width - 10, 40);
        var distributionRect = new Rect(
            rect.Left,
            headerRect.Bottom + 8,
            rect.Width,
            Math.Max(80, footerRect.Top - headerRect.Bottom - 10));
        var barLeft = distributionRect.Left + labelWidth;
        var barRight = distributionRect.Right - 8;
        var maxWidth = Math.Max(1, barRight - barLeft);

        for (var i = 0; i < distribution.Count; i++)
        {
            var item = distribution[i];
            var y = distributionRect.Bottom - distributionRect.Height * (i + 1) / distribution.Count;
            var h = Math.Max(1, distributionRect.Height / distribution.Count + 1);
            var width = (double)(item.Volume / maxVolume) * maxWidth;
            var brush = item.Price <= latestClose
                ? ChipYellowBrush
                : item.Price <= averageCost
                    ? ChipOrangeBrush
                    : ChipPinkBrush;
            dc.DrawRectangle(brush, null, new Rect(barLeft, y, width, h));
        }

        var peakY = MapChipY(peak.Price, priceRange.Min, priceRange.Max, distributionRect);
        var averageY = MapChipY(averageCost, priceRange.Min, priceRange.Max, distributionRect);
        dc.DrawLine(new Pen(Brushes.White, 1), new Point(barLeft, peakY), new Point(barRight, peakY));
        dc.DrawLine(new Pen(Ma60Brush, 1) { DashStyle = DashStyles.Dash }, new Point(barLeft, averageY), new Point(barRight, averageY));

        DrawChipPriceLabels(dc, distributionRect, labelWidth, priceRange.Min, priceRange.Max, headerRect, footerRect);

        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 15, 19, 26)), new Pen(TooltipBorderBrush, 1), headerRect);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 15, 19, 26)), null, footerRect);
        DrawText(dc, "筹码分布", headerRect.Left + 7, headerRect.Top + 5, 11, Brushes.White);
        DrawText(dc, $"主峰 {peak.Price:F2}", headerRect.Left + 7, headerRect.Top + 22, 10, ChipPinkBrush);
        DrawText(dc, $"均价 {averageCost:F2}", headerRect.Left + 7, headerRect.Top + 39, 10, TextBrush);
        DrawText(dc, $"获利 {profitRatio:F1}%", headerRect.Left + 92, headerRect.Top + 39, 10, ChipYellowBrush);
        DrawText(dc, "黄=获利  粉=套牢", footerRect.Left + 7, footerRect.Top + 5, 10, TextBrush);
        DrawText(dc, $"90%成本 {costRange.Low:F2}-{costRange.High:F2}", footerRect.Left + 7, footerRect.Top + 23, 10, TextBrush);
    }

    private static IReadOnlyList<ChipBucket> BuildChipDistribution(IReadOnlyList<KLineCandle> candles, int bucketCount, decimal minPrice, decimal maxPrice)
    {
        if (maxPrice <= minPrice)
        {
            maxPrice = minPrice + 0.01m;
        }

        var step = (maxPrice - minPrice) / bucketCount;

        var buckets = Enumerable.Range(0, bucketCount)
            .Select(index => new ChipBucket(
                minPrice + step * (index + 0.5m),
                0m))
            .ToArray();

        foreach (var candle in candles)
        {
            var low = Math.Min(candle.Low, candle.High);
            var high = Math.Max(candle.Low, candle.High);
            var first = Math.Clamp((int)Math.Floor((low - minPrice) / step), 0, bucketCount - 1);
            var last = Math.Clamp((int)Math.Floor((high - minPrice) / step), 0, bucketCount - 1);
            var touched = Math.Max(1, last - first + 1);
            var volumeShare = candle.Volume / touched;

            for (var i = first; i <= last; i++)
            {
                buckets[i] = buckets[i] with { Volume = buckets[i].Volume + volumeShare };
            }
        }

        return buckets;
    }

    private static void DrawChipPriceLabels(
        DrawingContext dc,
        Rect rect,
        double labelWidth,
        decimal min,
        decimal max,
        Rect headerRect,
        Rect footerRect)
    {
        var mid = (min + max) / 2m;
        var pen = new Pen(GridBrush, 1);

        var lastLabelBottom = double.NegativeInfinity;
        foreach (var price in new[] { max, mid, min })
        {
            var y = MapChipY(price, min, max, rect);
            dc.DrawLine(pen, new Point(rect.Left + labelWidth - 4, y), new Point(rect.Right - 6, y));
            var labelY = Math.Clamp(y - 7, rect.Top + 2, rect.Bottom - 13);
            if (labelY < lastLabelBottom + 2)
            {
                labelY = lastLabelBottom + 2;
            }

            if (labelY <= rect.Bottom - 12)
            {
                DrawText(dc, $"{price:F2}", rect.Left + 4, labelY, 10, TextBrush);
                lastLabelBottom = labelY + 12;
            }
        }
    }



    private static (decimal Low, decimal High) CalculateCostRange(
        IReadOnlyList<ChipBucket> distribution,
        decimal totalVolume,
        decimal targetRatio)
    {
        if (distribution.Count == 0 || totalVolume <= 0)
        {
            return (0, 0);
        }

        var lowerCut = totalVolume * ((1m - targetRatio) / 2m);
        var upperCut = totalVolume * (1m - (1m - targetRatio) / 2m);
        var cumulative = 0m;
        var low = distribution[0].Price;
        var high = distribution[^1].Price;

        foreach (var item in distribution.OrderBy(item => item.Price))
        {
            cumulative += item.Volume;
            if (cumulative >= lowerCut && low == distribution[0].Price)
            {
                low = item.Price;
            }

            if (cumulative >= upperCut)
            {
                high = item.Price;
                break;
            }
        }

        return (low, high);
    }

    private static double MapChipY(decimal price, decimal min, decimal max, Rect rect)
    {
        if (max <= min)
        {
            return rect.Top + rect.Height / 2;
        }

        return rect.Bottom - (double)((price - min) / (max - min)) * rect.Height;
    }

    private static void DrawAxisLabels(DrawingContext dc, IReadOnlyList<KLineCandle> candles, Rect chartRect, Rect volumeRect, Rect macdRect)
    {
        var (minPrice, maxPrice) = GetPriceRange(candles);
        for (var i = 0; i <= 5; i++)
        {
            var value = maxPrice - (maxPrice - minPrice) * i / 5;
            DrawText(dc, $"{value:F2}", chartRect.Right + 4, chartRect.Top + chartRect.Height * i / 5 - 8, 11, TextBrush);
        }

        var intraday = IsIntradayCandles(candles);
        DrawText(dc, FormatAxisTime(candles.First().TradingTime, intraday, includeDate: true), chartRect.Left + 8, macdRect.Bottom - 16, 11, TextBrush);
        DrawText(dc, FormatAxisTime(candles[candles.Count / 2].TradingTime, intraday, includeDate: false), chartRect.Left + chartRect.Width / 2 - 24, macdRect.Bottom - 16, 11, TextBrush);
        DrawText(dc, FormatAxisTime(candles.Last().TradingTime, intraday, includeDate: true), chartRect.Right - 88, macdRect.Bottom - 16, 11, TextBrush);
        DrawText(dc, "93.10", volumeRect.Right + 4, volumeRect.Top + 2, 11, TextBrush);
        DrawText(dc, "0.00", volumeRect.Right + 4, volumeRect.Bottom - 14, 11, TextBrush);
    }

    private static bool IsIntradayCandles(IReadOnlyList<KLineCandle> candles)
    {
        return candles.Any(item => item.TradingTime.TimeOfDay != TimeSpan.Zero);
    }

    private static string FormatAxisTime(DateTime value, bool intraday, bool includeDate)
    {
        if (!intraday)
        {
            return includeDate ? value.ToString("MM-dd") : value.ToString("MM");
        }

        return includeDate ? value.ToString("MM-dd HH:mm") : value.ToString("HH:mm");
    }

    private void DrawCrosshair(
        DrawingContext dc,
        IReadOnlyList<KLineCandle> candles,
        Rect chartRect,
        Rect volumeRect,
        Rect macdRect)
    {
        if (candles.Count == 0)
        {
            return;
        }

        if (_mousePosition is not { } point)
        {
            return;
        }

        var fullRect = new Rect(chartRect.Left, chartRect.Top, chartRect.Width, macdRect.Bottom - chartRect.Top);
        if (!fullRect.Contains(point))
        {
            return;
        }

        var step = chartRect.Width / candles.Count;
        var index = GetHitCandleIndex(candles, chartRect) ?? 0;
        var candle = candles[index];
        var x = chartRect.Left + step * index + step / 2;
        var y = Math.Clamp(point.Y, chartRect.Top, macdRect.Bottom);
        var pen = new Pen(CrosshairBrush, 1) { DashStyle = DashStyles.Dash };

        dc.DrawLine(pen, new Point(x, chartRect.Top), new Point(x, macdRect.Bottom));
        dc.DrawLine(pen, new Point(chartRect.Left, y), new Point(chartRect.Right, y));

        if (chartRect.Contains(point))
        {
            var (minPrice, maxPrice) = GetPriceRange(candles);
            var price = maxPrice - (decimal)((point.Y - chartRect.Top) / chartRect.Height) * (maxPrice - minPrice);
            DrawPriceMarker(dc, point, chartRect, price);
        }

        DrawDateMarker(dc, candle, x, macdRect);
        DrawCandleTooltip(dc, candle, index, candles.Count, chartRect);
    }

    private static void DrawPriceMarker(DrawingContext dc, Point point, Rect chartRect, decimal price)
    {
        var markerRect = new Rect(chartRect.Right + 2, Math.Clamp(point.Y - 9, chartRect.Top, chartRect.Bottom - 18), 54, 18);
        dc.DrawRectangle(TooltipBackgroundBrush, new Pen(TooltipBorderBrush, 1), markerRect);
        DrawText(dc, $"{price:F2}", markerRect.Left + 7, markerRect.Top + 2, 11, TextBrush);
    }

    private static void DrawDateMarker(DrawingContext dc, KLineCandle candle, double x, Rect macdRect)
    {
        var text = candle.TradingTime.ToString("MM-dd HH:mm");
        var markerRect = new Rect(Math.Clamp(x - 42, macdRect.Left, macdRect.Right - 84), macdRect.Bottom - 20, 84, 18);
        dc.DrawRectangle(TooltipBackgroundBrush, new Pen(TooltipBorderBrush, 1), markerRect);
        DrawText(dc, text, markerRect.Left + 6, markerRect.Top + 2, 11, TextBrush);
    }

    private static void DrawCandleTooltip(DrawingContext dc, KLineCandle candle, int index, int count, Rect chartRect)
    {
        var leftSide = index > count / 2;
        var tooltipWidth = 148d;
        var tooltipHeight = 112d;
        var x = leftSide ? chartRect.Left + 12 : chartRect.Right - tooltipWidth - 12;
        var y = chartRect.Top + 12;
        var rect = new Rect(x, y, tooltipWidth, tooltipHeight);
        var color = candle.Close >= candle.Open ? RisingBrush : FallingBrush;

        dc.DrawRectangle(TooltipBackgroundBrush, new Pen(TooltipBorderBrush, 1), rect);
        DrawText(dc, candle.TradingTime.ToString("yyyy-MM-dd HH:mm"), x + 10, y + 8, 11, TextBrush);
        DrawText(dc, $"开  {candle.Open:F2}", x + 10, y + 28, 11, TextBrush);
        DrawText(dc, $"高  {candle.High:F2}", x + 10, y + 44, 11, TextBrush);
        DrawText(dc, $"低  {candle.Low:F2}", x + 10, y + 60, 11, TextBrush);
        DrawText(dc, $"收  {candle.Close:F2}", x + 10, y + 76, 11, color);
        DrawText(dc, $"量  {candle.Volume / 10000m:F2}万", x + 10, y + 92, 11, TextBrush);

    }

    private int? GetHitCandleIndex(IReadOnlyList<KLineCandle> candles, Rect chartRect)
    {
        if (candles.Count == 0 || _mousePosition is not { } point || !chartRect.Contains(point))
        {
            return null;
        }

        var step = chartRect.Width / candles.Count;
        return (int)Math.Clamp(Math.Floor((point.X - chartRect.Left) / step), 0, candles.Count - 1);
    }

    private static int FindNearestCandleIndex(IReadOnlyList<KLineCandle> candles, DateTime tradingTime)
    {
        if (candles.Count == 0)
        {
            return -1;
        }

        var index = 0;
        var bestDistance = TimeSpan.MaxValue;
        for (var i = 0; i < candles.Count; i++)
        {
            var distance = (candles[i].TradingTime - tradingTime).Duration();
            if (distance < bestDistance)
            {
                bestDistance = distance;
                index = i;
            }
        }

        return index;
    }

    private static void DrawPolyline(DrawingContext dc, PointCollection points, Brush brush)
    {
        if (points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i], true, false);
            }
        }

        dc.DrawGeometry(null, new Pen(brush, 1), geometry);
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush)
    {
        var formattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            size,
            brush,
            1.0);

        dc.DrawText(formattedText, new Point(x, y));
    }

    private static double MapY(decimal value, decimal min, decimal max, Rect rect)
    {
        if (max <= min)
        {
            return rect.Top + rect.Height / 2;
        }

        return rect.Bottom - (double)((value - min) / (max - min)) * rect.Height;
    }

    private static double MapRangeY(decimal value, decimal min, decimal max, Rect rect)
    {
        if (max <= min)
        {
            return rect.Top + rect.Height / 2;
        }

        return rect.Bottom - (double)((value - min) / (max - min)) * (rect.Height - 20);
    }

    private static (decimal Min, decimal Max) GetPriceRange(IReadOnlyList<KLineCandle> candles)
    {
        var min = candles.Min(item => item.Low);
        var max = candles.Max(item => item.High);
        var padding = Math.Max(0.01m, (max - min) * 0.08m);
        return (min - padding, max + padding);
    }

    private static decimal Average(IReadOnlyList<KLineCandle> candles, int window)
    {
        var count = Math.Min(window, candles.Count);
        return candles.TakeLast(count).Average(item => item.Close);
    }

    private IReadOnlyList<KLineCandle> GetVisibleCandles(IReadOnlyList<KLineCandle> candles)
    {
        if (candles.Count == 0)
        {
            return candles;
        }

        _visibleCount = Math.Clamp(_visibleCount, 30, Math.Max(30, candles.Count));
        SetRightOffset(_rightOffset, candles.Count);

        var count = Math.Min(_visibleCount, candles.Count);
        var endExclusive = Math.Max(count, candles.Count - _rightOffset);
        var start = Math.Max(0, endExclusive - count);
        return candles.Skip(start).Take(count).ToArray();
    }

    private void SetRightOffset(int value, int candleCount)
    {
        var maxOffset = Math.Max(0, candleCount - Math.Min(_visibleCount, candleCount));
        _rightOffset = Math.Clamp(value, 0, maxOffset);
    }

    private sealed record ChipBucket(decimal Price, decimal Volume);
}
