namespace AShareRadar.Application.History;

public interface IHistoryQueryService
{
    IReadOnlyList<HistoricalSignalItem> QuerySignals(HistoricalSignalQuery query);

    IReadOnlyList<StrategyPerformanceItem> QueryStrategyPerformance(DateOnly? tradingDate, int count);
}
