namespace AShareRadar.Contracts.MarketData;

public sealed record MarketSnapshotDto(
    DateTimeOffset SnapshotTime,
    string ProviderName,
    long ElapsedMilliseconds,
    IReadOnlyList<StockQuoteDto> Quotes);
