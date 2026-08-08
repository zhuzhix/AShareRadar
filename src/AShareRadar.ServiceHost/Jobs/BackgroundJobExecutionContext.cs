using AShareRadar.Application.Jobs;

namespace AShareRadar.ServiceHost.Jobs;

public sealed class BackgroundJobExecutionContext
{
    private readonly IBackgroundJobStore _store;

    public BackgroundJobExecutionContext(BackgroundJob job, IBackgroundJobStore store, CancellationToken cancellationToken)
    {
        Job = job;
        _store = store;
        CancellationToken = cancellationToken;
    }

    public BackgroundJob Job { get; }

    public CancellationToken CancellationToken { get; }

    public string CompletionStep { get; private set; } = "任务完成";

    public string? ResultJson { get; private set; }

    public void Progress(int percent, string step)
    {
        _store.UpdateProgress(Job.Id, percent, step);
    }

    public void Stdout(string message)
    {
        _store.AppendLog(Job.Id, "stdout", message);
    }

    public void Stderr(string message)
    {
        _store.AppendLog(Job.Id, "stderr", message);
    }

    public void Complete(string step, string? resultJson = null)
    {
        CompletionStep = step;
        ResultJson = resultJson;
    }
}

public interface IBackgroundJobHandler
{
    string Type { get; }

    Task ExecuteAsync(BackgroundJobExecutionContext context);
}
