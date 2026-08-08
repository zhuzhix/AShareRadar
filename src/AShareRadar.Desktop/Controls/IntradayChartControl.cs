using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AShareRadar.Desktop.Controls;

public sealed class IntradayChartControl : FrameworkElement
{
    public static readonly DependencyProperty CandlesProperty = DependencyProperty.Register(
        nameof(Candles), typeof(IReadOnlyList<KLineCandle>), typeof(IntradayChartControl),
        new FrameworkPropertyMetadata(Array.Empty<KLineCandle>(), FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty SymbolNameProperty = DependencyProperty.Register(
        nameof(SymbolName), typeof(string), typeof(IntradayChartControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviousCloseProperty = DependencyProperty.Register(
        nameof(PreviousClose), typeof(decimal), typeof(IntradayChartControl),
        new FrameworkPropertyMetadata(0m, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFiveDayProperty = DependencyProperty.Register(
        nameof(IsFiveDay), typeof(bool), typeof(IntradayChartControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty IndicatorSeriesProperty = DependencyProperty.Register(
        nameof(IndicatorSeries), typeof(KLineIndicatorSeries), typeof(IntradayChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TradeMarkersProperty = DependencyProperty.Register(
        nameof(TradeMarkers), typeof(IReadOnlyList<KLineTradeMarker>), typeof(IntradayChartControl),
        new FrameworkPropertyMetadata(Array.Empty<KLineTradeMarker>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowTrainingMarkersProperty = DependencyProperty.Register(
        nameof(ShowTrainingMarkers), typeof(bool), typeof(IntradayChartControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush BackgroundBrush = Brush(5, 7, 6);
    private static readonly Brush PanelBrush = Brush(7, 9, 8);
    private static readonly Brush GridBrush = Brush(24, 29, 29);
    private static readonly Brush StrongGridBrush = Brush(52, 58, 58);
    private static readonly Brush TextBrush = Brush(127, 137, 138);
    private static readonly Brush MutedTextBrush = Brush(94, 104, 105);
    private static readonly Brush PriceBrush = Brush(191, 196, 198);
    private static readonly Brush VwapBrush = Brush(176, 160, 0);
    private static readonly Brush RisingBrush = Brush(224, 69, 63);
    private static readonly Brush FallingBrush = Brush(0, 191, 174);
    private static readonly Brush NeutralBrush = Brush(118, 128, 129);
    private static readonly Brush DifBrush = Brush(218, 188, 48);
    private static readonly Brush DeaBrush = Brush(78, 156, 214);
    private static readonly Brush CrosshairBrush = Brush(110, 119, 120);
    private static readonly Brush TooltipBrush = new SolidColorBrush(Color.FromArgb(238, 10, 14, 14));
    private static readonly Brush TooltipBorderBrush = Brush(66, 78, 78);
    private static readonly Brush BuyBrush = Brush(44, 184, 96);
    private static readonly Brush StopBrush = Brush(224, 69, 63);
    private static readonly Brush TakeProfitBrush = Brush(220, 135, 36);
    private static readonly Brush TrainingBuyBrush = Brush(238, 72, 66);
    private static readonly Brush TrainingSellBrush = Brush(48, 196, 104);

    private const double HeaderHeight = 36;
    private const double LeftAxisWidth = 58;
    private const double RightAxisWidth = 60;
    private const double TimeAxisHeight = 22;
    private const int FiveDaySlotCount = 5;
    private const double MorningSessionRatio = 0.495;
    private const double AfternoonSessionStartRatio = 0.505;
    private const double GridLineThickness = 0.6;
    private const double StrongGridLineThickness = 1.0;
    private const double PriceLineThickness = 1.05;
    private const double VwapLineThickness = 0.9;
    private const double IndicatorLineThickness = 0.85;
    private const double CrosshairLineThickness = 0.6;
    private Point? _mousePosition;

    public IntradayChartControl()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
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

    public decimal PreviousClose
    {
        get => (decimal)GetValue(PreviousCloseProperty);
        set => SetValue(PreviousCloseProperty, value);
    }

    public bool IsFiveDay
    {
        get => (bool)GetValue(IsFiveDayProperty);
        set => SetValue(IsFiveDayProperty, value);
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

    public bool ShowTrainingMarkers
    {
        get => (bool)GetValue(ShowTrainingMarkersProperty);
        set => SetValue(ShowTrainingMarkersProperty, value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mousePosition = e.GetPosition(this);
        InvalidateVisual();
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mousePosition = null;
        InvalidateVisual();
        base.OnMouseLeave(e);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 360 || ActualHeight < 300)
        {
            DrawText(dc, "分时区域过小", 12, 12, 12, TextBrush);
            return;
        }

        var orderedBars = Candles.OrderBy(item => item.TradingTime).ToArray();
        var bars = orderedBars.Length == 0
            ? orderedBars
            : IsFiveDay
                ? TakeLatestTradingDays(orderedBars, FiveDaySlotCount)
                : orderedBars.Where(item => item.TradingTime.Date == orderedBars[^1].TradingTime.Date).ToArray();
        var layout = CreateLayout();
        if (bars.Length == 0)
        {
            DrawPanel(dc, layout.Price);
            DrawPanel(dc, layout.Volume);
            DrawPanel(dc, layout.Macd);
            DrawText(dc, "暂无分时数据", layout.Price.Left + 18, layout.Price.Top + 18, 13, TextBrush);
            return;
        }

        var reference = PreviousClose > 0 ? PreviousClose : bars[0].Open;
        var mapped = BuildMappedBars(bars, layout.Price);
        var vwap = CalculateVwap(bars);
        var maxDeviation = GetMaxDeviation(bars, vwap, reference);

        DrawHeader(dc, bars, vwap, reference);
        DrawPriceGrid(dc, layout.Price, reference, maxDeviation);
        DrawPanelGrid(dc, layout.Volume, 2);
        DrawPanelGrid(dc, layout.Macd, 2);
        DrawTimeAxis(dc, bars, layout.Price, layout.Macd);
        DrawPriceLines(dc, bars, mapped, vwap, layout.Price, reference, maxDeviation);
        DrawTradeMarkers(dc, bars, mapped, layout.Price, reference, maxDeviation);
        DrawTrainingMarkers(dc, bars, mapped, layout.Price, reference, maxDeviation);
        DrawLatestMarker(
            dc,
            bars[^1],
            mapped[^1].X,
            layout.Price,
            reference,
            maxDeviation,
            GetDailyReference(bars, bars.Length - 1, reference));
        DrawVolume(dc, bars, mapped, layout.Volume);
        DrawMacd(dc, bars, mapped, layout.Macd);
        DrawCrosshair(dc, bars, mapped, vwap, layout, reference, maxDeviation);
    }

    private Layout CreateLayout()
    {
        var plotLeft = LeftAxisWidth;
        var plotWidth = Math.Max(1, ActualWidth - LeftAxisWidth - RightAxisWidth);
        var contentHeight = ActualHeight - HeaderHeight - TimeAxisHeight;
        var macdHeight = Math.Max(64, contentHeight * 0.16);
        var volumeHeight = Math.Max(58, contentHeight * 0.15);
        var priceHeight = Math.Max(100, contentHeight - volumeHeight - macdHeight);
        return new Layout(
            new Rect(plotLeft, HeaderHeight, plotWidth, priceHeight),
            new Rect(plotLeft, HeaderHeight + priceHeight, plotWidth, volumeHeight),
            new Rect(plotLeft, HeaderHeight + priceHeight + volumeHeight, plotWidth, macdHeight));
    }

    private static KLineCandle[] TakeLatestTradingDays(IReadOnlyList<KLineCandle> bars, int dayCount)
    {
        if (bars.Count == 0)
        {
            return [];
        }

        var dates = bars
            .Select(item => item.TradingTime.Date)
            .Distinct()
            .OrderBy(item => item)
            .TakeLast(dayCount)
            .ToHashSet();
        return bars.Where(item => dates.Contains(item.TradingTime.Date)).ToArray();
    }

    private IReadOnlyList<MappedBar> BuildMappedBars(IReadOnlyList<KLineCandle> bars, Rect rect)
    {
        if (!IsFiveDay)
        {
            return bars.Select((bar, index) => new MappedBar(bar, MapSingleDayX(bar.TradingTime, rect), index)).ToArray();
        }

        var visibleDates = bars
            .Select(item => item.TradingTime.Date)
            .Distinct()
            .OrderBy(item => item)
            .TakeLast(FiveDaySlotCount)
            .ToArray();
        var dateSlots = visibleDates
            .Select((date, index) => (Date: date, Slot: FiveDaySlotCount - visibleDates.Length + index))
            .ToDictionary(item => item.Date, item => item.Slot);
        var result = new List<MappedBar>(bars.Count);
        for (var index = 0; index < bars.Count; index++)
        {
            var bar = bars[index];
            if (!dateSlots.TryGetValue(bar.TradingTime.Date, out var slot))
            {
                continue;
            }

            var segmentLeft = rect.Left + rect.Width * slot / FiveDaySlotCount;
            var segmentWidth = rect.Width / FiveDaySlotCount;
            result.Add(new MappedBar(bar, segmentLeft + MapTradingSessionRatio(bar.TradingTime) * segmentWidth, index));
        }

        return result.ToArray();
    }

    private static double MapSingleDayX(DateTime time, Rect rect)
        => rect.Left + MapTradingSessionRatio(time) * rect.Width;

    private static double MapTradingSessionRatio(DateTime time)
    {
        var morning = time.TimeOfDay < TimeSpan.FromHours(12);
        var sessionMinutes = morning
            ? (time.TimeOfDay - new TimeSpan(9, 30, 0)).TotalMinutes
            : (time.TimeOfDay - new TimeSpan(13, 0, 0)).TotalMinutes;
        return morning
            ? Math.Clamp(sessionMinutes / 120d, 0, 1) * MorningSessionRatio
            : AfternoonSessionStartRatio + Math.Clamp(sessionMinutes / 120d, 0, 1) * MorningSessionRatio;
    }

    private static decimal[] CalculateVwap(IReadOnlyList<KLineCandle> bars)
    {
        var result = new decimal[bars.Count];
        decimal amount = 0;
        decimal volume = 0;
        DateTime? date = null;
        for (var index = 0; index < bars.Count; index++)
        {
            if (date != bars[index].TradingTime.Date)
            {
                date = bars[index].TradingTime.Date;
                amount = 0;
                volume = 0;
            }

            var barVolume = Math.Max(0, bars[index].Volume);
            var typicalPrice = (bars[index].High + bars[index].Low + bars[index].Close) / 3m;
            var averagePrice = barVolume > 0 ? bars[index].Amount / barVolume : 0m;
            var barAmount = averagePrice >= bars[index].Low * 0.95m && averagePrice <= bars[index].High * 1.05m
                ? bars[index].Amount
                : typicalPrice * barVolume;
            amount += Math.Max(0, barAmount);
            volume += barVolume;
            result[index] = volume > 0 ? amount / volume : bars[index].Close;
        }

        return result;
    }

    private static decimal GetMaxDeviation(IReadOnlyList<KLineCandle> bars, IReadOnlyList<decimal> vwap, decimal reference)
    {
        var deviations = bars
            .SelectMany(item => GetValidPricePoints(item, reference))
            .Concat(vwap.Where(item => IsValidPrice(item, reference)))
            .Select(item => Math.Abs(item - reference))
            .Where(item => item >= 0)
            .OrderBy(item => item)
            .ToArray();
        var minimumDeviation = Math.Max(reference * 0.005m, 0.01m);
        if (deviations.Length == 0)
        {
            return minimumDeviation;
        }

        var maximum = deviations[^1];
        var percentile98 = deviations[Math.Clamp((int)Math.Floor((deviations.Length - 1) * 0.98), 0, deviations.Length - 1)];
        var latestDeviation = bars.Count == 0 || !IsValidPrice(bars[^1].Close, reference)
            ? 0m
            : Math.Abs(bars[^1].Close - reference);
        var robustMaximum = deviations.Length >= 40 && percentile98 > 0 && maximum > percentile98 * 2.2m
            ? Math.Max(percentile98 * 1.18m, latestDeviation * 1.1m)
            : maximum;
        return Math.Max(robustMaximum * 1.08m, minimumDeviation);
    }

    private static IEnumerable<decimal> GetValidPricePoints(KLineCandle bar, decimal reference)
    {
        if (bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0 || bar.High < bar.Low)
        {
            yield break;
        }

        if (IsValidPrice(bar.High, reference)) yield return bar.High;
        if (IsValidPrice(bar.Low, reference)) yield return bar.Low;
        if (IsValidPrice(bar.Close, reference)) yield return bar.Close;
        if (IsValidPrice(bar.Open, reference)) yield return bar.Open;
    }

    private static bool IsValidPrice(decimal value, decimal reference)
    {
        if (value <= 0)
        {
            return false;
        }

        if (reference <= 0)
        {
            return true;
        }

        var ratio = value / reference;
        return ratio is >= 0.35m and <= 2.80m;
    }

    private void DrawHeader(DrawingContext dc, IReadOnlyList<KLineCandle> bars, IReadOnlyList<decimal> vwap, decimal reference)
    {
        var last = bars[^1];
        var latestReference = GetDailyReference(bars, bars.Count - 1, reference);
        var change = latestReference > 0 ? (last.Close - latestReference) / latestReference * 100 : 0;
        var changeBrush = ChangeBrush(last.Close, latestReference);
        DrawText(dc, SymbolName, 12, 8, 13, PriceBrush, FontWeights.SemiBold);
        DrawText(dc, last.Close.ToString("F2"), 190, 7, 15, changeBrush, FontWeights.Bold);
        DrawText(dc, $"{change:+0.00;-0.00;0.00}%", 260, 9, 12, changeBrush);
        DrawText(dc, "价格", 350, 10, 11, MutedTextBrush);
        DrawText(dc, "均价", 390, 10, 11, VwapBrush);
        DrawText(dc, vwap[^1].ToString("F2"), 430, 10, 11, VwapBrush);
    }

    private void DrawPriceGrid(DrawingContext dc, Rect rect, decimal reference, decimal maxDeviation)
    {
        DrawPanel(dc, rect);
        for (var row = 0; row <= 8; row++)
        {
            var ratio = row / 8d;
            var y = rect.Top + ratio * rect.Height;
            var pen = row == 4 ? new Pen(StrongGridBrush, StrongGridLineThickness) : new Pen(GridBrush, GridLineThickness);
            dc.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));
            var price = reference + maxDeviation * (decimal)(1 - ratio * 2);
            var percent = reference > 0 ? (price - reference) / reference * 100 : 0;
            var brush = ChangeBrush(price, reference);
            DrawText(dc, price.ToString("F2"), 4, y - 7, 10, brush);
            DrawText(dc, $"{percent:+0.00;-0.00;0.00}%", rect.Right + 5, y - 7, 10, brush);
        }

        for (var column = 0; column <= (IsFiveDay ? FiveDaySlotCount : 4); column++)
        {
            var x = rect.Left + rect.Width * column / (IsFiveDay ? FiveDaySlotCount : 4);
            var pen = IsFiveDay && column > 0 && column < FiveDaySlotCount
                ? new Pen(StrongGridBrush, 0.75)
                : new Pen(GridBrush, GridLineThickness);
            dc.DrawLine(pen, new Point(x, rect.Top), new Point(x, rect.Bottom));
        }
    }

    private static void DrawPanel(DrawingContext dc, Rect rect)
    {
        dc.DrawRectangle(PanelBrush, new Pen(GridBrush, GridLineThickness), rect);
    }

    private static void DrawPanelGrid(DrawingContext dc, Rect rect, int rows)
    {
        DrawPanel(dc, rect);
        for (var row = 1; row < rows; row++)
        {
            var y = rect.Top + rect.Height * row / rows;
            dc.DrawLine(new Pen(GridBrush, GridLineThickness), new Point(rect.Left, y), new Point(rect.Right, y));
        }
    }

    private void DrawTimeAxis(DrawingContext dc, IReadOnlyList<KLineCandle> bars, Rect priceRect, Rect macdRect)
    {
        if (!IsFiveDay)
        {
            var labels = new[] { (0d, "09:30"), (0.25, "10:30"), (0.5, "11:30/13:00"), (0.75, "14:00"), (1d, "15:00") };
            foreach (var (ratio, label) in labels)
            {
                DrawCenteredText(dc, label, priceRect.Left + priceRect.Width * ratio, macdRect.Bottom + 4, 10, MutedTextBrush);
            }
            return;
        }

        var dates = bars.Select(item => item.TradingTime.Date).Distinct().OrderBy(item => item).TakeLast(FiveDaySlotCount).ToArray();
        var startSlot = FiveDaySlotCount - dates.Length;
        for (var index = 0; index < dates.Length; index++)
        {
            var x = priceRect.Left + priceRect.Width * (startSlot + index + 0.5) / FiveDaySlotCount;
            DrawCenteredText(dc, dates[index].ToString("MM-dd"), x, macdRect.Bottom + 4, 10, MutedTextBrush);
        }
    }

    private static void DrawPriceLines(DrawingContext dc, IReadOnlyList<KLineCandle> bars, IReadOnlyList<MappedBar> mapped,
        IReadOnlyList<decimal> vwap, Rect rect, decimal reference, decimal maxDeviation)
    {
        dc.PushClip(new RectangleGeometry(rect));
        var priceGeometry = new StreamGeometry();
        using (var context = priceGeometry.Open())
        {
            for (var index = 0; index < mapped.Count; index++)
            {
                var barIndex = mapped[index].Index;
                var point = new Point(mapped[index].X, MapPriceY(bars[barIndex].Close, rect, reference, maxDeviation));
                if (index == 0 || mapped[index].Bar.TradingTime.Date != mapped[index - 1].Bar.TradingTime.Date)
                {
                    context.BeginFigure(point, false, false);
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }
        priceGeometry.Freeze();
        dc.DrawGeometry(null, new Pen(PriceBrush, PriceLineThickness), priceGeometry);

        var vwapGeometry = new StreamGeometry();
        using (var context = vwapGeometry.Open())
        {
            for (var index = 0; index < mapped.Count; index++)
            {
                var barIndex = mapped[index].Index;
                var point = new Point(mapped[index].X, MapPriceY(vwap[barIndex], rect, reference, maxDeviation));
                if (index == 0 || mapped[index].Bar.TradingTime.Date != mapped[index - 1].Bar.TradingTime.Date)
                {
                    context.BeginFigure(point, false, false);
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }
        vwapGeometry.Freeze();
        dc.DrawGeometry(null, new Pen(VwapBrush, VwapLineThickness), vwapGeometry);
        dc.Pop();
    }

    private static void DrawVolume(DrawingContext dc, IReadOnlyList<KLineCandle> bars, IReadOnlyList<MappedBar> mapped, Rect rect)
    {
        var max = bars.Max(item => item.Volume);
        if (max <= 0) return;
        var widthBase = IsMappedFiveDay(mapped) ? FiveDaySlotCount * 240 : mapped.Count;
        var width = Math.Max(1, Math.Min(4, rect.Width / Math.Max(1, widthBase) * 0.72));
        for (var index = 0; index < mapped.Count; index++)
        {
            var barIndex = mapped[index].Index;
            var height = (double)(bars[barIndex].Volume / max) * (rect.Height - 5);
            var firstBarOfDay = barIndex == 0 || bars[barIndex].TradingTime.Date != bars[barIndex - 1].TradingTime.Date;
            var comparison = firstBarOfDay ? bars[barIndex].Open : bars[barIndex - 1].Close;
            var brush = ChangeBrush(bars[barIndex].Close, comparison);
            dc.DrawRectangle(brush, null, new Rect(mapped[index].X - width / 2, rect.Bottom - height, width, height));
        }
        DrawText(dc, $"VOL {FormatLargeNumber(bars[^1].Volume)}", rect.Left + 6, rect.Top + 4, 10, TextBrush);
    }

    private void DrawMacd(DrawingContext dc, IReadOnlyList<KLineCandle> bars, IReadOnlyList<MappedBar> mapped, Rect rect)
    {
        var values = GetMacdValues(bars);
        var maximum = values.SelectMany(item => new[] { Math.Abs(item.Dif), Math.Abs(item.Dea), Math.Abs(item.Bar) }).DefaultIfEmpty(0.01m).Max();
        maximum = Math.Max(maximum, 0.0001m);
        var zeroY = rect.Top + rect.Height / 2;
        dc.DrawLine(new Pen(StrongGridBrush, 0.75), new Point(rect.Left, zeroY), new Point(rect.Right, zeroY));
        var widthBase = IsMappedFiveDay(mapped) ? FiveDaySlotCount * 240 : mapped.Count;
        var barWidth = Math.Max(1, Math.Min(4, rect.Width / Math.Max(1, widthBase) * 0.65));
        for (var index = 0; index < mapped.Count; index++)
        {
            var value = values[mapped[index].Index];
            var barY = MapMacdY(value.Bar, rect, maximum);
            dc.DrawRectangle(value.Bar >= 0 ? RisingBrush : FallingBrush, null,
                new Rect(mapped[index].X - barWidth / 2, Math.Min(zeroY, barY), barWidth, Math.Max(1, Math.Abs(zeroY - barY))));
        }
        DrawSeries(dc, mapped, values.Select(item => item.Dif).ToArray(), rect, maximum, DifBrush);
        DrawSeries(dc, mapped, values.Select(item => item.Dea).ToArray(), rect, maximum, DeaBrush);
        var last = values[^1];
        DrawText(dc, $"MACD(12,26,9)  DIF {last.Dif:F3}  DEA {last.Dea:F3}  MACD {last.Bar:F3}", rect.Left + 6, rect.Top + 4, 10, TextBrush);
    }

    private IReadOnlyList<MacdValue> GetMacdValues(IReadOnlyList<KLineCandle> bars)
    {
        if (IndicatorSeries is { IndicatorType: "MACD", Points.Count: > 0 } series)
        {
            var byTime = series.Points.ToDictionary(item => item.TradingTime, item => item);
            if (bars.All(item => byTime.ContainsKey(item.TradingTime)))
            {
                return bars.Select(item =>
                {
                    var point = byTime[item.TradingTime];
                    return new MacdValue(point.Value1 ?? 0, point.Value2 ?? 0, point.BarValue ?? point.Value3 ?? 0);
                }).ToArray();
            }
        }

        decimal ema12 = bars[0].Close;
        decimal ema26 = bars[0].Close;
        decimal dea = 0;
        var result = new List<MacdValue>(bars.Count);
        foreach (var bar in bars)
        {
            ema12 = ema12 * 11m / 13m + bar.Close * 2m / 13m;
            ema26 = ema26 * 25m / 27m + bar.Close * 2m / 27m;
            var dif = ema12 - ema26;
            dea = dea * 8m / 10m + dif * 2m / 10m;
            result.Add(new MacdValue(dif, dea, (dif - dea) * 2));
        }
        return result;
    }

    private static void DrawSeries(DrawingContext dc, IReadOnlyList<MappedBar> mapped, IReadOnlyList<decimal> values,
        Rect rect, decimal maximum, Brush brush)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        for (var index = 0; index < mapped.Count; index++)
        {
            var point = new Point(mapped[index].X, MapMacdY(values[mapped[index].Index], rect, maximum));
            if (index == 0 || mapped[index].Bar.TradingTime.Date != mapped[index - 1].Bar.TradingTime.Date)
                context.BeginFigure(point, false, false);
            else
                context.LineTo(point, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(brush, IndicatorLineThickness), geometry);
    }

    private void DrawTradeMarkers(DrawingContext dc, IReadOnlyList<KLineCandle> bars, IReadOnlyList<MappedBar> mapped,
        Rect rect, decimal reference, decimal maxDeviation)
    {
        foreach (var marker in TradeMarkers)
        {
            var index = FindNearestBarIndex(bars, marker.TradingTime);
            if (index < 0) continue;
            var mappedMarker = mapped.FirstOrDefault(item => item.Index == index);
            if (mappedMarker is null) continue;
            var brush = marker.MarkerType switch
            {
                "StopLoss" => StopBrush,
                "TakeProfit" => TakeProfitBrush,
                _ => BuyBrush
            };
            var x = mappedMarker.X;
            var y = MapPriceY(marker.Price, rect, reference, maxDeviation);
            if (y < rect.Top || y > rect.Bottom) continue;
            dc.DrawEllipse(brush, new Pen(BackgroundBrush, 0.9), new Point(x, y), 3.6, 3.6);
            DrawText(dc, marker.Label, x + 6, Math.Max(rect.Top, y - 14), 10, brush, FontWeights.SemiBold);
        }
    }

    private void DrawTrainingMarkers(
        DrawingContext dc,
        IReadOnlyList<KLineCandle> bars,
        IReadOnlyList<MappedBar> mapped,
        Rect rect,
        decimal reference,
        decimal maxDeviation)
    {
        if (!ShowTrainingMarkers || bars.Count < 24 || mapped.Count == 0)
        {
            return;
        }

        var markers = TrainingTradeMarkerCalculator.Calculate(bars);
        if (markers.Count == 0)
        {
            return;
        }

        var mappedByIndex = mapped.ToDictionary(item => item.Index, item => item);
        foreach (var marker in markers)
        {
            if (!mappedByIndex.TryGetValue(marker.Index, out var mappedMarker))
            {
                continue;
            }

            var y = MapPriceY(marker.Price, rect, reference, maxDeviation);
            DrawTrainingMarkerText(dc, rect, marker, mappedMarker.X, y);
        }
    }

    private static void DrawTrainingMarkerText(
        DrawingContext dc,
        Rect rect,
        TrainingTradeMarker marker,
        double x,
        double priceY)
    {
        var isBuy = marker.MarkerType == "B";
        var brush = isBuy ? TrainingBuyBrush : TrainingSellBrush;
        var fontSize = 15 + (double)Math.Min(2m, marker.Strength - 1m);
        var formatted = CreateText(marker.MarkerType, fontSize, brush, FontWeights.Black);
        var y = isBuy
            ? Math.Clamp(priceY + 5, rect.Top + 2, rect.Bottom - formatted.Height - 2)
            : Math.Clamp(priceY - formatted.Height - 5, rect.Top + 2, rect.Bottom - formatted.Height - 2);
        var origin = new Point(x - formatted.Width / 2, y);
        var shadow = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0));
        shadow.Freeze();

        dc.DrawText(CreateText(marker.MarkerType, fontSize, shadow, FontWeights.Black), new Point(origin.X + 1, origin.Y + 1));
        dc.DrawText(formatted, origin);
    }

    private static void DrawLatestMarker(
        DrawingContext dc,
        KLineCandle last,
        double x,
        Rect rect,
        decimal axisReference,
        decimal maxDeviation,
        decimal changeReference)
    {
        var y = MapPriceY(last.Close, rect, axisReference, maxDeviation);
        var brush = ChangeBrush(last.Close, changeReference);
        dc.DrawEllipse(brush, null, new Point(x, y), 2.5, 2.5);
        dc.DrawLine(new Pen(brush, 0.6) { DashStyle = DashStyles.Dash }, new Point(x, y), new Point(rect.Right, y));
        dc.DrawRectangle(brush, null, new Rect(rect.Right + 2, y - 9, 55, 18));
        DrawText(dc, last.Close.ToString("F2"), rect.Right + 6, y - 7, 10, BackgroundBrush, FontWeights.SemiBold);
    }

    private void DrawCrosshair(DrawingContext dc, IReadOnlyList<KLineCandle> bars, IReadOnlyList<MappedBar> mapped,
        IReadOnlyList<decimal> vwap, Layout layout, decimal reference, decimal maxDeviation)
    {
        if (_mousePosition is not { } mouse || !new Rect(layout.Price.Left, layout.Price.Top, layout.Price.Width, layout.Macd.Bottom - layout.Price.Top).Contains(mouse))
            return;

        var mappedIndex = mapped.Select((item, i) => (Distance: Math.Abs(item.X - mouse.X), Index: i)).MinBy(item => item.Distance).Index;
        var barIndex = mapped[mappedIndex].Index;
        var x = mapped[mappedIndex].X;
        var y = MapPriceY(bars[barIndex].Close, layout.Price, reference, maxDeviation);
        var pen = new Pen(CrosshairBrush, CrosshairLineThickness) { DashStyle = DashStyles.Dash };
        dc.DrawLine(pen, new Point(x, layout.Price.Top), new Point(x, layout.Macd.Bottom));
        dc.DrawLine(pen, new Point(layout.Price.Left, y), new Point(layout.Price.Right, y));

        var bar = bars[barIndex];
        var dailyReference = GetDailyReference(bars, barIndex, reference);
        var change = dailyReference > 0 ? (bar.Close - dailyReference) / dailyReference * 100 : 0;
        var text = $"{bar.TradingTime:yyyy-MM-dd HH:mm}\n价格  {bar.Close:F2}    均价  {vwap[barIndex]:F2}\n涨跌  {change:+0.00;-0.00;0.00}%    成交量  {FormatLargeNumber(bar.Volume)}\n成交额  {FormatAmount(bar.Amount)}";
        const double tooltipWidth = 226;
        const double tooltipHeight = 78;
        var left = mouse.X > layout.Price.Left + layout.Price.Width / 2 ? layout.Price.Left + 8 : layout.Price.Right - tooltipWidth - 8;
        var top = layout.Price.Top + 8;
        dc.DrawRectangle(TooltipBrush, new Pen(TooltipBorderBrush, 0.9), new Rect(left, top, tooltipWidth, tooltipHeight));
        DrawText(dc, text, left + 9, top + 7, 11, PriceBrush);
    }

    private static int FindNearestBarIndex(IReadOnlyList<KLineCandle> bars, DateTime time)
    {
        if (bars.Count == 0) return -1;
        var sameDay = bars.Select((item, index) => (item, index)).Where(pair => pair.item.TradingTime.Date == time.Date).ToArray();
        return sameDay.Length == 0
            ? -1
            : sameDay.MinBy(pair => Math.Abs((pair.item.TradingTime - time).Ticks)).index;
    }

    private static decimal GetDailyReference(IReadOnlyList<KLineCandle> bars, int index, decimal fallback)
    {
        var date = bars[index].TradingTime.Date;
        for (var previous = index - 1; previous >= 0; previous--)
        {
            if (bars[previous].TradingTime.Date < date)
            {
                return bars[previous].Close;
            }
        }

        return fallback;
    }

    private static double MapPriceY(decimal value, Rect rect, decimal reference, decimal maximum)
        => maximum <= 0
            ? rect.Top + rect.Height / 2
            : rect.Top + (double)((reference + maximum - value) / (maximum * 2)) * rect.Height;

    private static double MapMacdY(decimal value, Rect rect, decimal maximum)
        => rect.Top + rect.Height / 2 - (double)(value / maximum) * (rect.Height * 0.44);

    private static bool IsMappedFiveDay(IReadOnlyList<MappedBar> mapped)
        => mapped.Select(item => item.Bar.TradingTime.Date).Distinct().Take(2).Count() > 1;

    private static Brush ChangeBrush(decimal value, decimal reference)
        => value > reference ? RisingBrush : value < reference ? FallingBrush : NeutralBrush;

    private static string FormatLargeNumber(decimal value)
        => value >= 100_000_000 ? $"{value / 100_000_000:F2}亿" : value >= 10_000 ? $"{value / 10_000:F2}万" : value.ToString("F0");

    private static string FormatAmount(decimal value) => FormatLargeNumber(value);

    private static Brush Brush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static void DrawCenteredText(DrawingContext dc, string text, double centerX, double y, double size, Brush brush)
    {
        var formatted = CreateText(text, size, brush, FontWeights.Normal);
        dc.DrawText(formatted, new Point(centerX - formatted.Width / 2, y));
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush, FontWeight? weight = null)
        => dc.DrawText(CreateText(text, size, brush, weight ?? FontWeights.Normal), new Point(x, y));

    private static FormattedText CreateText(string text, double size, Brush brush, FontWeight weight)
        => new(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, 1.0);

    private static void OnDataChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is IntradayChartControl control)
        {
            control._mousePosition = null;
            control.InvalidateVisual();
        }
    }

    private sealed record MappedBar(KLineCandle Bar, double X, int Index);
    private sealed record MacdValue(decimal Dif, decimal Dea, decimal Bar);
    private sealed record Layout(Rect Price, Rect Volume, Rect Macd);
}
