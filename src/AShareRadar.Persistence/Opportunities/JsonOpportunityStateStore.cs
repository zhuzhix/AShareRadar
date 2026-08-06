using System.Text.Json;
using AShareRadar.Application.Opportunities.Storage;

namespace AShareRadar.Persistence.Opportunities;

public sealed class JsonOpportunityStateStore : IOpportunityStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    public JsonOpportunityStateStore()
    {
        _filePath = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "runtime",
            "opportunity-state.json");
    }

    public OpportunityState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                return new OpportunityState([], []);
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<OpportunityState>(json, JsonOptions)
                    ?? new OpportunityState([], []);
            }
            catch
            {
                return new OpportunityState([], []);
            }
        }
    }

    public void Save(OpportunityState state)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(tempPath, json);

            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
    }
}
