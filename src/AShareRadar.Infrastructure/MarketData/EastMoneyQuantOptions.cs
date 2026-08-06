namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyQuantOptions
{
    public bool Enabled { get; set; }

    public string PythonPath { get; set; } = "";

    public string RealtimeScriptPath { get; set; } = "";

    public string KLineScriptPath { get; set; } = "";

    public string DuckDbPath { get; set; } = "";

    public string Token { get; set; } = "";

    public string TokenEnvironmentVariable { get; set; } = "EASTMONEY_QUANT_TOKEN";

    public int RequestTimeoutSeconds { get; set; } = 45;

    public int BatchSize { get; set; } = 1000;

    public int SnapshotCacheSeconds { get; set; } = 60;

    public int KLineCacheSeconds { get; set; } = 30;
}

public sealed class EastMoneyQuantDotNetOptions
{
    public bool Enabled { get; set; } = true;

    public string Token { get; set; } = "";

    public string TokenEnvironmentVariable { get; set; } = "EASTMONEY_QUANT_TOKEN";

    public int RequestTimeoutSeconds { get; set; } = 45;

    public int BatchSize { get; set; } = 200;

    public int SnapshotCacheSeconds { get; set; } = 60;

    public int KLineCacheSeconds { get; set; } = 30;
}
