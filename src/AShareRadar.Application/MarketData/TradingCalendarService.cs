using System.Text.Json;

namespace AShareRadar.Application.MarketData;

public sealed class TradingCalendarOptions
{
    public string CalendarPath { get; set; } = "";

    public IReadOnlyList<DateOnly> Holidays { get; set; } = [];

    public IReadOnlyList<DateOnly> ExtraTradingDays { get; set; } = [];
}

public sealed class TradingCalendarService
{
    private readonly HashSet<DateOnly> _holidays;
    private readonly HashSet<DateOnly> _extraTradingDays;
    private readonly string _source;
    private readonly string? _loadError;

    public TradingCalendarService(TradingCalendarOptions options)
    {
        var holidays = options.Holidays.ToList();
        var extraTradingDays = options.ExtraTradingDays.ToList();
        var source = "Configuration";
        string? loadError = null;
        if (!string.IsNullOrWhiteSpace(options.CalendarPath))
        {
            try
            {
                var path = ResolvePath(options.CalendarPath);
                if (File.Exists(path))
                {
                    var file = JsonSerializer.Deserialize<TradingCalendarFile>(
                        File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (file is not null)
                    {
                        holidays.AddRange(file.Holidays);
                        extraTradingDays.AddRange(file.ExtraTradingDays);
                        source = path;
                    }
                }
                else
                {
                    loadError = $"交易日历文件不存在：{path}";
                }
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
            }
        }

        _holidays = holidays.ToHashSet();
        _extraTradingDays = extraTradingDays.ToHashSet();
        _source = source;
        _loadError = loadError;
    }

    public bool IsTradingDay(DateOnly date)
    {
        if (_extraTradingDays.Contains(date))
        {
            return true;
        }

        if (_holidays.Contains(date))
        {
            return false;
        }

        return date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
    }

    public DateOnly GetPreviousTradingDate(DateOnly date)
    {
        var previous = date.AddDays(-1);
        while (!IsTradingDay(previous))
        {
            previous = previous.AddDays(-1);
        }

        return previous;
    }

    public TradingCalendarStatus GetStatus()
    {
        return new TradingCalendarStatus(
            _source,
            _holidays.Count,
            _extraTradingDays.Count,
            _loadError is null ? "Available" : "Degraded",
            _loadError);
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }
}

public sealed record TradingCalendarStatus(
    string Source,
    int HolidayCount,
    int ExtraTradingDayCount,
    string Status,
    string? Error);

public sealed record TradingCalendarFile(
    IReadOnlyList<DateOnly> Holidays,
    IReadOnlyList<DateOnly> ExtraTradingDays);
