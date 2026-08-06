namespace AShareRadar.Infrastructure.MarketData;

public sealed class MarketDataOptions
{
    public string Provider { get; set; } = "Simulation";

    public string Universe { get; set; } = "Seed";

    public string StockPool { get; set; } = "AShare";

    public int MaxSymbols { get; set; } = 5000;

    public int RequestBatchSize { get; set; } = 80;

    public int RequestConcurrency { get; set; } = 6;

    public string SectorMappingPath { get; set; } = "data/sector-mapping.csv";

    public string ConceptMappingPath { get; set; } = "data/concept-mapping.csv";

    public string[] SeedSymbols { get; set; } =
    [
        "sh600000",
        "sz000001",
        "sz300059",
        "sh601318",
        "sz002415",
        "sz002230"
    ];

    public int RequestTimeoutSeconds { get; set; } = 8;
}
