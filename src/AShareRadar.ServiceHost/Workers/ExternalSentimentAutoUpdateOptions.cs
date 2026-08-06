namespace AShareRadar.ServiceHost.Workers;

public sealed class ExternalSentimentAutoUpdateOptions
{
    public bool Enabled { get; set; }

    public bool RunOnStartup { get; set; }

    public int IntervalMinutes { get; set; } = 60;

    public string RunAfterTime { get; set; } = "16:30";

    public int RequestTimeoutSeconds { get; set; } = 12;

    public string DataPath { get; set; } = "data/market-sentiment-external.csv";

    public ExternalSentimentSourceRule[] Sources { get; set; } = [];
}

public sealed class ExternalSentimentSourceRule
{
    public bool Enabled { get; set; } = true;

    public string Code { get; set; } = "";

    public string Url { get; set; } = "";

    public string ValuePattern { get; set; } = "";

    public decimal Scale { get; set; } = 1m;
}
