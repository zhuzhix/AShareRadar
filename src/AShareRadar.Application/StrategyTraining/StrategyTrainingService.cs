using AShareRadar.Application.Backtesting;
using AShareRadar.Application.MarketData;

namespace AShareRadar.Application.StrategyTraining;

public sealed class StrategyTrainingService
{
    private const int DefaultEvaluationDays = 5;
    private static readonly decimal[] ScoreThresholds = [80m, 85m, 90m, 95m];
    private static readonly decimal[] AmountThresholds = [0m, 3m, 5m, 8m, 10m];
    private static readonly decimal[] RelativeStrengthThresholds = [0m, 1m, 2m, 3m];
    private static readonly decimal[] HeatThresholds = [0m, 60m, 70m, 80m, 90m];
    private static readonly int[] OutputLimits = [20, 30, 50, 80];

    private readonly IStrategyTrainingStore _store;
    private readonly IKLineDataProvider _kLineDataProvider;
    private readonly BacktestReplayService _backtestReplayService;

    public StrategyTrainingService(
        IStrategyTrainingStore store,
        IKLineDataProvider kLineDataProvider,
        BacktestReplayService backtestReplayService)
    {
        _store = store;
        _kLineDataProvider = kLineDataProvider;
        _backtestReplayService = backtestReplayService;
    }

    public async Task<StrategyTrainingDataset> BuildDatasetAsync(
        StrategyTrainingQuery query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeQuery(query);
        var cachedSamples = _store.QuerySamples(
            normalizedQuery.StartDate,
            normalizedQuery.EndDate,
            normalizedQuery.StrategyCode,
            normalizedQuery.EvaluationDays,
            10000);
        var cachedSamplesNeedMetricRefresh = cachedSamples.Any(item => item.Metrics is null || item.Metrics.Count == 0);
        if (!normalizedQuery.ForceRebuild && cachedSamples.Count > 0 && !cachedSamplesNeedMetricRefresh)
        {
            return BuildDatasetFromSamples(
                normalizedQuery,
                cachedSamples.Select(item => ApplySuccessCriteria(item, normalizedQuery)).ToArray(),
                "已读取已生成的训练样本缓存，并按当前成功标准重新统计。");
        }

        var sources = _store.QuerySignalSources(
            normalizedQuery.StartDate,
            normalizedQuery.EndDate,
            normalizedQuery.StrategyCode,
            5000);

        var distinctSources = sources
            .GroupBy(item => $"{item.SignalDate:yyyy-MM-dd}|{NormalizeSymbol(item.Symbol)}|{item.StrategyCode}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.EventTime)
                .First())
            .ToArray();

        var samples = new List<StrategyTrainingSample>(distinctSources.Length);
        foreach (var source in distinctSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = await BuildSampleAsync(source, normalizedQuery, cancellationToken);
            if (sample is not null)
            {
                samples.Add(sample);
            }
        }

        var usedReplayFallback = false;
        if (samples.Count == 0)
        {
            samples.AddRange(await BuildReplaySamplesAsync(normalizedQuery, cancellationToken));
            usedReplayFallback = samples.Count > 0;
        }

        _store.UpsertSamples(samples);
        var successCount = samples.Count(item => item.IsSuccess);
        return new StrategyTrainingDataset(
            normalizedQuery.StartDate,
            normalizedQuery.EndDate,
            normalizedQuery.StrategyCode,
            usedReplayFallback ? samples.Count : distinctSources.Length,
            samples.Count,
            successCount,
            CalculateRate(successCount, samples.Count),
            BuildDatasetMessage(distinctSources.Length, samples.Count, usedReplayFallback),
            samples
                .OrderByDescending(item => item.SignalDate)
                .ThenByDescending(item => item.Score)
                .Take(200)
                .ToArray());
    }

