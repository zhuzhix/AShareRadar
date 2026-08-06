namespace AShareRadar.ServiceHost.Workers;

public sealed class MarketMappingUpdateOptions
{
    public bool Enabled { get; set; } = true;

    public string PythonPath { get; set; } = "";

    public string ScriptPath { get; set; } = "";

    public string OutputDataDir { get; set; } = "data";

    public int Limit { get; set; }

    public double SleepSeconds { get; set; } = 0.03;

    public bool IncludeDynamicConcepts { get; set; }
}
