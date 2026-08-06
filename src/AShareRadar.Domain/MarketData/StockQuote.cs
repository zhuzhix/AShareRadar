namespace AShareRadar.Domain.MarketData;

public sealed record StockQuote(
    string Symbol,
    string Name,
    decimal Price,
    decimal ChangePercent,
    decimal VolumeRatio,
    decimal TurnoverRate,
    decimal Amount,
    DateTimeOffset QuoteTime,
    decimal Open = 0,
    decimal High = 0,
    decimal Low = 0,
    decimal Volume = 0);