    public async Task<StrategyTrainingRun> RunAsync(
        StrategyTrainingQuery query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeQuery(query);
        var dataset = await BuildDatasetAsync(normalizedQuery, cancellationToken);
        var allSamples = dataset.Samples.Count < dataset.SampleCount
            ? _store.QuerySamples(dataset.StartDate, dataset.EndDate, dataset.StrategyCode, normalizedQuery.EvaluationDays, 10000)
            : dataset.Samples;
        allSamples = allSamples
            .Select(item => ApplySuccessCriteria(item, normalizedQuery))
            .ToArray();

        var scoreThresholds = normalizedQuery.ScoreThresholds ?? ScoreThresholds;
        var amountThresholds = normalizedQuery.AmountThresholds ?? AmountThresholds;
        var relativeStrengthThresholds = normalizedQuery.RelativeStrengthThresholds ?? RelativeStrengthThresholds;
        var heatThresholds = normalizedQuery.HeatThresholds ?? HeatThresholds;
        var outputLimits = normalizedQuery.OutputLimits ?? OutputLimits;
        var results = new List<StrategyTrainingResult>();
        foreach (var minScore in scoreThresholds)
        foreach (var minAmount in amountThresholds)
        foreach (var minRelativeStrength in relativeStrengthThresholds)
        foreach (var minHeat in heatThresholds)
        foreach (var maxOutput in outputLimits)
        {
            var selected = allSamples
                .Where(item => item.Score >= minScore)
                .Where(item => PassOptionalThreshold(item.AmountYi, minAmount))
                .Where(item => PassOptionalThreshold(item.RelativeStrengthPercent, minRelativeStrength))
                .Where(item => PassOptionalThreshold(GetHeatScore(item), minHeat))
                .GroupBy(item => item.SignalDate)
                .SelectMany(group => group
                    .OrderByDescending(BuildRankScore)
                    .ThenByDescending(item => item.Score)
                    .Take(maxOutput))
                .ToArray();

            if (selected.Length == 0)
            {
                continue;
            }

            var successCount = selected.Count(item => item.IsSuccess);
            results.Add(new StrategyTrainingResult(
                0,
                minScore,
                minAmount,
                minRelativeStrength,
                minHeat,
                maxOutput,
                selected.Length,
                successCount,
                CalculateRate(successCount, selected.Length),
                Average(selected.Select(item => item.NextOpenReturn)),
                Average(selected.Select(item => item.NextHighReturn)),
                Average(selected.Select(item => item.NextCloseReturn)),
                selected.Min(item => item.NextCloseReturn),
                BuildResultSummary(successCount, selected.Length, minScore, minAmount, minRelativeStrength, minHeat, maxOutput)));
        }

        var rankedResults = results
            .OrderByDescending(item => item.HitCount >= 5)
            .ThenByDescending(item => item.SuccessRate ?? 0m)
            .ThenByDescending(item => item.AverageNextCloseReturn ?? -999m)
            .ThenByDescending(item => item.HitCount)
            .Take(50)
            .Select((item, index) => item with { Rank = index + 1 })
            .ToArray();

        var run = new StrategyTrainingRun(
            Guid.NewGuid(),
            dataset.StartDate,
            dataset.EndDate,
            dataset.StrategyCode,
            dataset.SourceSignalCount,
            dataset.SampleCount,
            rankedResults.Length,
            DateTimeOffset.Now,
            dataset.SampleCount == 0 ? dataset.Message : "训练完成，结果按成功率、平均收益和样本量排序。",
            rankedResults);

        _store.SaveRun(run);
        return run;
    }

    private async Task<StrategyTrainingSample?> BuildSampleAsync(
        StrategyTrainingSignalSource source,
        StrategyTrainingQuery query,
        CancellationToken cancellationToken)
    {
        var bars = await _kLineDataProvider.LoadKLineAsync(source.Symbol, "day", 260, cancellationToken);
        if (bars.Count < 2)
        {
            return null;
        }

        var orderedBars = bars
            .OrderBy(item => item.TradingTime)
            .ToArray();
        var signalIndex = Array.FindLastIndex(
            orderedBars,
            item => DateOnly.FromDateTime(item.TradingTime.Date) <= source.SignalDate);
        if (signalIndex < 0 || signalIndex + query.EvaluationDays >= orderedBars.Length)
        {
            return null;
        }

        var baseBar = orderedBars[signalIndex];
        var forwardBars = orderedBars
            .Skip(signalIndex + 1)
            .Take(query.EvaluationDays)
            .ToArray();
        var nextBar = forwardBars[0];
        var evaluationCloseBar = forwardBars[^1];
        if (baseBar.Close <= 0m)
        {
            return null;
        }

        var nextOpenReturn = CalculateReturn(nextBar.Open, baseBar.Close);
        var nextHighReturn = CalculateReturn(forwardBars.Max(item => item.High), baseBar.Close);
        var nextCloseReturn = CalculateReturn(evaluationCloseBar.Close, baseBar.Close);
        var isSuccess = nextHighReturn >= query.SuccessHighReturnThreshold
            && (!query.RequirePositiveClose || nextCloseReturn > 0m);

        return new StrategyTrainingSample(
            StableId(source.SignalDate, source.Symbol, source.StrategyCode),
            source.SignalDate,
            NormalizeSymbol(source.Symbol),
            source.Name,
            source.StrategyCode,
            source.StrategyName,
            source.Score,
            source.Price,
            FindMetric(source.Metrics, "amount_yi", "amountYi", "total_amount_yi", "turnover_yi", "amount"),
            FindMetric(source.Metrics, "change_percent", "changePercent", "pct_chg", "涨幅"),
            FindMetric(source.Metrics, "volume_ratio", "volumeRatio", "量比"),
            FindMetric(source.Metrics, "relative_strength_percent", "relativeStrengthPercent", "market_relative_strength", "相对强度"),
            FindMetric(source.Metrics, "sector_heat_score", "sectorHeatScore", "板块热度"),
            FindMetric(source.Metrics, "concept_heat_score", "conceptHeatScore", "概念热度"),
            null,
            nextOpenReturn,
            nextHighReturn,
            nextCloseReturn,
            isSuccess,
            source.Reason,
            source.Metrics,
            query.EvaluationDays);
    }

