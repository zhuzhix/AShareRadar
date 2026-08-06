namespace AShareRadar.Contracts.MarketData;

public sealed record HeatLeaderDto(
    int Rank,
    string Symbol,
    string Name,
    decimal ChangePercent,
    decimal Amount,
    decimal VolumeRatio);
