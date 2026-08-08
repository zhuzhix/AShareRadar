namespace AShareRadar.Desktop.Controls;

public sealed record TrainingTradeMarker(
    int Index,
    DateTime TradingTime,
    decimal Price,
    string MarkerType,
    decimal Strength,
    string Reason);
