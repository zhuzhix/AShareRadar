using System.Text.Json;
using AShareRadar.Application.MarketData;
using AShareRadar.Application.Review;
using AShareRadar.Domain.MarketData;
using AShareRadar.ServiceHost.Workers;

namespace AShareRadar.ServiceHost.Jobs;

public sealed class HistoryDataUpdateJobHandler : IBackgroundJobHandler
{
    private readonly HistoricalDataUpdateService _service;

    public HistoryDataUpdateJobHandler(HistoricalDataUpdateService service)
    {
        _service = service;
    }

    public string Type => "history-data-update";

    public async Task ExecuteAsync(BackgroundJobExecutionContext context)
    {
        context.Progress(5, "启动历史更新脚本");
        var ok = await _service.RunManualJobAsync(
            context.CancellationToken,
            (line, isError) =>
            {
                if (isError)
                {
                    context.Stderr(line);
                }
                else
                {
                    context.Stdout(line);
                }

                if (line.Contains("missing_dates=", StringComparison.OrdinalIgnoreCase))
                {
                    context.Progress(20, "检查缺失交易日");
                }
                else if (line.Contains("weekly", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("周", StringComparison.OrdinalIgnoreCase))
                {
                    context.Progress(75, "构建周线数据");
                }
                else
                {
                    context.Progress(50, "更新历史数据");
                }
            });

        if (!ok)
        {
            throw new InvalidOperationException("历史更新脚本返回失败。");
        }

        context.Complete("历史数据更新完成");
    }
}

public sealed class NextDayPredictionJobHandler : IBackgroundJobHandler
{
    private readonly PredictionReviewService _predictionReviewService;

    public NextDayPredictionJobHandler(PredictionReviewService predictionReviewService)
    {
        _predictionReviewService = predictionReviewService;
    }

    public string Type => "next-day-prediction";

    public async Task ExecuteAsync(BackgroundJobExecutionContext context)
    {
        var payload = JsonSerializer.Deserialize<NextDayPredictionPayload>(context.Job.PayloadJson)
            ?? new NextDayPredictionPayload(DateOnly.FromDateTime(DateTime.Today));
        context.Progress(5, $"准备 {payload.SignalDate:yyyy-MM-dd} 的历史命中股票池");
        var review = await _predictionReviewService.GenerateAsync(
            payload.SignalDate,
            context.CancellationToken,
            (line, isError) =>
            {
                if (isError)
                {
                    context.Stderr(line);
                }
                else
                {
                    context.Stdout(line);
                }

                if (line.Contains("Running Qlib", StringComparison.OrdinalIgnoreCase))
                {
                    context.Progress(25, "运行 Qlib 明日预测实验");
                }
                else if (line.Contains("Done", StringComparison.OrdinalIgnoreCase))
                {
                    context.Progress(90, "读取 tomorrow_predictions.csv");
                }
            });

        var result = JsonSerializer.Serialize(new
        {
            review.SignalDate,
            review.PredictionCount,
            review.UpPredictionCount,
            review.Message
        });
        context.Complete($"次日预测完成，生成 {review.PredictionCount} 条", result);
    }

    private sealed record NextDayPredictionPayload(DateOnly SignalDate);
}

public sealed class ThirtyMinuteKLineUpdateJobHandler : IBackgroundJobHandler
{
    private readonly ThirtyMinuteKLineCacheWorkerOptions _options;
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IKLineDataProvider _kLineDataProvider;

    public ThirtyMinuteKLineUpdateJobHandler(
        ThirtyMinuteKLineCacheWorkerOptions options,
        IMarketDataProvider marketDataProvider,
        IKLineDataProvider kLineDataProvider)
    {
        _options = options;
        _marketDataProvider = marketDataProvider;
        _kLineDataProvider = kLineDataProvider;
    }

    public string Type => "m30-kline-update";

    public async Task ExecuteAsync(BackgroundJobExecutionContext context)
    {
        context.Progress(5, "读取实时行情快照");
        var snapshot = await _marketDataProvider.LoadMarketSnapshotAsync(context.CancellationToken);
        var candidateSymbols = BuildCandidateSymbols(snapshot);
        context.Stdout($"m30 candidates={candidateSymbols.Length}");
        if (candidateSymbols.Length == 0)
        {
            context.Complete("30分钟K更新完成：无候选股票");
            return;
        }

        var count = Math.Clamp(_options.BarCount, 40, 1200);
        if (_kLineDataProvider is IBatchKLineDataProvider batchProvider)
        {
            context.Progress(20, $"批量下载30分钟K：{candidateSymbols.Length}只");
            var bars = await batchProvider.LoadKLinesAsync(candidateSymbols, "m30", count, context.CancellationToken);
            context.Stdout($"m30 loaded={bars.Count}/{candidateSymbols.Length}");
            context.Complete($"30分钟K更新完成：{bars.Count}/{candidateSymbols.Length}只");
            return;
        }

        var loaded = 0;
        for (var index = 0; index < candidateSymbols.Length; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var symbol = candidateSymbols[index];
            try
            {
                var bars = await _kLineDataProvider.LoadKLineAsync(symbol, "m30", count, context.CancellationToken);
                if (bars.Count > 0)
                {
                    loaded++;
                }

                context.Stdout($"{symbol} m30 bars={bars.Count}");
            }
            catch (Exception ex)
            {
                context.Stderr($"{symbol} failed: {ex.Message}");
            }

            var progress = 20 + (int)Math.Round((index + 1) * 75d / candidateSymbols.Length);
            context.Progress(progress, $"更新30分钟K：{index + 1}/{candidateSymbols.Length}，成功 {loaded}");
        }

        context.Complete($"30分钟K更新完成：{loaded}/{candidateSymbols.Length}只");
    }

    private string[] BuildCandidateSymbols(MarketSnapshot snapshot)
    {
        var candidateCount = Math.Clamp(_options.CandidateCount, 1, 2000);
        return snapshot.Quotes
            .Where(item => item.Price > 0 && item.Amount >= 30_000_000m && item.ChangePercent >= -8m && item.ChangePercent <= 9m)
            .OrderByDescending(item =>
                Math.Max(item.ChangePercent, 0m) * 8m
                + Math.Min(Math.Max(item.VolumeRatio, 0m), 5m) * 8m
                + Math.Min(item.Amount / 100_000_000m, 35m))
            .Take(candidateCount)
            .Select(item => item.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