    private static StrategyTrainingDataset BuildDatasetFromSamples(
        StrategyTrainingQuery query,
        IReadOnlyList<StrategyTrainingSample> samples,
        string message)
    {
        var successCount = samples.Count(item => item.IsSuccess);
        return new StrategyTrainingDataset(
            query.StartDate,
            query.EndDate,
            query.StrategyCode,
            samples.Count,
            samples.Count,
            successCount,
            CalculateRate(successCount, samples.Count),
            message,
            samples
                .OrderByDescending(item => item.SignalDate)
                .ThenByDescending(item => item.Score)
                .Take(200)
                .ToArray());
    }

    private static StrategyTrainingSample ApplySuccessCriteria(
        StrategyTrainingSample sample,
        StrategyTrainingQuery query)
    {
        var isSuccess = sample.NextHighReturn >= query.SuccessHighReturnThreshold
            && (!query.RequirePositiveClose || sample.NextCloseReturn > 0m);
        return sample with { IsSuccess = isSuccess };
    }

    private async Task<IReadOnlyList<StrategyTrainingSample>> BuildReplaySamplesAsync(
        StrategyTrainingQuery query,
        CancellationToken cancellationToken)
    {
        var strategyCodes = string.IsNullOrWhiteSpace(query.StrategyCode)
            ? Array.Empty<string>()
            : [query.StrategyCode];
        var replay = await _backtestReplayService.ReplayAsync(
            new BacktestReplayQuery(
                query.StartDate,
                query.EndDate,
                [],
                strategyCodes,
                120 + query.EvaluationDays,
                "Historical",
                100),
            cancellationToken);

        var sources = replay.Signals
            .GroupBy(item => $"{item.TradingDate:yyyy-MM-dd}|{NormalizeSymbol(item.Symbol)}|{item.StrategyCode}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Score)
                .First())
            .Select(item => new StrategyTrainingSignalSource(
                StableId(item.TradingDate, item.Symbol, item.StrategyCode),
                new DateTimeOffset(item.TradingDate.ToDateTime(new TimeOnly(15, 0)), TimeSpan.FromHours(8)),
                item.TradingDate,
                item.Symbol,
                item.Name,
                item.StrategyCode,
                item.StrategyName,
                item.Score,
                item.Price,
                item.Reason,
                item.Metrics ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        var samples = new List<StrategyTrainingSample>(sources.Length);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = await BuildSampleAsync(source, query, cancellationToken);
            if (sample is not null)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    private static StrategyTrainingQuery NormalizeQuery(StrategyTrainingQuery query)
    {
        var start = query.StartDate <= query.EndDate ? query.StartDate : query.EndDate;
        var end = query.EndDate >= query.StartDate ? query.EndDate : query.StartDate;
        return query with
        {
            StartDate = start,
            EndDate = end,
            StrategyCode = string.IsNullOrWhiteSpace(query.StrategyCode) ? null : query.StrategyCode.Trim(),
            SuccessHighReturnThreshold = query.SuccessHighReturnThreshold <= 0m ? 2m : query.SuccessHighReturnThreshold,
            EvaluationDays = query.EvaluationDays <= 0 ? DefaultEvaluationDays : Math.Clamp(query.EvaluationDays, 1, 20),
            ScoreThresholds = NormalizeDecimalGrid(query.ScoreThresholds, ScoreThresholds, 0m, 300m),
            AmountThresholds = NormalizeDecimalGrid(query.AmountThresholds, AmountThresholds, 0m, 500m),
            RelativeStrengthThresholds = NormalizeDecimalGrid(query.RelativeStrengthThresholds, RelativeStrengthThresholds, -50m, 50m),
            HeatThresholds = NormalizeDecimalGrid(query.HeatThresholds, HeatThresholds, 0m, 100m),
            OutputLimits = NormalizeIntGrid(query.OutputLimits, OutputLimits, 1, 300)
        };
    }

    private static decimal[] NormalizeDecimalGrid(
        IReadOnlyList<decimal>? values,
        decimal[] fallback,
        decimal min,
        decimal max)
    {
        var normalized = values?
            .Select(item => Math.Clamp(item, min, max))
            .Distinct()
            .OrderBy(item => item)
            .Take(12)
            .ToArray();
        return normalized is { Length: > 0 } ? normalized : fallback;
    }

    private static int[] NormalizeIntGrid(
        IReadOnlyList<int>? values,
        int[] fallback,
        int min,
        int max)
    {
        var normalized = values?
            .Select(item => Math.Clamp(item, min, max))
            .Distinct()
            .OrderBy(item => item)
            .Take(12)
            .ToArray();
        return normalized is { Length: > 0 } ? normalized : fallback;
    }

    private static decimal BuildRankScore(StrategyTrainingSample item)
    {
        var heatScore = Math.Max(item.SectorHeatScore ?? 0m, item.ConceptHeatScore ?? 0m);
        var amountScore = Math.Min((item.AmountYi ?? 0m) / 10m * 100m, 100m);
        var relativeStrengthScore = Math.Clamp((item.RelativeStrengthPercent ?? 0m) * 12m + 50m, 0m, 100m);
        var sentimentScore = item.SentimentTemperature ?? 55m;

        return item.Score * 0.35m
            + heatScore * 0.20m
            + relativeStrengthScore * 0.15m
            + amountScore * 0.10m
            + sentimentScore * 0.10m;
    }

    private static decimal? FindMetric(IReadOnlyDictionary<string, decimal> metrics, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metrics.TryGetValue(key, out var value))
            {
                return NormalizeAmountMetric(key, value);
            }

            var match = metrics.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Key))
            {
                return NormalizeAmountMetric(match.Key, match.Value);
            }
        }

