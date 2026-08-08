using AShareRadar.Domain.Opportunities;

namespace AShareRadar.Application.Review;

public sealed class LongTermTrackingService
{
    private static readonly HashSet<string> ExcludedStrategyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "main-sector-resonance",
        "main-sector-gap-recovery",
        "qlib-r013"
    };

    private readonly ILongTermTrackingStore _store;

    public LongTermTrackingService(ILongTermTrackingStore store)
    {
        _store = store;
    }

    public void TrackSignalEvents(IReadOnlyList<SignalEvent> signalEvents)
    {
        var signals = signalEvents
            .SelectMany(BuildTrackableSignals)
            .ToArray();

        if (signals.Length > 0)
        {
            _store.UpsertSignals(signals);
        }
    }

    public LongTermTrackingBackfillResult Backfill()
    {
        return _store.Backfill();
    }

    public LongTermTrackingQueryResult Query(LongTermTrackingQuery query)
    {
        return _store.Query(query);
    }

    public IReadOnlyList<string> GetActiveTrackingSymbols(int count)
    {
        return _store.GetActiveTrackingSymbols(count);
    }

    public IReadOnlyList<LongTermTrackingTimelineItem> QueryTimeline(string symbol, int count)
    {
        return _store.QueryTimeline(symbol, count);
    }

    public LongTermTrackingItem? UpdateStatus(Guid id, string status)
    {
        return _store.UpdateStatus(id, NormalizeStatus(status));
    }

    public LongTermTrackingItem? UpdateNote(Guid id, string? note)
    {
        return _store.UpdateNote(id, note);
    }

    private static IEnumerable<LongTermTrackingSignal> BuildTrackableSignals(SignalEvent signalEvent)
    {
        if (signalEvent.StrategyHits.Count == 0)
        {
            if (IsTrackableStrategy(signalEvent.StrategyCode, signalEvent.StrategyName))
            {
                yield return new LongTermTrackingSignal(
                    signalEvent.Id,
                    signalEvent.EventTime,
                    signalEvent.Symbol,
                    signalEvent.Name,
                    signalEvent.StrategyCode,
                    signalEvent.StrategyName,
                    signalEvent.Score,
                    signalEvent.Price,
                    signalEvent.Reason,
                    signalEvent.Risk);
            }

            yield break;
        }

        foreach (var hit in signalEvent.StrategyHits)
        {
            if (!IsTrackableStrategy(hit.StrategyCode, hit.StrategyName))
            {
                continue;
            }

            yield return new LongTermTrackingSignal(
                signalEvent.Id,
                signalEvent.EventTime,
                signalEvent.Symbol,
                signalEvent.Name,
                hit.StrategyCode,
                hit.StrategyName,
                hit.Score,
                hit.Price ?? signalEvent.Price,
                hit.Reason,
                hit.Risk);
        }
    }

    public static bool IsTrackableStrategy(string strategyCode, string strategyName)
    {
        if (ExcludedStrategyCodes.Contains(strategyCode))
        {
            return false;
        }

        return !ContainsLowSpark(strategyCode) && !ContainsLowSpark(strategyName);
    }

    private static bool ContainsLowSpark(string value)
    {
        return value.Contains("low-spark", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("low_spark", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("低位星火", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStatus(string status)
    {
        return status.Trim() switch
        {
            "Focus" or "重点" => "Focus",
            "Watch" or "观察" => "Watch",
            "GiveUp" or "放弃" => "GiveUp",
            "Archived" or "归档" => "Archived",
            _ => "Watch"
        };
    }
}

public interface ILongTermTrackingStore
{
    void UpsertSignals(IReadOnlyList<LongTermTrackingSignal> signals);

    LongTermTrackingBackfillResult Backfill();

    LongTermTrackingQueryResult Query(LongTermTrackingQuery query);

    IReadOnlyList<string> GetActiveTrackingSymbols(int count);

    IReadOnlyList<LongTermTrackingTimelineItem> QueryTimeline(string symbol, int count);

    LongTermTrackingItem? UpdateStatus(Guid id, string status);

    LongTermTrackingItem? UpdateNote(Guid id, string? note);
}

public sealed record LongTermTrackingSignal(
    Guid EventId,
    DateTimeOffset HitTime,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    string Reason,
    string? Risk);

public sealed record LongTermTrackingQuery(
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    string? Symbol = null,
    string? StrategyCode = null,
    string? Status = null,
    string SortBy = "LastHitAt",
    bool Descending = true,
    int Count = 500);

public sealed record LongTermTrackingQueryResult(
    int TotalCount,
    DateTimeOffset? LastHitAt,
    IReadOnlyList<LongTermTrackingItem> Items);

public sealed record LongTermTrackingBackfillResult(
    DateTimeOffset BackfilledAt,
    int ItemCount,
    int EventCount);

public sealed record LongTermTrackingItem(
    Guid Id,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    DateTimeOffset FirstHitAt,
    DateTimeOffset LastHitAt,
    int HitCount,
    decimal? LatestPrice,
    decimal LatestScore,
    decimal BestScore,
    string LatestReason,
    string? LatestRisk,
    string Status,
    int ManualPriority,
    string? Note,
    string? Tags,
    Guid? LatestEventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LongTermTrackingTimelineItem(
    Guid EventId,
    DateTimeOffset EventTime,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    string Reason,
    string? Risk);
