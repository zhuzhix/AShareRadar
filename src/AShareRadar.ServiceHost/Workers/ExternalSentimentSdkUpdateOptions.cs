namespace AShareRadar.ServiceHost.Workers;

public sealed class ExternalSentimentSdkUpdateOptions
{
    public bool Enabled { get; set; }

    public int MinIntervalSeconds { get; set; } = 180;

    public int RequestTimeoutSeconds { get; set; } = 45;

    public string PythonPath { get; set; } = "";

    public string ScriptPath { get; set; } = "";

    public string OutputPath { get; set; } = "data/market-sentiment-external.csv";

    public string Token { get; set; } = "";

    public string TokenEnvironmentVariable { get; set; } = "EASTMONEY_QUANT_TOKEN";
}
