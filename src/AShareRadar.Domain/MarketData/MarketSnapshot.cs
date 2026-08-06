namespace AShareRadar.Domain.MarketData;

public sealed record MarketSnapshot(
    DateTimeOffset SnapshotTime,
    string ProviderName,
    IReadOnlyList<StockQuote> Quotes);
