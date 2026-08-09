using AShareRadar.Application.MarketData;
using AShareRadar.Domain.Opportunities;

namespace AShareRadar.Application.Opportunities;

public sealed class SignalHeatContextService
{
    private const string SectorContextType = "sector";
    private const string ConceptContextType = "concept";

    private readonly ISignalHeatContextStore _store;

    public SignalHeatContextService(ISignalHeatContextStore store)
    {
        _store = store;
    }

    public void TrackSignalEvents(
        IReadOnlyList<SignalEvent> signalEvents,
        SectorHeatSnapshot sectorHeatSnapshot,
        ConceptHeatSnapshot conceptHeatSnapshot,
        string? heatSnapshotBatchId)
    {
        if (signalEvents.Count == 0)
        {
            return;
        }

        var sectorRanks = BuildSectorRanks(sectorHeatSnapshot);
        var conceptRanks = BuildConceptRanks(conceptHeatSnapshot);
        var createdAt = DateTimeOffset.Now;
        var contexts = new List<SignalHeatContext>();

        foreach (var signalEvent in signalEvents)
        {
            var normalizedSymbol = StockSymbolNormalizer.NormalizeCode(signalEvent.Symbol);
            if (TryResolveSectorHeat(signalEvent.Symbol, normalizedSymbol, sectorHeatSnapshot, out var sectorHeat))
            {
                contexts.Add(CreateSectorContext(
                    signalEvent,
                    normalizedSymbol,
                    sectorHeat,
                    sectorRanks.GetValueOrDefault(sectorHeat.SectorCode),
                    heatSnapshotBatchId,
                    createdAt));
            }

            foreach (var conceptHeat in ResolveConceptHeats(signalEvent.Symbol, normalizedSymbol, conceptHeatSnapshot)
                         .OrderBy(item => conceptRanks.GetValueOrDefault(item.ConceptCode, int.MaxValue))
                         .ThenByDescending(item => item.HeatScore))
            {
                contexts.Add(CreateConceptContext(
                    signalEvent,
                    normalizedSymbol,
                    conceptHeat,
                    conceptRanks.GetValueOrDefault(conceptHeat.ConceptCode),
                    heatSnapshotBatchId,
                    createdAt));
            }
        }

        if (contexts.Count > 0)
        {
            _store.SaveContexts(contexts);
        }
    }

    private static SignalHeatContext CreateSectorContext(
        SignalEvent signalEvent,
        string normalizedSymbol,
        SectorHeat heat,
        int heatRank,
        string? heatSnapshotBatchId,
        DateTimeOffset createdAt)
    {
        return new SignalHeatContext(
            signalEvent.Id,
            normalizedSymbol,
            signalEvent.EventTime,
            SectorContextType,
            heat.SectorCode,
            heat.SectorName,
            heatRank,
            heat.StockCount,
            heat.RisingCount,
            heat.AverageChangePercent,
            heat.RisingRatioPercent,
            heat.TotalAmount,
            heat.HeatScore,
            IsLeader(normalizedSymbol, heat.LeaderSymbols),
            heatSnapshotBatchId,
            createdAt);
    }

    private static SignalHeatContext CreateConceptContext(
        SignalEvent signalEvent,
        string normalizedSymbol,
        ConceptHeat heat,
        int heatRank,
        string? heatSnapshotBatchId,
        DateTimeOffset createdAt)
    {
        return new SignalHeatContext(
            signalEvent.Id,
            normalizedSymbol,
            signalEvent.EventTime,
            ConceptContextType,
            heat.ConceptCode,
            heat.ConceptName,
            heatRank,
            heat.StockCount,
            heat.RisingCount,
            heat.AverageChangePercent,
            heat.RisingRatioPercent,
            heat.TotalAmount,
            heat.HeatScore,
            IsLeader(normalizedSymbol, heat.LeaderSymbols),
            heatSnapshotBatchId,
            createdAt);
    }

    private static bool TryResolveSectorHeat(
        string symbol,
        string normalizedSymbol,
        SectorHeatSnapshot snapshot,
        out SectorHeat heat)
    {
        if (snapshot.HeatBySymbol.TryGetValue(symbol, out heat!))
        {
            return true;
        }

        return snapshot.HeatBySymbol.TryGetValue(normalizedSymbol, out heat!);
    }

    private static IReadOnlyList<ConceptHeat> ResolveConceptHeats(
        string symbol,
        string normalizedSymbol,
        ConceptHeatSnapshot snapshot)
    {
        if (snapshot.HeatBySymbol.TryGetValue(symbol, out var heats))
        {
            return heats;
        }

        return snapshot.HeatBySymbol.TryGetValue(normalizedSymbol, out heats)
            ? heats
            : [];
    }

    private static IReadOnlyDictionary<string, int> BuildSectorRanks(SectorHeatSnapshot snapshot)
    {
        return snapshot.SectorsByCode.Values
            .OrderByDescending(item => item.HeatScore)
            .ThenByDescending(item => item.TotalAmount)
            .ThenBy(item => item.SectorName, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => new { item.SectorCode, Rank = index + 1 })
            .ToDictionary(item => item.SectorCode, item => item.Rank, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, int> BuildConceptRanks(ConceptHeatSnapshot snapshot)
    {
        return snapshot.ConceptsByCode.Values
            .OrderByDescending(item => item.HeatScore)
            .ThenByDescending(item => item.TotalAmount)
            .ThenBy(item => item.ConceptName, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => new { item.ConceptCode, Rank = index + 1 })
            .ToDictionary(item => item.ConceptCode, item => item.Rank, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLeader(string normalizedSymbol, IReadOnlyList<string> leaderSymbols)
    {
        return leaderSymbols.Any(item =>
            string.Equals(StockSymbolNormalizer.NormalizeCode(item), normalizedSymbol, StringComparison.OrdinalIgnoreCase));
    }
}

public interface ISignalHeatContextStore
{
    void SaveContexts(IReadOnlyList<SignalHeatContext> contexts);

    IReadOnlyList<SignalHeatContext> GetByEventId(Guid eventId);

    IReadOnlyDictionary<Guid, IReadOnlyList<SignalHeatContext>> GetByEventIds(IEnumerable<Guid> eventIds);
}

public sealed record SignalHeatContext(
    Guid EventId,
    string Symbol,
    DateTimeOffset EventTime,
    string ContextType,
    string Code,
    string Name,
    int HeatRank,
    int StockCount,
    int RisingCount,
    decimal AverageChangePercent,
    decimal RisingRatioPercent,
    decimal TotalAmount,
    decimal HeatScore,
    bool IsLeader,
    string? HeatSnapshotBatchId,
    DateTimeOffset CreatedAt);
