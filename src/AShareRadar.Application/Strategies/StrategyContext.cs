using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Application.Strategies;

public sealed record StrategyContext(
    Guid RunId,
    DateOnly TradingDate,
    MarketSnapshot Snapshot,
    StrategyRunMode RunMode = StrategyRunMode.Realtime,
    IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>? DailyBarsBySymbol = null,
    IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>? WeeklyBarsBySymbol = null,
    IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>? MinuteBarsBySymbol = null,
    IReadOnlyDictionary<string, IReadOnlyList<KLineBar>>? ThirtyMinuteBarsBySymbol = null,
    SectorHeatSnapshot? SectorHeatSnapshot = null,
    ConceptHeatSnapshot? ConceptHeatSnapshot = null,
    MarketSentimentSnapshot? MarketSentiment = null,
    StrategyMarketStats? MarketStats = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record StrategyMarketStats(
    decimal AverageChangePercent,
    decimal RisingRatioPercent,
    decimal FallingRatioPercent,
    decimal TotalAmount);
