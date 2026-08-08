using AShareRadar.Application.Jobs;

namespace AShareRadar.ServiceHost.Jobs;

public sealed class BackgroundJobWorker : BackgroundService
{
    private readonly BackgroundJobQueue _queue;
    private readonly IBackgroundJobStore _store;
    private readonly IReadOnlyDictionary<string, IBackgroundJobHandler> _handlers;
    private readonly ILogger<BackgroundJobWorker> _logger;

    public BackgroundJobWorker(
        BackgroundJobQueue queue,
        IBackgroundJobStore store,
        IEnumerable<IBackgroundJobHandler> handlers,
        ILogger<BackgroundJobWorker> logger)
    {
        _queue = queue;
        _store = store;
        _handlers = handlers.ToDictionary(item => item.Type, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var queued in _store.GetQueued(20))
        {
            _queue.Enqueue(queued.Id);
        }

        await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var job = _store.Get(jobId);
            if (job is null || !string.Equals(job.Status, "Queued", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_handlers.TryGetValue(job.Type, out var handler))
            {
                _store.MarkFailed(job.Id, "未找到任务处理器", $"Unsupported job type: {job.Type}", "检查服务端是否已部署最新版本。");
                continue;
            }

            try
            {
                _logger.LogInformation("Background job started. Id={JobId} Type={JobType}", job.Id, job.Type);
                _store.MarkRunning(job.Id, "开始执行");
                var context = new BackgroundJobExecutionContext(job, _store, stoppingToken);
                await handler.ExecuteAsync(context);
                _store.MarkSucceeded(job.Id, context.CompletionStep, context.ResultJson);
                _logger.LogInformation("Background job completed. Id={JobId} Type={JobType}", job.Id, job.Type);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _store.MarkFailed(job.Id, "服务停止", "任务因服务停止被取消。", "重新启动应用后再次执行该任务。");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background job failed. Id={JobId} Type={JobType}", job.Id, job.Type);
                _store.AppendLog(job.Id, "stderr", ex.ToString());
                _store.MarkFailed(job.Id, "执行失败", ex.Message, BuildFixSuggestion(ex));
            }
        }
    }

    private static string BuildFixSuggestion(Exception ex)
    {
        var message = ex.ToString();
        if (message.Contains("FileNotFoundException", StringComparison.OrdinalIgnoreCase))
        {
            return "检查脚本路径、Python路径、数据目录是否存在；如果刚更新安装包，先运行 doctor。";
        }

        if (message.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase))
        {
            return "检查对应 Python 运行环境是否安装依赖包，尤其是 qlib、pandas、duckdb、gm。";
        }

        if (message.Contains("token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "检查东财 SDK token 是否有效，并确认 secrets.json 已更新。";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "任务超时。可以减少股票池、提高超时时间，或检查脚本是否卡在数据下载/训练阶段。";
        }

        if (message.Contains("tomorrow_predictions.csv", StringComparison.OrdinalIgnoreCase))
        {
            return "检查 Qlib 实验是否生成 tomorrow_predictions.csv，确认 SignalDate 和输出目录一致。";
        }

        if (message.Contains("locked", StringComparison.OrdinalIgnoreCase))
        {
            return "数据库被占用。关闭重复运行的服务或稍后重试。";
        }

        return "查看 stderr/stdout 末尾日志；优先检查脚本路径、运行环境、数据目录和网络/SDK token。";
    }
}
