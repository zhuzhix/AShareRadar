using AShareRadar.Application.MarketData;
using AShareRadar.Domain.Opportunities;
using AShareRadar.Domain.Strategies;
using AShareRadar.Application.Opportunities.Storage;

namespace AShareRadar.Application.Opportunities;

public sealed class OpportunityAppService
{
    private const decimal MinimumSignalScore = 75m;
    private const decimal DuplicateScoreTolerance = 2m;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan WeakenAfter = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan DisappearAfter = TimeSpan.FromMinutes(18);

    private readonly object _gate = new();
    private readonly IOpportunityStateStore _stateStore;
    private readonly Dictionary<string, Opportunity> _opportunitiesBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SignalEvent> _events = [];
    private DateTimeOffset _lastSavedAt = DateTimeOffset.MinValue;
    private bool _deferredSaveScheduled;

    public OpportunityAppService(IOpportunityStateStore stateStore)
    {
        _stateStore = stateStore;
        RestoreState(_stateStore.Load());
    }

    public IReadOnlyList<Opportunity> GetTodayOpportunities()
    {
        lock (_gate)
        {
            return _opportunitiesBySymbol.Values
                .OrderByDescending(item => item.LastSeenTime)
                .ToArray();
        }
    }

    public IReadOnlyList<Opportunity> QueryOpportunities(string? view)
    {
        lock (_gate)
        {
            IEnumerable<Opportunity> query = _opportunitiesBySymbol.Values;
            var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);

            query = view switch
            {
                "Current" => query.Where(item => item.TradingDate == today && item.Status is not OpportunityStatus.GivenUp and not OpportunityStatus.Disappeared),
                "Focused" => query.Where(item => item.Status == OpportunityStatus.Focused || item.ManualTag == "Focus"),
                "Candidate" => query.Where(item => item.Status == OpportunityStatus.Candidate),
                "Watch" => query.Where(item => item.Status == OpportunityStatus.Watch || item.Status == OpportunityStatus.Weakened || item.ManualTag == "WaitPullback"),
                "GivenUp" => query.Where(item => item.Status == OpportunityStatus.GivenUp || item.ManualTag == "GiveUp"),
                "WaitPullback" => query.Where(item => item.ManualTag == "WaitPullback"),
                _ => query
            };

            return query
                .OrderBy(item => GetLayerOrder(item))
                .ThenByDescending(item => item.CurrentScore)
                .ThenByDescending(item => item.LastSeenTime)
                .ToArray();
        }
    }

    public IReadOnlyList<SignalEvent> GetRecentEvents(int count)
    {
        lock (_gate)
        {
            return _events
                .OrderByDescending(item => item.EventTime)
                .Take(count)
                .ToArray();
        }
    }

    public IReadOnlyList<SignalEvent> GetEventsForTradingDate(DateOnly tradingDate)
    {
        lock (_gate)
        {
            return _events
                .Where(item => DateOnly.FromDateTime(item.EventTime.LocalDateTime) == tradingDate)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.EventTime)
                .ToArray();
        }
    }

    public Opportunity? GetOpportunity(Guid id)
    {
        lock (_gate)
        {
            return _opportunitiesBySymbol.Values.FirstOrDefault(item => item.Id == id);
        }
    }

    public IReadOnlyList<SignalEvent> GetEventsForOpportunity(Guid opportunityId, int count)
    {
        lock (_gate)
        {
            return _events
                .Where(item => item.OpportunityId == opportunityId)
                .OrderByDescending(item => item.EventTime)
                .Take(count)
                .ToArray();
        }
    }

    public Opportunity? MarkOpportunity(Guid id, string decisionType, string? note)
    {
        lock (_gate)
        {
            var opportunity = _opportunitiesBySymbol.Values.FirstOrDefault(item => item.Id == id);
            if (opportunity is null)
            {
                return null;
            }

            var normalizedDecision = NormalizeDecisionType(decisionType);
            opportunity.Mark(normalizedDecision, note);
            SaveState(force: true);
            return opportunity;
        }
    }

    public OpportunityArchiveResult ArchiveOpportunitiesMissingEventDetails()
    {
        lock (_gate)
        {
            var eventOpportunityIds = _events
                .Select(item => item.OpportunityId)
                .ToHashSet();
            var archived = _opportunitiesBySymbol.Values
                .Where(item => !eventOpportunityIds.Contains(item.Id))
                .OrderByDescending(item => item.LastSeenTime)
                .Select(item => new ArchivedOpportunity(
                    item.Id,
                    item.TradingDate,
                    item.Symbol,
                    item.Name,
                    item.FirstSeenTime,
                    item.LastSeenTime,
                    item.Status.ToString(),
                    item.HitCount,
                    item.CurrentScore,
                    item.BestScore,
                    item.ManualTag,
                    item.Note))
                .ToArray();

            foreach (var item in archived)
            {
                _opportunitiesBySymbol.Remove(StockSymbolNormalizer.NormalizeCode(item.Symbol));
            }

            if (archived.Length > 0)
            {
                SaveState(force: true);
            }

            return new OpportunityArchiveResult(DateTimeOffset.Now, archived.Length, archived);
        }
    }

    public IReadOnlyList<SignalEvent> ApplyStrategySignals(
        Guid runId,
        DateOnly tradingDate,
        DateTimeOffset eventTime,
        IReadOnlyList<StrategySignal> strategySignals)
    {
        lock (_gate)
        {
            var signalEvents = new List<SignalEvent>();
            signalEvents.AddRange(ExpirePreviousTradingDayOpportunities(runId, tradingDate, eventTime));

            var currentHitSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var signalGroup in strategySignals
                         .Where(IsEligibleSignal)
                         .GroupBy(item => StockSymbolNormalizer.NormalizeCode(item.Symbol), StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(group => group.Max(item => item.Score)))
            {
                var orderedSignals = signalGroup
                    .OrderByDescending(item => item.Score)
                    .ToArray();
                var bestSignal = orderedSignals[0];
                var normalizedSymbol = StockSymbolNormalizer.NormalizeCode(bestSignal.Symbol);
                currentHitSymbols.Add(normalizedSymbol);
                var isNew = !_opportunitiesBySymbol.TryGetValue(normalizedSymbol, out var opportunity);
                opportunity ??= new Opportunity(
                    Guid.NewGuid(),
                    tradingDate,
                    normalizedSymbol,
                    bestSignal.Name,
                    eventTime);

                var eventType = ClassifySignalEventType(isNew, opportunity, bestSignal, eventTime);

                var strategyHits = orderedSignals
                    .Select(item => new StrategyHitDetail(
                        item.StrategyCode,
                        item.StrategyName,
                        item.Score,
                        item.Price,
                        item.Reason,
                        item.Risk,
                        item.Metrics,
                        item.Tags,
                        item.PassedConditions,
                        item.FailedConditions,
                        item.StopLossPrice,
                        item.TakeProfitPrice))
                    .ToArray();

                var signalEvent = new SignalEvent(
                    Guid.NewGuid(),
                    opportunity.Id,
                    runId,
                    eventTime,
                    eventType,
                    normalizedSymbol,
                    bestSignal.Name,
                    bestSignal.StrategyCode,
                    bestSignal.StrategyName,
                    bestSignal.Score,
                    bestSignal.Price,
                    BuildMergedReason(strategyHits),
                    BuildMergedRisk(strategyHits),
                    strategyHits);

                if (!isNew && IsDuplicateEvent(opportunity, signalEvent))
                {
                    opportunity.RefreshSignal(signalEvent);
                    _opportunitiesBySymbol[normalizedSymbol] = opportunity;
                    continue;
                }

                opportunity.ApplySignal(signalEvent);
                _opportunitiesBySymbol[normalizedSymbol] = opportunity;
                _events.Add(signalEvent);
                signalEvents.Add(signalEvent);
            }

            signalEvents.AddRange(ApplyMissingOpportunities(runId, tradingDate, eventTime, currentHitSymbols));

            SaveState(force: false);
            return signalEvents;
        }
    }

    private IReadOnlyList<SignalEvent> ExpirePreviousTradingDayOpportunities(
        Guid runId,
        DateOnly tradingDate,
        DateTimeOffset eventTime)
    {
        foreach (var opportunity in _opportunitiesBySymbol.Values.ToArray())
        {
            if (opportunity.TradingDate >= tradingDate ||
                opportunity.Status is OpportunityStatus.Disappeared or OpportunityStatus.GivenUp)
            {
                continue;
            }
            opportunity.MarkDisappeared(eventTime);
        }

        return [];
    }

    private IReadOnlyList<SignalEvent> ApplyMissingOpportunities(
        Guid runId,
        DateOnly tradingDate,
        DateTimeOffset eventTime,
        IReadOnlySet<string> currentHitSymbols)
    {
        foreach (var opportunity in _opportunitiesBySymbol.Values.ToArray())
        {
            if (opportunity.TradingDate != tradingDate ||
                currentHitSymbols.Contains(StockSymbolNormalizer.NormalizeCode(opportunity.Symbol)) ||
                opportunity.Status is OpportunityStatus.Disappeared or OpportunityStatus.GivenUp)
            {
                continue;
            }

            var missingDuration = eventTime - opportunity.LastSeenTime;
            if (missingDuration >= DisappearAfter)
            {
                opportunity.MarkDisappeared(eventTime);
            }
            else if (missingDuration >= WeakenAfter && opportunity.Status != OpportunityStatus.Weakened)
            {
                var decayedScore = Math.Round(opportunity.CurrentScore * 0.72m, 2);
                opportunity.MarkWeakened(decayedScore);
            }
        }

        return [];
    }

    private static bool IsEligibleSignal(StrategySignal signal)
    {
        if (signal.Score < MinimumSignalScore)
        {
            return false;
        }

        if (signal.Action == StrategySignalAction.Watch && signal.Confidence == StrategySignalConfidence.Low && signal.Score < 90m)
        {
            return false;
        }

        return signal.FailedConditions is null || signal.FailedConditions.Count <= 3 || signal.Score >= 95m;
    }

    private SignalEventType ClassifySignalEventType(
        bool isNew,
        Opportunity opportunity,
        StrategySignal bestSignal,
        DateTimeOffset eventTime)
    {
        if (isNew)
        {
            return SignalEventType.New;
        }

        if (opportunity.Status == OpportunityStatus.Disappeared ||
            eventTime - opportunity.LastSeenTime >= WeakenAfter)
        {
            return SignalEventType.ReHit;
        }

        if (bestSignal.Score >= opportunity.BestScore + DuplicateScoreTolerance)
        {
            return SignalEventType.Strengthened;
        }

        if (bestSignal.Score <= opportunity.CurrentScore - 8m)
        {
            return SignalEventType.Weakened;
        }

        return SignalEventType.Continued;
    }

    private bool IsDuplicateEvent(Opportunity opportunity, SignalEvent candidateEvent)
    {
        if (candidateEvent.EventType is SignalEventType.New or SignalEventType.ReHit or SignalEventType.Strengthened or SignalEventType.Weakened)
        {
            return false;
        }

        var lastEvent = _events
            .Where(item => item.OpportunityId == opportunity.Id)
            .OrderByDescending(item => item.EventTime)
            .FirstOrDefault();
        if (lastEvent is null || candidateEvent.EventTime - lastEvent.EventTime > DuplicateWindow)
        {
            return false;
        }

        var sameStrategies = string.Equals(
            BuildStrategyKey(lastEvent.StrategyHits),
            BuildStrategyKey(candidateEvent.StrategyHits),
            StringComparison.OrdinalIgnoreCase);
        return sameStrategies && Math.Abs(candidateEvent.Score - lastEvent.Score) < DuplicateScoreTolerance;
    }

    private static string BuildStrategyKey(IReadOnlyList<StrategyHitDetail> hits)
    {
        return string.Join("|", hits
            .Select(item => item.StrategyCode)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
    }


    private static int GetLayerOrder(Opportunity opportunity)
    {
        if (opportunity.ManualTag == "Focus" || opportunity.Status == OpportunityStatus.Focused)
        {
            return 0;
        }

        return opportunity.Status switch
        {
            OpportunityStatus.Candidate or OpportunityStatus.Strengthened or OpportunityStatus.ReHit => 1,
            OpportunityStatus.Watch or OpportunityStatus.Weakened or OpportunityStatus.New or OpportunityStatus.Continued => 2,
            OpportunityStatus.Disappeared => 3,
            OpportunityStatus.GivenUp => 4,
            _ => 2
        };
    }

    private void RestoreState(OpportunityState state)
    {
        foreach (var item in state.Opportunities)
        {
            if (!Enum.TryParse<OpportunityStatus>(item.Status, out var status))
            {
                status = OpportunityStatus.New;
            }

            status = NormalizeRestoredStatus(status, item.CurrentScore, item.HitCount, item.ManualTag);

            var opportunity = Opportunity.Restore(
                item.Id,
                item.TradingDate,
                item.Symbol,
                item.Name,
                item.FirstSeenTime,
                item.LastSeenTime,
                status,
                item.HitCount,
                item.CurrentScore,
                item.BestScore,
                item.ManualTag,
                item.Note);

            _opportunitiesBySymbol[StockSymbolNormalizer.NormalizeCode(opportunity.Symbol)] = opportunity;
        }

        foreach (var item in state.Events)
        {
            if (!Enum.TryParse<SignalEventType>(item.EventType, out var eventType))
            {
                eventType = SignalEventType.New;
            }

            _events.Add(new SignalEvent(
                item.Id,
                item.OpportunityId,
                item.RunId,
                item.EventTime,
                eventType,
                item.Symbol,
                item.Name,
                item.StrategyCode,
                item.StrategyName,
                item.Score,
                item.Price,
                item.Reason,
                item.Risk,
                item.StrategyHits
                    .Select(hit => new StrategyHitDetail(
                        hit.StrategyCode,
                        hit.StrategyName,
                        hit.Score,
                        hit.Price,
                        hit.Reason,
                        hit.Risk,
                        hit.Metrics,
                        hit.Tags,
                        hit.PassedConditions,
                        hit.FailedConditions,
                        hit.StopLossPrice,
                        hit.TakeProfitPrice))
                    .ToArray()));
        }
    }

    private void SaveState(bool force)
    {
        var now = DateTimeOffset.Now;
        if (!force && now - _lastSavedAt < TimeSpan.FromSeconds(3))
        {
            ScheduleDeferredSave();
            return;
        }

        var state = new OpportunityState(
            _opportunitiesBySymbol.Values
                .Select(item => new OpportunityStateItem(
                    item.Id,
                    item.TradingDate,
                    item.Symbol,
                    item.Name,
                    item.FirstSeenTime,
                    item.LastSeenTime,
                    item.Status.ToString(),
                    item.HitCount,
                    item.CurrentScore,
                    item.BestScore,
                    item.ManualTag,
                    item.Note))
                .ToArray(),
            _events
                .Select(item => new SignalEventStateItem(
                    item.Id,
                    item.OpportunityId,
                    item.RunId,
                    item.EventTime,
                    item.EventType.ToString(),
                    item.Symbol,
                    item.Name,
                    item.StrategyCode,
                    item.StrategyName,
                    item.Score,
                    item.Price,
                    item.Reason,
                    item.Risk,
                    item.StrategyHits
                        .Select(hit => new StrategyHitStateItem(
                            hit.StrategyCode,
                            hit.StrategyName,
                            hit.Score,
                            hit.Price,
                            hit.Reason,
                            hit.Risk,
                            hit.Metrics,
                            hit.Tags,
                            hit.PassedConditions,
                            hit.FailedConditions,
                            hit.StopLossPrice,
                            hit.TakeProfitPrice))
                        .ToArray()))
                .ToArray());

        _stateStore.Save(state);
        _lastSavedAt = now;
        _deferredSaveScheduled = false;
    }

    private void ScheduleDeferredSave()
    {
        if (_deferredSaveScheduled)
        {
            return;
        }

        _deferredSaveScheduled = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            lock (_gate)
            {
                SaveState(force: true);
            }
        });
    }

    private static string BuildMergedReason(IReadOnlyList<StrategyHitDetail> strategyHits)
    {
        if (strategyHits.Count == 1)
        {
            return strategyHits[0].Reason;
        }

        var strategyNames = string.Join(", ", strategyHits.Select(item => item.StrategyName));
        return $"Multi-strategy hit: {strategyNames}. Top reason: {strategyHits[0].Reason}";
    }

    private static string? BuildMergedRisk(IReadOnlyList<StrategyHitDetail> strategyHits)
    {
        var risks = strategyHits
            .Select(item => item.Risk)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return risks.Length == 0 ? null : string.Join(" | ", risks);
    }

    private static string NormalizeDecisionType(string decisionType)
    {
        return decisionType.Trim() switch
        {
            "Focus" => "Focus",
            "WaitPullback" => "WaitPullback",
            "GiveUp" => "GiveUp",
            "Watch" => "Watch",
            "PaperBuy" => "PaperBuy",
            _ => "Watch"
        };
    }

    private static OpportunityStatus NormalizeRestoredStatus(
        OpportunityStatus status,
        decimal currentScore,
        int hitCount,
        string? manualTag)
    {
        if (manualTag == "Focus" || status == OpportunityStatus.Focused)
        {
            return OpportunityStatus.Focused;
        }

        if (manualTag == "GiveUp" || status == OpportunityStatus.GivenUp)
        {
            return OpportunityStatus.GivenUp;
        }

        if (status == OpportunityStatus.Disappeared)
        {
            return OpportunityStatus.Disappeared;
        }

        if (manualTag is "Watch" or "WaitPullback" || status == OpportunityStatus.Weakened)
        {
            return OpportunityStatus.Watch;
        }

        if (currentScore >= 120m || (hitCount >= 2 && currentScore >= 105m))
        {
            return OpportunityStatus.Focused;
        }

        return currentScore >= 90m || hitCount >= 2
            ? OpportunityStatus.Candidate
            : OpportunityStatus.Watch;
    }
}

public sealed record OpportunityArchiveResult(
    DateTimeOffset ArchivedAt,
    int ArchivedCount,
    IReadOnlyList<ArchivedOpportunity> Opportunities);

public sealed record ArchivedOpportunity(
    Guid Id,
    DateOnly TradingDate,
    string Symbol,
    string Name,
    DateTimeOffset FirstSeenTime,
    DateTimeOffset LastSeenTime,
    string Status,
    int HitCount,
    decimal CurrentScore,
    decimal BestScore,
    string? ManualTag,
    string? Note);
