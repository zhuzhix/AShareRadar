namespace AShareRadar.Contracts.MarketData;

public sealed record AuctionObservationDto(
    DateOnly TradingDate,
    DateOnly ReferenceTradeDate,
    string Symbol,
    string Name,
    int SourceRank,
    decimal SourceScore,
    string SourceStrategies,
    DateTimeOffset? LatestEventTime,
    string Phase,
    decimal? ReferencePrice,
    decimal? GapPercent,
    decimal Imbalance,
    decimal QueueDecay,
    decimal StrengthScore,
    decimal RiskScore,
    string Status,
    string OpenConfirmStatus,
    string Reason);

public sealed record AuctionObservationStatusDto(
    DateOnly TradingDate,
    DateOnly ReferenceTradeDate,
    string Phase,
    int MonitoringCount,
    DateTimeOffset? LastUpdated,
    string L2Status,
    string Message);
