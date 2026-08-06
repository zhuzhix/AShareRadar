namespace AShareRadar.Domain.Monitoring;

public enum MarketStatus
{
    Unknown = 0,
    NonTradingDay = 1,
    BeforeOpen = 2,
    CallAuction = 3,
    Trading = 4,
    MiddayBreak = 5,
    Closed = 6
}
