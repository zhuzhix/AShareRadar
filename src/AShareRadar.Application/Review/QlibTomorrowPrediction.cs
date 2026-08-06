namespace AShareRadar.Application.Review;

public sealed record QlibTomorrowPrediction(
    DateOnly SignalDate,
    string Symbol,
    string Name,
    decimal UpProbability,
    decimal DownProbability,
    string Direction,
    string Confidence,
    decimal RawScore,
    bool? Executable,
    string? BlockReason);

public sealed record QlibNextDayPredictionRunResult(
    string OutputDirectory,
    IReadOnlyList<QlibTomorrowPrediction> Predictions);
