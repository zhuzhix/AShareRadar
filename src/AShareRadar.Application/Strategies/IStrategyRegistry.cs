namespace AShareRadar.Application.Strategies;

public interface IStrategyRegistry
{
    IReadOnlyList<ISignalStrategy> GetEnabledStrategies();
}
