using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AShareRadar.Desktop.Services;

public static class ThemeService
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AShareRadar");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "theme.txt");

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static AppTheme LoadSavedTheme()
    {
        try
        {
            if (File.Exists(SettingsPath)
                && Enum.TryParse<AppTheme>(File.ReadAllText(SettingsPath).Trim(), ignoreCase: true, out var saved))
            {
                return saved;
            }
        }
        catch
        {
            // Ignore corrupt preference files; fall back to the product default.
        }

        return AppTheme.Dark;
    }

    public static AppTheme Toggle()
    {
        var next = CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        Apply(next, save: true);
        return next;
    }

    public static void Apply(AppTheme theme, bool save)
    {
        CurrentTheme = theme;
        var resources = Application.Current.Resources;

        if (theme == AppTheme.Dark)
        {
            Set(resources, "PageBackgroundBrush", "#080D19");
            Set(resources, "PanelBrush", "#0D1424");
            Set(resources, "SoftPanelBrush", "#121C32");
            Set(resources, "CardBrush", "#17233B");
            Set(resources, "CardDarkBrush", "#090E19");
            Set(resources, "BorderBrushSoft", "#243451");
            Set(resources, "TextBrush", "#F8FAFC");
            Set(resources, "MutedTextBrush", "#8EA6CF");
            Set(resources, "SubtleTextBrush", "#59719A");
            Set(resources, "PrimaryBrush", "#F6C409");
            Set(resources, "PrimarySoftBrush", "#2F2B13");
            Set(resources, "AccentBlueBrush", "#4F7BFF");
            Set(resources, "SuccessBrush", "#00D6B0");
            Set(resources, "WarningBrush", "#FF9A3D");
            Set(resources, "DangerBrush", "#FF6575");
            Set(resources, "TableRowBrush", "#101A2E");
            Set(resources, "TableAltRowBrush", "#17213A");
            Set(resources, "TableHoverBrush", "#1E2C4E");
            Set(resources, "TableSelectedBrush", "#233257");
            Set(resources, "InputBrush", "#090E19");
            Set(resources, "InputHoverBrush", "#111B2D");
            Set(resources, "ButtonSurfaceBrush", "#121C32");
            Set(resources, "ButtonHoverBrush", "#1B2946");
            Set(resources, "ComboBackgroundBrush", "#0B1322");
            Set(resources, "ComboHoverBrush", "#111B2D");
            Set(resources, "ComboItemHoverBrush", "#858583");
            Set(resources, "ComboTextBrush", "#FFFFFF");
            Set(resources, "SelectedTextOnPrimaryBrush", "#0B1322");
            Set(resources, "ScrollBarTrackBrush", "#0B1322");
            Set(resources, "ScrollBarThumbBrush", "#8A8D92");
            Set(resources, "ScrollBarThumbHoverBrush", "#A1A4AA");
            Set(resources, "ScrollBarArrowBrush", "#8A8D92");
        }
        else
        {
            Set(resources, "PageBackgroundBrush", "#EEF2F7");
            Set(resources, "PanelBrush", "#FFFFFF");
            Set(resources, "SoftPanelBrush", "#F6F8FB");
            Set(resources, "CardBrush", "#FFFFFF");
            Set(resources, "CardDarkBrush", "#F8FBFF");
            Set(resources, "BorderBrushSoft", "#DDE6F2");
            Set(resources, "TextBrush", "#0F1F38");
            Set(resources, "MutedTextBrush", "#66758A");
            Set(resources, "SubtleTextBrush", "#9AA6B6");
            Set(resources, "PrimaryBrush", "#176BFF");
            Set(resources, "PrimarySoftBrush", "#EAF2FF");
            Set(resources, "AccentBlueBrush", "#2563EB");
            Set(resources, "SuccessBrush", "#078C6B");
            Set(resources, "WarningBrush", "#C56A00");
            Set(resources, "DangerBrush", "#D92D20");
            Set(resources, "TableRowBrush", "#FFFFFF");
            Set(resources, "TableAltRowBrush", "#F7FAFF");
            Set(resources, "TableHoverBrush", "#FAFCFF");
            Set(resources, "TableSelectedBrush", "#EEF3FB");
            Set(resources, "InputBrush", "#FFFFFF");
            Set(resources, "InputHoverBrush", "#F8FBFF");
            Set(resources, "ButtonSurfaceBrush", "#F4F7FB");
            Set(resources, "ButtonHoverBrush", "#E9EFF8");
            Set(resources, "ComboBackgroundBrush", "#FFFFFF");
            Set(resources, "ComboHoverBrush", "#F8FBFF");
            Set(resources, "ComboItemHoverBrush", "#E8EEF8");
            Set(resources, "ComboTextBrush", "#0F1F38");
            Set(resources, "SelectedTextOnPrimaryBrush", "#FFFFFF");
            Set(resources, "ScrollBarTrackBrush", "#F7F9FC");
            Set(resources, "ScrollBarThumbBrush", "#8A8D92");
            Set(resources, "ScrollBarThumbHoverBrush", "#6F7378");
            Set(resources, "ScrollBarArrowBrush", "#8A8D92");
        }

        if (save)
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, theme.ToString());
        }
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
