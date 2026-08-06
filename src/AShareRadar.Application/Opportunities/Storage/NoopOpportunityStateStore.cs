namespace AShareRadar.Application.Opportunities.Storage;

public sealed class NoopOpportunityStateStore : IOpportunityStateStore
{
    public OpportunityState Load()
    {
        return new OpportunityState([], []);
    }

    public void Save(OpportunityState state)
    {
    }
}
