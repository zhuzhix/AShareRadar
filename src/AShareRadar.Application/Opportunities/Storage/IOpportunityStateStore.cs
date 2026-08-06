namespace AShareRadar.Application.Opportunities.Storage;

public interface IOpportunityStateStore
{
    OpportunityState Load();

    void Save(OpportunityState state);
}
