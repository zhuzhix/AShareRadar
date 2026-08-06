namespace AShareRadar.Contracts.Opportunities;

public sealed record DecisionRequest(
    string DecisionType,
    string? Note);
