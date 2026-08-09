using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace AShareRadar.Desktop;

public sealed class MappingWebViewWindow : Window
{
    private readonly TextBlock _statusText;
    private readonly TextBox _addressText;

    public MappingWebViewWindow()
    {
        Title = "概念行业更新 - 东方财富 WebView2";
        Width = 1180;
        Height = 760;
        MinWidth = 920;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(10, 15, 28));

        Browser = new WebView2();
        _statusText = new TextBlock
        {
            Text = "正在打开东方财富页面...",
            Foreground = new SolidColorBrush(Color.FromRgb(204, 214, 235)),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        _addressText = new TextBox
        {
            Text = "当前地址：等待页面初始化...",
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(142, 162, 196)),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var headerContent = new Grid();
        headerContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        headerContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_statusText, 0);
        Grid.SetRow(_addressText, 1);
        headerContent.Children.Add(_statusText);
        headerContent.Children.Add(_addressText);

        var header = new Border
        {
            Height = 68,
            Padding = new Thickness(14, 6, 14, 6),
            Background = new SolidColorBrush(Color.FromRgb(16, 25, 46)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(37, 52, 85)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerContent
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(Browser);
        Content = root;
    }

    public WebView2 Browser { get; }

    public void SetStatus(string text)
    {
        _statusText.Text = text;
    }

    public void SetAddress(string? address)
    {
        _addressText.Text = $"当前地址：{(string.IsNullOrWhiteSpace(address) ? "about:blank" : address)}";
        _addressText.ToolTip = address;
        _addressText.CaretIndex = 0;
        _addressText.ScrollToHorizontalOffset(0);
    }
}
