namespace AShareRadar.Contracts.Opportunities;

public sealed record OpportunityDetailDto(
    OpportunityDto Opportunity,
    SignalEventDto? LatestEvent,
    IReadOnlyList<SignalEventDto> RecentEvents);
