namespace AShareRadar.Domain.Opportunities;

public sealed class Opportunity
{
    public Opportunity(
        Guid id,
        DateOnly tradingDate,
        string symbol,
        string name,
        DateTimeOffset firstSeenTime)
    {
        Id = id;
        TradingDate = tradingDate;
        Symbol = symbol;
        Name = name;
        FirstSeenTime = firstSeenTime;
        LastSeenTime = firstSeenTime;
        Status = OpportunityStatus.New;
    }

    public Guid Id { get; }

    public DateOnly TradingDate { get; }

    public string Symbol { get; }

    public string Name { get; private set; }

    public DateTimeOffset FirstSeenTime { get; }

    public DateTimeOffset LastSeenTime { get; private set; }

    public OpportunityStatus Status { get; private set; }

    public int HitCount { get; private set; }

    public decimal CurrentScore { get; private set; }

    public decimal BestScore { get; private set; }

    public string? ManualTag { get; private set; }

    public string? Note { get; private set; }

    public static Opportunity Restore(
        Guid id,
        DateOnly tradingDate,
        string symbol,
        string name,
        DateTimeOffset firstSeenTime,
        DateTimeOffset lastSeenTime,
        OpportunityStatus status,
        int hitCount,
        decimal currentScore,
        decimal bestScore,
        string? manualTag,
        string? note)
    {
        return new Opportunity(id, tradingDate, symbol, name, firstSeenTime)
        {
            LastSeenTime = lastSeenTime,
            Status = status,
            HitCount = hitCount,
            CurrentScore = currentScore,
            BestScore = bestScore,
            ManualTag = manualTag,
            Note = note
        };
    }

    public void ApplySignal(SignalEvent signalEvent)
    {
        Name = signalEvent.Name;
        LastSeenTime = signalEvent.EventTime;
        CurrentScore = signalEvent.Score;
        BestScore = Math.Max(BestScore, signalEvent.Score);

        if (signalEvent.EventType is SignalEventType.New or SignalEventType.Continued or SignalEventType.ReHit or SignalEventType.Strengthened)
        {
            HitCount++;
        }

        Status = signalEvent.EventType switch
        {
            SignalEventType.New or SignalEventType.Continued or SignalEventType.ReHit or SignalEventType.Strengthened => ClassifyLayer(signalEvent),
            SignalEventType.Weakened => OpportunityStatus.Weakened,
            SignalEventType.Disappeared => OpportunityStatus.Disappeared,
            SignalEventType.ManualMarked => Status,
            _ => Status
        };
    }

    public void RefreshSignal(SignalEvent signalEvent)
    {
        Name = signalEvent.Name;
        LastSeenTime = signalEvent.EventTime;
        CurrentScore = signalEvent.Score;
        BestScore = Math.Max(BestScore, signalEvent.Score);
        if (ManualTag is not "Focus" and not "GiveUp")
        {
            Status = ClassifyLayer(signalEvent);
        }
    }

    public void Mark(string manualTag, string? note)
    {
        ManualTag = manualTag;
        Note = note;

        Status = manualTag switch
        {
            "Focus" => OpportunityStatus.Focused,
            "GiveUp" => OpportunityStatus.GivenUp,
            "Watch" or "WaitPullback" => OpportunityStatus.Watch,
            _ => Status
        };
    }

    public void MarkWeakened(decimal decayedScore)
    {
        CurrentScore = Math.Max(0m, decayedScore);
        if (ManualTag is not "Focus" and not "GiveUp")
        {
            Status = OpportunityStatus.Weakened;
        }
    }

    public void MarkDisappeared(DateTimeOffset eventTime)
    {
        LastSeenTime = eventTime;
        CurrentScore = 0m;
        if (ManualTag != "GiveUp")
        {
            Status = OpportunityStatus.Disappeared;
        }
    }

    private OpportunityStatus ClassifyLayer(SignalEvent signalEvent)
    {
        if (ManualTag == "Focus" || signalEvent.Score >= 120m || (signalEvent.StrategyHits.Count >= 2 && signalEvent.Score >= 105m))
        {
            return OpportunityStatus.Focused;
        }

        if (ManualTag == "WaitPullback")
        {
            return OpportunityStatus.Watch;
        }

        return signalEvent.Score >= 90m || signalEvent.StrategyHits.Count >= 2
            ? OpportunityStatus.Candidate
            : OpportunityStatus.Watch;
    }
}
