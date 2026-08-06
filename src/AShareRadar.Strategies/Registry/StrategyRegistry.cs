using AShareRadar.Application.Strategies;

namespace AShareRadar.Strategies.Registry;

public sealed class StrategyRegistry : IStrategyRegistry
{
    private readonly IReadOnlyList<ISignalStrategy> _strategies;

    public StrategyRegistry(IEnumerable<ISignalStrategy> strategies)
    {
        _strategies = strategies.ToArray();
    }

    public IReadOnlyList<ISignalStrategy> GetEnabledStrategies()
    {
        return _strategies;
    }
}
