using System.Text.Json;
using AShareRadar.Infrastructure.MarketData;

namespace AShareRadar.ServiceHost.Services;

public sealed class StockNameMapSyncService
{
    private readonly EastMoneyQuantDotNetClient _client;
    private readonly ILogger<StockNameMapSyncService> _logger;

    public StockNameMapSyncService(EastMoneyQuantDotNetClient client, ILogger<StockNameMapSyncService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<string> SyncAsync(string dataDir, CancellationToken cancellationToken)
    {
        var names = await _client.LoadAshareInstrumentNamesAsync(cancellationToken);
        var path = Path.Combine(dataDir, "stock-name-map.json");
        var tempPath = path + ".tmp";
        Directory.CreateDirectory(dataDir);
        var json = JsonSerializer.Serialize(
            names.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tempPath, json, new System.Text.UTF8Encoding(false), cancellationToken);
        File.Move(tempPath, path, true);
        _logger.LogInformation("Canonical stock name map synchronized. Count={Count} Path={Path}", names.Count, path);
        return path;
    }
}