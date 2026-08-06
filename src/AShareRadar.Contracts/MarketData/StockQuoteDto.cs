namespace AShareRadar.Contracts.MarketData;

public sealed record StockQuoteDto(
    string Symbol,
    string Name,
    decimal Price,
    decimal ChangePercent,
    decimal VolumeRatio,
    decimal TurnoverRate,
    decimal Amount,
    DateTimeOffset QuoteTime);
