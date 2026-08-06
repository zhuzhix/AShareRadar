namespace AShareRadar.Application.StrategyTraining;

public sealed class StrategyParameterProfileService
{
    private readonly IStrategyParameterProfileStore _store;

    public StrategyParameterProfileService(IStrategyParameterProfileStore store)
    {
        _store = store;
    }

    public IReadOnlyList<StrategyParameterProfile> GetProfiles(string? strategyCode)
    {
        return _store.GetProfiles(strategyCode);
    }

    public IReadOnlyDictionary<string, string> GetActiveParameters(string strategyCode)
    {
        return _store.GetActiveProfile(strategyCode)?.Parameters
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public StrategyParameterProfile SaveProfile(SaveStrategyParameterProfileCommand command)
    {
        var parameters = BuildParameters(command);
        var profile = new StrategyParameterProfile(
            Guid.NewGuid(),
            command.StrategyCode.Trim(),
            string.IsNullOrWhiteSpace(command.ProfileName)
                ? $"{command.StrategyCode} training {DateTimeOffset.Now:yyyyMMdd-HHmm}"
                : command.ProfileName.Trim(),
            command.SourceTrainingRunId,
            parameters,
            command.SampleCount,
            command.SuccessRate,
            command.AverageNextHighReturn,
            command.AverageNextCloseReturn,
            IsActive: false,
            DateTimeOffset.Now,
            ActivatedAt: null);

        _store.SaveProfile(profile);
        return profile;
    }

    public StrategyParameterProfile? Activate(Guid id)
    {
        return _store.Activate(id);
    }

    public void Deactivate(string strategyCode)
    {
        _store.Deactivate(strategyCode);
    }

    private static IReadOnlyDictionary<string, string> BuildParameters(SaveStrategyParameterProfileCommand command)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["min_score"] = command.MinScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            ["min_amount"] = (command.MinAmountYi * 100_000_000m).ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
            ["min_amount_yi"] = command.MinAmountYi.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            ["min_relative_strength_percent"] = command.MinRelativeStrengthPercent.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            ["min_sector_heat_score"] = command.MinHeatScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            ["min_heat_score"] = command.MinHeatScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            ["max_result_count"] = command.MaxOutputPerDay.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }
}

public interface IStrategyParameterProfileStore
{
    IReadOnlyList<StrategyParameterProfile> GetProfiles(string? strategyCode);

    StrategyParameterProfile? GetActiveProfile(string strategyCode);

    void SaveProfile(StrategyParameterProfile profile);

    StrategyParameterProfile? Activate(Guid id);

    void Deactivate(string strategyCode);
}

public sealed record SaveStrategyParameterProfileCommand(
    string StrategyCode,
    string ProfileName,
    Guid? SourceTrainingRunId,
    decimal MinScore,
    decimal MinAmountYi,
    decimal MinRelativeStrengthPercent,
    decimal MinHeatScore,
    int MaxOutputPerDay,
    int SampleCount,
    decimal? SuccessRate,
    decimal? AverageNextHighReturn,
    decimal? AverageNextCloseReturn);

public sealed record StrategyParameterProfile(
    Guid Id,
    string StrategyCode,
    string ProfileName,
    Guid? SourceTrainingRunId,
    IReadOnlyDictionary<string, string> Parameters,
    int SampleCount,
    decimal? SuccessRate,
    decimal? AverageNextHighReturn,
    decimal? AverageNextCloseReturn,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt);
