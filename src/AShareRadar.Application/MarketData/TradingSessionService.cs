using AShareRadar.Domain.Monitoring;

namespace AShareRadar.Application.MarketData;

public sealed class TradingSessionService
{
    private readonly TradingCalendarService _tradingCalendarService;
    private readonly TradingSessionOptions _options;

    public TradingSessionService(
        TradingCalendarService tradingCalendarService,
        TradingSessionOptions options)
    {
        _tradingCalendarService = tradingCalendarService;
        _options = options;
    }

    public MarketStatus GetMarketStatus(DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        if (!_tradingCalendarService.IsTradingDay(date))
        {
            return MarketStatus.NonTradingDay;
        }

        var time = TimeOnly.FromDateTime(now.LocalDateTime);
        var callAuctionStart = ParseTime(_options.CallAuctionStartTime, new TimeOnly(9, 15));
        var morningStart = ParseTime(_options.MorningStartTime, new TimeOnly(9, 30));
        var morningEnd = ParseTime(_options.MorningEndTime, new TimeOnly(11, 30));
        var afternoonStart = ParseTime(_options.AfternoonStartTime, new TimeOnly(13, 0));
        var afternoonEnd = ParseTime(_options.AfternoonEndTime, new TimeOnly(15, 0));

        if (time < callAuctionStart)
        {
            return MarketStatus.BeforeOpen;
        }

        if (time < morningStart)
        {
            return MarketStatus.CallAuction;
        }

        if (time < morningEnd || time >= afternoonStart && time < afternoonEnd)
        {
            return MarketStatus.Trading;
        }

        return time < afternoonStart
            ? MarketStatus.MiddayBreak
            : MarketStatus.Closed;
    }

    public bool IsTradingSession(DateTimeOffset now)
    {
        return GetMarketStatus(now) == MarketStatus.Trading;
    }

    public DateOnly GetLatestCompletedTradingDate(DateTimeOffset now, TimeOnly dailyDataReadyTime)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var localTime = TimeOnly.FromDateTime(now.LocalDateTime);
        if (_tradingCalendarService.IsTradingDay(today) && localTime >= dailyDataReadyTime)
        {
            return today;
        }

        return _tradingCalendarService.GetPreviousTradingDate(today);
    }

    private static TimeOnly ParseTime(string value, TimeOnly fallback)
    {
        return TimeOnly.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
