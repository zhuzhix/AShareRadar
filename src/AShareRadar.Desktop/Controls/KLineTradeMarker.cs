namespace AShareRadar.Desktop.Controls;

public sealed record KLineTradeMarker(
    DateTime TradingTime,
    decimal Price,
    string MarkerType,
    string Label);
