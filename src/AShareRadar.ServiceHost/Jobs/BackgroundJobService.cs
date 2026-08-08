using System.Text.Json;
using AShareRadar.Application.Jobs;

namespace AShareRadar.ServiceHost.Jobs;

public sealed class BackgroundJobService
{
    private readonly IBackgroundJobStore _store;
    private readonly BackgroundJobQueue _queue;

    public BackgroundJobService(IBackgroundJobStore store, BackgroundJobQueue queue)
    {
        _store = store;
        _queue = queue;
    }

    public BackgroundJob Enqueue(string type, string title, object payload)
    {
        var job = _store.Create(type, title, JsonSerializer.Serialize(payload));
        _queue.Enqueue(job.Id);
        return job;
    }

    public BackgroundJob? Get(Guid id) => _store.Get(id);

    public BackgroundJob? GetLatest(string? type) => _store.GetLatest(type);

    public IReadOnlyList<BackgroundJob> GetActive() => _store.GetActive();

    public IReadOnlyList<BackgroundJobLog> GetLogs(Guid id, int count) => _store.GetLogs(id, count);
}
