namespace AShareRadar.Application.Review;

public sealed record PredictionReview(
    DateOnly SignalDate,
    DateOnly? VerifyDate,
    int PredictionCount,
    int UpPredictionCount,
    int VerifiedCount,
    int CloseSuccessCount,
    int IntradaySuccessCount,
    decimal? CloseSuccessRate,
    decimal? IntradaySuccessRate,
    decimal? AverageNextCloseReturn,
    string Message,
    IReadOnlyList<PredictionRecord> Records);

public sealed record PredictionRecord(
    Guid Id,
    DateOnly SignalDate,
    string Symbol,
    string Name,
    string StrategyCodes,
    string StrategyNames,
    int SignalCount,
    int StrategyHitCount,
    decimal Score,
    decimal BestScore,
    string PredictionDirection,
    decimal PredictionScore,
    string PredictionReason,
    string RiskNote,
    DateOnly? VerifyDate,
    decimal? NextOpenReturn,
    decimal? NextCloseReturn,
    decimal? NextHighReturn,
    decimal? NextLowReturn,
    bool? IsCloseSuccess,
    bool? IsIntradaySuccess,
    string VerifyStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset? VerifiedAt);

public interface IPredictionReviewStore
{
    IReadOnlyList<PredictionRecord> GetBySignalDate(DateOnly signalDate);

    void UpsertMany(IReadOnlyList<PredictionRecord> records);
}
