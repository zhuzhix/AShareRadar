namespace AShareRadar.Persistence.Database;

public sealed class DatabaseOptions
{
    public string StateStore { get; set; } = "SQLite";

    public string SqlitePath { get; set; } = "data/runtime/ashare-radar.sqlite";

    public string DuckDbPath { get; set; } = "data/runtime/ashare-radar.duckdb";
}
