using AShareRadar.Domain.Strategies;

namespace AShareRadar.Application.Strategies;

public interface ISignalStrategy
{
    string Code { get; }

    string Name { get; }

    StrategyType Type { get; }

    StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.TriggerConfirmation,
        StrategySignalAction.Candidate,
        new StrategyDataRequirement(
            RequiresRealtimeQuote: true,
            RequiresDailyKLine: false,
            RequiresMinuteKLine: false,
            RequiresSectorData: false,
            RequiresCapitalFlow: false,
            MinDailyBarCount: 0),
        new Dictionary<string, string>(),
        Name);

    Task<IReadOnlyList<StrategySignal>> EvaluateAsync(
        StrategyContext context,
        CancellationToken cancellationToken);
}
