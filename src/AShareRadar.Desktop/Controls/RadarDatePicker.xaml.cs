using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AShareRadar.Desktop.Controls;

public partial class RadarDatePicker : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(
            nameof(SelectedDate),
            typeof(DateTime?),
            typeof(RadarDatePicker),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDateChanged));

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(
            nameof(Placeholder),
            typeof(string),
            typeof(RadarDatePicker),
            new PropertyMetadata("选择日期", OnPlaceholderChanged));

    private readonly CultureInfo _culture = CultureInfo.GetCultureInfo("zh-CN");
    private DateTime _displayMonth;
    private bool _isMonthView;

    public RadarDatePicker()
    {
        InitializeComponent();
        _displayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        Loaded += (_, _) => Refresh();
    }

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    private static void OnSelectedDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RadarDatePicker picker)
        {
            return;
        }

        if (e.NewValue is DateTime selected)
        {
            picker._displayMonth = new DateTime(selected.Year, selected.Month, 1);
        }

        picker.Refresh();
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RadarDatePicker picker)
        {
            picker.RefreshInputText();
        }
    }

    private void InputChrome_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled)
        {
            return;
        }

        _displayMonth = SelectedDate.HasValue
            ? new DateTime(SelectedDate.Value.Year, SelectedDate.Value.Month, 1)
            : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _isMonthView = false;
        Refresh();
        CalendarPopup.IsOpen = true;
        e.Handled = true;
    }

    private void HeaderButton_Click(object sender, RoutedEventArgs e)
    {
        _isMonthView = !_isMonthView;
        Refresh();
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _isMonthView
            ? _displayMonth.AddYears(-1)
            : _displayMonth.AddMonths(-1);
        Refresh();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _isMonthView
            ? _displayMonth.AddYears(1)
            : _displayMonth.AddMonths(1);
        Refresh();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedDate = null;
        CalendarPopup.IsOpen = false;
    }

    private void TodayButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedDate = DateTime.Today;
        CalendarPopup.IsOpen = false;
    }

    private void YearButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int year)
        {
            return;
        }

        _displayMonth = new DateTime(year, _displayMonth.Month, 1);
        Refresh();
    }

    private void DayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DateTime date })
        {
            SelectedDate = date;
            CalendarPopup.IsOpen = false;
        }
    }

    private void MonthButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int month })
        {
            return;
        }

        _displayMonth = new DateTime(_displayMonth.Year, month, 1);
        _isMonthView = false;
        Refresh();
    }

    private void Refresh()
    {
        RefreshInputText();
        HeaderTextBlock.Text = $"{_displayMonth:yyyy年MM月}";
        DayView.Visibility = _isMonthView ? Visibility.Collapsed : Visibility.Visible;
        MonthView.Visibility = _isMonthView ? Visibility.Visible : Visibility.Collapsed;

        if (_isMonthView)
        {
            RefreshMonthView();
        }
        else
        {
            RefreshDayView();
        }
    }

    private void RefreshInputText()
    {
        DisplayTextBlock.Text = SelectedDate.HasValue
            ? SelectedDate.Value.ToString("yyyy-MM-dd", _culture)
            : Placeholder;
        DisplayTextBlock.Foreground = SelectedDate.HasValue
            ? ResourceBrush("TextBrush")
            : ResourceBrush("MutedTextBrush");
    }

    private void RefreshDayView()
    {
        DayGrid.Children.Clear();

        var firstDay = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
        var offset = ((int)firstDay.DayOfWeek + 6) % 7;
        var gridStart = firstDay.AddDays(-offset);

        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            DayGrid.Children.Add(CreateDayButton(date));
        }
    }

    private Button CreateDayButton(DateTime date)
    {
        var isSelected = SelectedDate.HasValue && SelectedDate.Value.Date == date.Date;
        var isToday = DateTime.Today == date.Date;
        var isCurrentMonth = date.Month == _displayMonth.Month;

        var border = new Border
        {
            Width = 42,
            Height = 42,
            Background = isSelected ? ResourceBrush("ComboItemHoverBrush") : Brushes.Transparent,
            BorderBrush = isToday && !isSelected ? ResourceBrush("TextBrush") : Brushes.Transparent,
            BorderThickness = isToday && !isSelected ? new Thickness(3) : new Thickness(0),
            CornerRadius = new CornerRadius(2),
            Child = new TextBlock
            {
                Text = date.Day.ToString(_culture),
                FontSize = 18,
                FontWeight = isSelected || isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isSelected
                    ? ResourceBrush("ComboTextBrush")
                    : isCurrentMonth
                        ? ResourceBrush("TextBrush")
                        : ResourceBrush("SubtleTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var button = new Button
        {
            Tag = date,
            Content = border,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
        };
        button.Click += DayButton_Click;
        return button;
    }

    private void RefreshMonthView()
    {
        var year = _displayMonth.Year;
        SetYearButton(YearMinus2Button, year - 2);
        SetYearButton(YearMinus1Button, year - 1);
        SetYearButton(CurrentYearButton, year);
        SetYearButton(YearPlus1Button, year + 1);
        SetYearButton(YearPlus2Button, year + 2);

        MonthGrid.Children.Clear();
        for (var month = 1; month <= 12; month++)
        {
            MonthGrid.Children.Add(CreateMonthButton(month));
        }
    }

    private void SetYearButton(Button button, int year)
    {
        button.Tag = year;
        button.Content = new TextBlock
        {
            Text = year.ToString(_culture),
            FontSize = 19,
            Foreground = ResourceBrush("TextBrush"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Button CreateMonthButton(int month)
    {
        var isSelected = month == _displayMonth.Month;

        var border = new Border
        {
            Width = 66,
            Height = 40,
            Background = isSelected ? ResourceBrush("ComboItemHoverBrush") : Brushes.Transparent,
            BorderBrush = isSelected ? ResourceBrush("TextBrush") : Brushes.Transparent,
            BorderThickness = isSelected ? new Thickness(3) : new Thickness(0),
            CornerRadius = new CornerRadius(2),
            Child = new TextBlock
            {
                Text = $"{month}月",
                FontSize = 18,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isSelected ? ResourceBrush("ComboTextBrush") : ResourceBrush("TextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var button = new Button
        {
            Tag = month,
            Content = border,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
        };
        button.Click += MonthButton_Click;
        return button;
    }

    private Brush ResourceBrush(string key)
    {
        return TryFindResource(key) as Brush ?? Brushes.Transparent;
    }
}
