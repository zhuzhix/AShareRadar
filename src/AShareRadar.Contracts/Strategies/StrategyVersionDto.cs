namespace AShareRadar.Contracts.Strategies;

public sealed record StrategyVersionDto(
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

public sealed record StrategyHitVersionDto(
    Guid EventId,
    string StrategyCode,
    string StrategyVersionId,
    string Version,
    string ParameterJson,
    string RuleSummary,
    DateTimeOffset CreatedAt);
