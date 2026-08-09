using System.Diagnostics;
using System.Text;
using AShareRadar.Application.MarketData;
using AShareRadar.Contracts.MarketData;

namespace AShareRadar.ServiceHost.Services;

public sealed class MarketMappingSyncService
{
    private readonly ISectorHeatService _sectorHeatService;
    private readonly IHeatSnapshotStore _heatSnapshotStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<MarketMappingSyncService> _logger;

    public MarketMappingSyncService(
        ISectorHeatService sectorHeatService,
        IHeatSnapshotStore heatSnapshotStore,
        ILogger<MarketMappingSyncService> logger)
    {
        _sectorHeatService = sectorHeatService;
        _heatSnapshotStore = heatSnapshotStore;
        _logger = logger;
    }

    public async Task<MarketMappingSyncResult> SyncAsync(MarketMappingSyncRequest request, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = request.Version });
        var waitStopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Waiting for mapping synchronization lock. TraceId={TraceId}", request.Version);
        await _gate.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _logger.LogInformation(
                "Mapping sync started. TraceId={TraceId} UpdatedAt={UpdatedAt} SectorInput={SectorInput} ConceptInput={ConceptInput} LockWaitMs={LockWaitMs}",
                request.Version,
                request.UpdatedAt,
                request.SectorMappings.Count,
                request.ConceptMappings.Count,
                waitStopwatch.ElapsedMilliseconds);
            var sectors = Normalize(request.SectorMappings, singlePerSymbol: true);
            var concepts = Normalize(request.ConceptMappings, singlePerSymbol: false);
            _logger.LogInformation(
                "Mapping normalization completed. TraceId={TraceId} SectorValid={SectorValid} SectorRejectedOrDuplicate={SectorRejectedOrDuplicate} ConceptValid={ConceptValid} ConceptRejectedOrDuplicate={ConceptRejectedOrDuplicate}",
                request.Version,
                sectors.Count,
                request.SectorMappings.Count - sectors.Count,
                concepts.Count,
                request.ConceptMappings.Count - concepts.Count);
            if (sectors.Count == 0 || concepts.Count == 0)
            {
                _logger.LogWarning("Mapping sync rejected empty normalized data. TraceId={TraceId} SectorRows={SectorRows} ConceptRows={ConceptRows}", request.Version, sectors.Count, concepts.Count);
                return new(false, request.Version, sectors.Count, concepts.Count, "映射数据为空。", "行业和概念映射都必须非空。");
            }

            var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            var sectorPath = Path.Combine(dataDir, "sector-mapping.csv");
            var conceptPath = Path.Combine(dataDir, "concept-mapping.csv");
            var sectorTempPath = sectorPath + ".tmp";
            var conceptTempPath = conceptPath + ".tmp";
            _logger.LogInformation(
                "Writing mapping temporary files. TraceId={TraceId} SectorTempPath={SectorTempPath} ConceptTempPath={ConceptTempPath}",
                request.Version,
                sectorTempPath,
                conceptTempPath);
            WriteCsv(sectorTempPath, sectors, "sector", request.UpdatedAt);
            WriteCsv(conceptTempPath, concepts, "concept", request.UpdatedAt);
            _logger.LogInformation(
                "Mapping temporary files written. TraceId={TraceId} SectorBytes={SectorBytes} ConceptBytes={ConceptBytes}",
                request.Version,
                new FileInfo(sectorTempPath).Length,
                new FileInfo(conceptTempPath).Length);

            _logger.LogInformation("Replacing mapping files. TraceId={TraceId} SectorPath={SectorPath} ConceptPath={ConceptPath}", request.Version, sectorPath, conceptPath);
            File.Move(sectorTempPath, sectorPath, true);
            File.Move(conceptTempPath, conceptPath, true);
            _logger.LogInformation("Mapping files replaced. TraceId={TraceId}", request.Version);

            _sectorHeatService.ReloadMappings();
            var sectorBatch = _heatSnapshotStore.SaveMappingSnapshot(
                "sector",
                request.UpdatedAt,
                "EastMoney-WebView2",
                BuildMappingSnapshotItems(sectors));
            var conceptBatch = _heatSnapshotStore.SaveMappingSnapshot(
                "concept",
                request.UpdatedAt,
                "EastMoney-WebView2",
                BuildMappingSnapshotItems(concepts));
            var sectorStatus = _sectorHeatService.GetMappingStatus();
            var conceptStatus = _sectorHeatService.GetConceptMappingStatus();
            _logger.LogInformation(
                "Mapping sync completed. TraceId={TraceId} SectorRows={SectorRows} ConceptRows={ConceptRows} LoadedSectorRows={LoadedSectorRows} LoadedConceptRows={LoadedConceptRows} SectorMode={SectorMode} ConceptMode={ConceptMode} SectorBatchId={SectorBatchId} ConceptBatchId={ConceptBatchId} ElapsedMs={ElapsedMs}",
                request.Version,
                sectors.Count,
                concepts.Count,
                sectorStatus.MappingCount,
                conceptStatus.MappingCount,
                sectorStatus.Source,
                conceptStatus.Source,
                sectorBatch.Id,
                conceptBatch.Id,
                stopwatch.ElapsedMilliseconds);
            return new(true, request.Version, sectors.Count, concepts.Count, "行业概念映射已更新。");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Mapping sync canceled. TraceId={TraceId} ElapsedMs={ElapsedMs}", request.Version, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mapping sync failed. TraceId={TraceId} ElapsedMs={ElapsedMs}", request.Version, stopwatch.ElapsedMilliseconds);
            return new(false, request.Version, 0, 0, "行业概念映射更新失败。", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<MarketMappingRowDto> Normalize(IEnumerable<MarketMappingRowDto> rows, bool singlePerSymbol)
    {
        var valid = rows.Where(row => row.Symbol.Length == 6 && row.Symbol.All(char.IsDigit)
            && !string.IsNullOrWhiteSpace(row.Code) && !string.IsNullOrWhiteSpace(row.Name));
        return (singlePerSymbol
            ? valid.GroupBy(row => row.Symbol).Select(group => group.First())
            : valid.GroupBy(row => (row.Symbol, row.Code)).Select(group => group.First())).ToList();
    }

    private static void WriteCsv(string path, IEnumerable<MarketMappingRowDto> rows, string kind, DateTimeOffset updatedAt)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine($"symbol,{kind}_code,{kind}_name,source,updated_at");
        foreach (var row in rows)
            writer.WriteLine($"{row.Symbol},{Escape(row.Code)},{Escape(row.Name)},EastMoney-WebView2,{updatedAt:yyyy-MM-dd HH:mm:ss}");
    }

    private static IReadOnlyList<MappingSnapshotItem> BuildMappingSnapshotItems(IReadOnlyList<MarketMappingRowDto> rows)
    {
        var boardRanks = rows
            .Select(row => (row.Code, row.Name))
            .Distinct()
            .Select((board, index) => new { board.Code, board.Name, Rank = index + 1 })
            .ToDictionary(item => (item.Code, item.Name), item => item.Rank);

        return rows
            .Select(row => new MappingSnapshotItem(
                row.Code,
                row.Name,
                boardRanks.TryGetValue((row.Code, row.Name), out var rank) ? rank : 0,
                row.Symbol,
                null,
                "EastMoney-WebView2"))
            .ToArray();
    }

    private static string Escape(string value) => value.Contains(',') || value.Contains('"')
        ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
