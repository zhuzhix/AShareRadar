namespace AShareRadar.Domain.Strategies;

public enum StrategyRunMode
{
    Realtime = 0,
    HistoricalReplay = 1,
    Backtest = 2,
    Simulation = 3,
    Observation = 4
}