        return null;
    }

    private static bool PassOptionalThreshold(decimal? value, decimal threshold)
    {
        return threshold <= 0m || !value.HasValue || value.Value >= threshold;
    }

    private static decimal? GetHeatScore(StrategyTrainingSample item)
    {
        return item.SectorHeatScore.HasValue || item.ConceptHeatScore.HasValue
            ? Math.Max(item.SectorHeatScore ?? 0m, item.ConceptHeatScore ?? 0m)
            : null;
    }

    private static decimal NormalizeAmountMetric(string key, decimal value)
    {
        if (key.Contains("amount", StringComparison.OrdinalIgnoreCase) && !key.Contains("yi", StringComparison.OrdinalIgnoreCase) && value > 1000000m)
        {
            return value / 100000000m;
        }

        return value;
    }

    private static decimal CalculateReturn(decimal target, decimal source)
    {
        return source == 0m ? 0m : decimal.Round((target - source) / source * 100m, 4);
    }

    private static decimal? CalculateRate(int numerator, int denominator)
    {
        return denominator == 0 ? null : decimal.Round((decimal)numerator / denominator * 100m, 4);
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var available = values.Where(item => item.HasValue).Select(item => item!.Value).ToArray();
        return available.Length == 0 ? null : decimal.Round(available.Average(), 4);
    }

    private static string BuildDatasetMessage(int sourceCount, int sampleCount, bool usedReplayFallback)
    {
        if (usedReplayFallback)
        {
            return $"运行时信号缺少可验证样本，已改用历史日线回放生成 {sampleCount} 条训练样本。";
        }

        if (sourceCount == 0)
        {
            return "没有找到符合条件的历史信号。";
        }

        if (sampleCount == 0)
        {
            return "找到历史信号，但缺少可验证的下一交易日日线。";
        }

        return $"已生成 {sampleCount} 条可验证训练样本，原始信号 {sourceCount} 条。";
    }

    private static string BuildResultSummary(
        int successCount,
        int hitCount,
        decimal minScore,
        decimal minAmount,
        decimal minRelativeStrength,
        decimal minHeat,
        int maxOutput)
    {
        var rate = CalculateRate(successCount, hitCount)?.ToString("F1") ?? "--";
        return $"分数>={minScore:F0}，成交额>={minAmount:F0}亿，相对强度>={minRelativeStrength:F0}%，热度>={minHeat:F0}，每日Top {maxOutput}，成功率 {rate}%";
    }

    private static string NormalizeSymbol(string symbol)
    {
        var value = symbol.Trim().ToLowerInvariant();
        if ((value.StartsWith("sh") || value.StartsWith("sz")) && value.Length == 8)
        {
            return value[2..];
        }

        return value;
    }

    private static Guid StableId(DateOnly date, string symbol, string strategyCode)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{date:yyyy-MM-dd}|{NormalizeSymbol(symbol)}|{strategyCode}");
        Span<byte> hash = stackalloc byte[16];
        System.Security.Cryptography.MD5.HashData(bytes, hash);
        return new Guid(hash);
    }
}

