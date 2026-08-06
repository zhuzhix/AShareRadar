using AShareRadar.Application.Opportunities;

namespace AShareRadar.Application.Review;

public sealed class ReviewAppService
{
    private readonly OpportunityAppService _opportunityAppService;

    public ReviewAppService(OpportunityAppService opportunityAppService)
    {
        _opportunityAppService = opportunityAppService;
    }

    public TodayReview BuildTodayReview()
    {
        var opportunities = _opportunityAppService.GetTodayOpportunities();
        var events = _opportunityAppService.GetRecentEvents(500);
        var tradingDate = opportunities.Count > 0
            ? opportunities.Max(item => item.TradingDate)
            : DateOnly.FromDateTime(DateTime.Today);

        var strategyRows = events
            .SelectMany(item => item.StrategyHits)
            .GroupBy(item => item.StrategyName)
            .Select(group => new StrategyReview(
                group.Key,
                group.Count(),
                decimal.Round(group.Average(item => item.Score), 2)))
            .OrderByDescending(item => item.HitCount)
            .ToArray();

        var opportunityRows = opportunities
            .OrderByDescending(item => item.CurrentScore)
            .Select(item => new ReviewOpportunity(
                item.Symbol,
                item.Name,
                item.Status.ToString(),
                item.ManualTag,
                item.CurrentScore,
                item.HitCount,
                item.FirstSeenTime,
                item.LastSeenTime))
            .ToArray();

        return new TodayReview(
            tradingDate,
            opportunities.Count,
            opportunities.Count(item => item.ManualTag == "Focus" || item.Status.ToString() == "Focused"),
            opportunities.Count(item => item.ManualTag == "GiveUp" || item.Status.ToString() == "GivenUp"),
            opportunities.Count(item => item.ManualTag == "WaitPullback"),
            opportunities.Count == 0 ? 0 : decimal.Round(opportunities.Average(item => item.CurrentScore), 2),
            strategyRows,
            opportunityRows);
    }
}
