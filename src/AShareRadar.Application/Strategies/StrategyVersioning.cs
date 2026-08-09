using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AShareRadar.Domain.Opportunities;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Application.Strategies;

public sealed class StrategyVersionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IStrategyRegistry _strategyRegistry;
    private readonly IStrategyVersionStore _store;

    public StrategyVersionService(
        IStrategyRegistry strategyRegistry,
        IStrategyVersionStore store)
    {
        _strategyRegistry = strategyRegistry;
        _store = store;
    }

    public IReadOnlyList<StrategyVersion> SyncCurrentVersions()
    {
        return _strategyRegistry.GetEnabledStrategies()
            .Select(strategy => _store.UpsertActiveVersion(CreateVersion(strategy.Definition)))
            .ToArray();
    }

    public IReadOnlyList<StrategyVersion> QueryVersions(string? strategyCode = null)
    {
        SyncCurrentVersions();
        return _store.QueryVersions(strategyCode);
    }

    public IReadOnlyList<StrategyHitVersion> GetHitVersions(Guid eventId)
    {
        return _store.GetHitVersions(eventId);
    }

    public void TrackSignalEvents(IReadOnlyList<SignalEvent> signalEvents)
    {
        if (signalEvents.Count == 0)
        {
            return;
        }

        var definitions = _strategyRegistry.GetEnabledStrategies()
            .Select(strategy => strategy.Definition)
            .GroupBy(definition => definition.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var versionsByCode = definitions.Values
            .Select(CreateVersion)
            .Select(_store.UpsertActiveVersion)
            .ToDictionary(item => item.StrategyCode, item => item, StringComparer.OrdinalIgnoreCase);

        var createdAt = DateTimeOffset.Now;
        var hitVersions = new List<StrategyHitVersion>();
        foreach (var signalEvent in signalEvents)
        {
            foreach (var hit in signalEvent.StrategyHits)
            {
                if (!versionsByCode.TryGetValue(hit.StrategyCode, out var version))
                {
                    continue;
                }

                hitVersions.Add(new StrategyHitVersion(
                    signalEvent.Id,
                    hit.StrategyCode,
                    version.Id,
                    version.Version,
                    version.ParameterJson,
                    version.RuleSummary,
                    createdAt));
            }
        }

        if (hitVersions.Count > 0)
        {
            _store.SaveHitVersions(hitVersions);
        }
    }

    private static StrategyVersion CreateVersion(StrategyDefinition definition)
    {
        var parameterJson = JsonSerializer.Serialize(
            definition.Parameters.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
            JsonOptions);
        var dataRequirementJson = JsonSerializer.Serialize(definition.DataRequirement, JsonOptions);
        var hashInput = string.Join(
            "|",
            definition.Code,
            definition.Name,
            definition.Type,
            definition.Stage,
            definition.DefaultAction,
            parameterJson,
            dataRequirementJson,
            definition.Description);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant()[..12];
        var version = $"v2026.08.09-code-{hash[..8]}";
        var now = DateTimeOffset.Now;

        return new StrategyVersion(
            Guid.NewGuid().ToString("N"),
            definition.Code,
            definition.Name,
            version,
            "Active",
            definition.Description,
            parameterJson,
            dataRequirementJson,
            hash,
            now,
            now,
            null,
            "code-baseline");
    }
}

public interface IStrategyVersionStore
{
    StrategyVersion UpsertActiveVersion(StrategyVersion version);

    IReadOnlyList<StrategyVersion> QueryVersions(string? strategyCode = null);

    void SaveHitVersions(IReadOnlyList<StrategyHitVersion> hitVersions);

    IReadOnlyList<StrategyHitVersion> GetHitVersions(Guid eventId);
}

public sealed record StrategyVersion(
    string Id,
    string StrategyCode,
    string StrategyName,
    string Version,
    string Status,
    string RuleSummary,
    string ParameterJson,
    string DataRequirementJson,
    string DefinitionHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? DeactivatedAt,
    string Source);

public sealed record StrategyHitVersion(
    Guid EventId,
    string StrategyCode,
    string StrategyVersionId,
    string Version,
    string ParameterJson,
    string RuleSummary,
    DateTimeOffset CreatedAt);
