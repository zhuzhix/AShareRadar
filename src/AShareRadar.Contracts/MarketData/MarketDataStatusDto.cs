namespace AShareRadar.Contracts.MarketData;

public sealed record MarketDataStatusDto(
    string ConfiguredProvider,
    string ActiveProvider,
    string Universe,
    string StockPool,
    int MaxSymbols,
    int RequestBatchSize,
    int RequestConcurrency,
    IReadOnlyList<string> SeedSymbols,
    int RequestTimeoutSeconds);