public interface IStrategyTrainingStore
{
    IReadOnlyList<StrategyTrainingSignalSource> QuerySignalSources(
        DateOnly startDate,
        DateOnly endDate,
        string? strategyCode,
        int maxCount);

    IReadOnlyList<StrategyTrainingSample> QuerySamples(
        DateOnly startDate,
        DateOnly endDate,
        string? strategyCode,
        int evaluationDays,
        int maxCount);

    void UpsertSamples(IReadOnlyList<StrategyTrainingSample> samples);

    void SaveRun(StrategyTrainingRun run);
}

public sealed record StrategyTrainingQuery(
    DateOnly StartDate,
    DateOnly EndDate,
    string? StrategyCode,
    decimal SuccessHighReturnThreshold,
    bool RequirePositiveClose,
    int EvaluationDays = 5,
    bool ForceRebuild = false,
    IReadOnlyList<decimal>? ScoreThresholds = null,
    IReadOnlyList<decimal>? AmountThresholds = null,
    IReadOnlyList<decimal>? RelativeStrengthThresholds = null,
    IReadOnlyList<decimal>? HeatThresholds = null,
    IReadOnlyList<int>? OutputLimits = null);

public sealed record StrategyTrainingSignalSource(
    Guid EventId,
    DateTimeOffset EventTime,
    DateOnly SignalDate,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    string Reason,
    IReadOnlyDictionary<string, decimal> Metrics);

public sealed record StrategyTrainingDataset(
    DateOnly StartDate,
    DateOnly EndDate,
    string? StrategyCode,
    int SourceSignalCount,
    int SampleCount,
    int SuccessCount,
    decimal? SuccessRate,
    string Message,
    IReadOnlyList<StrategyTrainingSample> Samples);

public sealed record StrategyTrainingSample(
    Guid Id,
    DateOnly SignalDate,
    string Symbol,
    string Name,
    string StrategyCode,
    string StrategyName,
    decimal Score,
    decimal? Price,
    decimal? AmountYi,
    decimal? ChangePercent,
    decimal? VolumeRatio,
    decimal? RelativeStrengthPercent,
    decimal? SectorHeatScore,
    decimal? ConceptHeatScore,
    decimal? SentimentTemperature,
    decimal? NextOpenReturn,
    decimal? NextHighReturn,
    decimal? NextCloseReturn,
    bool IsSuccess,
    string Reason,
    IReadOnlyDictionary<string, decimal>? Metrics = null,
    int EvaluationDays = 5);

public sealed record StrategyTrainingRun(
    Guid RunId,
    DateOnly StartDate,
    DateOnly EndDate,
    string? StrategyCode,
    int SourceSignalCount,
    int SampleCount,
    int ResultCount,
    DateTimeOffset CreatedAt,
    string Message,
    IReadOnlyList<StrategyTrainingResult> Results);

public sealed record StrategyTrainingResult(
    int Rank,
    decimal MinScore,
    decimal MinAmountYi,
    decimal MinRelativeStrengthPercent,
    decimal MinHeatScore,
    int MaxOutputPerDay,
    int HitCount,
    int SuccessCount,
    decimal? SuccessRate,
    decimal? AverageNextOpenReturn,
    decimal? AverageNextHighReturn,
    decimal? AverageNextCloseReturn,
    decimal? WorstNextCloseReturn,
    string Summary);
