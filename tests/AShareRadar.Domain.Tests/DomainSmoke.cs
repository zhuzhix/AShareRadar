using AShareRadar.Domain.Opportunities;

namespace AShareRadar.Domain.Tests;

internal static class DomainSmoke
{
    public static void Main()
    {
        var opportunity = new Opportunity(
            Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.Today),
            "600000",
            "Sample",
            DateTimeOffset.Now);

        if (opportunity.Status != OpportunityStatus.New)
        {
            throw new InvalidOperationException("Opportunity should start as New.");
        }
    }
}
