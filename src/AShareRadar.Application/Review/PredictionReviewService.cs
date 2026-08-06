using AShareRadar.Application.History;
using AShareRadar.Application.MarketData;

namespace AShareRadar.Application.Review;

public sealed class PredictionReviewService
{
    private const decimal UpThreshold = 75m;
    private const decimal WatchThreshold = 55m;
    private const decimal IntradaySuccessThreshold = 2m;
    private const int PredictionSignalLimit = 10000;

    private readonly IHistoryQueryService _historyQueryService;
    private readonly IKLineDataProvider _kLineDataProvider;
    private readonly IIntradayKLineOverlayService _intradayOverlayService;
    private readonly IPredictionReviewStore _store;
    private readonly QlibNextDayPredictionRunner _qlibPredictionRunner;

    public PredictionReviewService(
        IHistoryQueryService historyQueryService,
        IKLineDataProvider kLineDataProvider,
        IIntradayKLineOverlayService intradayOverlayService,
        IPredictionReviewStore store,
        QlibNextDayPredictionRunner qlibPredictionRunner)
    {
        _historyQueryService = historyQueryService;
        _kLineDataProvider = kLineDataProvider;
        _intradayOverlayService = intradayOverlayService;
        _store = store;
        _qlibPredictionRunner = qlibPredictionRunner;
    }

    public PredictionReview Get(DateOnly signalDate)
    {
        return BuildReview(signalDate, _store.GetBySignalDate(signalDate), "已加载历史预测记录。");
    }

    public PredictionReview Generate(DateOnly signalDate)
    {
        var signals = _historyQueryService.QuerySignals(new HistoricalSignalQuery(signalDate, null, null, PredictionSignalLimit));
        var signalGroups = signals
            .Where(item => item.Price is > 0)
            .GroupBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => NormalizeSymbol(group.Key),
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        if (signalGroups.Count == 0)
        {
            return BuildReview(signalDate, [], "当天没有可用于预测的命中记录。");
        }

        var runResult = _qlibPredictionRunner.RunAsync(signalDate, signalGroups.Keys.OrderBy(item => item).ToArray())
            .GetAwaiter()
            .GetResult();

        var records = runResult.Predictions
            .Where(item => signalGroups.ContainsKey(item.Symbol))
            .Select(item => BuildPredictionRecord(signalDate, item, signalGroups[item.Symbol]))
            .OrderByDescending(item => item.PredictionScore)
            .ThenByDescending(item => item.Score)
            .ToArray();

        _store.UpsertMany(records);
        return BuildReview(signalDate, records, records.Length == 0
            ? "Qlib 明日预测未返回可导入记录。"
            : $"已生成 {records.Length} 条 Qlib 明日预测。输出目录：{runResult.OutputDirectory}");
    }

    public async Task<PredictionReview> VerifyAsync(DateOnly signalDate, CancellationToken cancellationToken)
    {
        var records = _store.GetBySignalDate(signalDate);
        if (records.Count == 0)
        {
            return BuildReview(signalDate, records, "请先生成当天预测，再执行验证。");
        }

        var verified = new List<PredictionRecord>();
        foreach (var record in records)
        {
            var bars = await _kLineDataProvider.LoadKLineAsync(record.Symbol, "day", 120, cancellationToken);
            bars = await _intradayOverlayService.AppendTemporaryDailyBarAsync(
                record.Symbol,
                "day",
                bars,
                cancellationToken);
            var orderedBars = bars.OrderBy(item => item.TradingTime).ToArray();
            var signalBar = orderedBars.LastOrDefault(item => DateOnly.FromDateTime(item.TradingTime) <= signalDate);
            var verifyBar = orderedBars.FirstOrDefault(item => DateOnly.FromDateTime(item.TradingTime) > signalDate);
            if (signalBar is null || verifyBar is null || signalBar.Close <= 0)
            {
                verified.Add(record with { VerifyStatus = "待验证" });
                continue;
            }

            var nextOpenReturn = CalculateReturn(verifyBar.Open, signalBar.Close);
            var nextCloseReturn = CalculateReturn(verifyBar.Close, signalBar.Close);
            var nextHighReturn = CalculateReturn(verifyBar.High, signalBar.Close);
            var nextLowReturn = CalculateReturn(verifyBar.Low, signalBar.Close);
            var predictsUp = record.PredictionDirection == "继续上涨";
            var closeSuccess = predictsUp ? nextCloseReturn > 0m : nextCloseReturn <= 0m;
            var intradaySuccess = predictsUp ? nextHighReturn >= IntradaySuccessThreshold : nextHighReturn < IntradaySuccessThreshold;

            verified.Add(record with
            {
                VerifyDate = DateOnly.FromDateTime(verifyBar.TradingTime),
                NextOpenReturn = nextOpenReturn,
                NextCloseReturn = nextCloseReturn,
                NextHighReturn = nextHighReturn,
                NextLowReturn = nextLowReturn,
                IsCloseSuccess = closeSuccess,
                IsIntradaySuccess = intradaySuccess,
                VerifyStatus = closeSuccess ? "成功" : "失败",
                VerifiedAt = DateTimeOffset.Now
            });
        }

        _store.UpsertMany(verified);
        return BuildReview(signalDate, verified, "已根据次一交易日日 K 验证预测结果。");
    }

    private static PredictionRecord BuildPredictionRecord(
        DateOnly signalDate,
        QlibTomorrowPrediction prediction,
        IReadOnlyList<HistoricalSignalItem> signals)
    {
        var ordered = signals.OrderByDescending(item => item.Score).ToArray();
        var best = ordered[0];
        var strategyCodes = ordered
            .Select(item => item.StrategyCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var strategyNames = ordered
            .Select(item => item.StrategyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var bestScore = ordered.Max(item => item.Score);
        var score = Math.Round(prediction.UpProbability * 100m, 2);
        var direction = MapQlibDirection(prediction.Direction);

        return new PredictionRecord(
            Guid.NewGuid(),
            signalDate,
            prediction.Symbol,
            string.IsNullOrWhiteSpace(prediction.Name) ? best.Name : prediction.Name,
            string.Join(",", strategyCodes),
            string.Join("、", strategyNames),
            ordered.Length,
            ordered.Sum(item => Math.Max(item.StrategyHitCount, 1)),
            ordered.Average(item => item.Score),
            bestScore,
            direction,
            Math.Round(score, 2),
            BuildPredictionReason(prediction, ordered, strategyCodes.Length),
            BuildRiskNote(prediction, ordered),
            VerifyDate: null,
            NextOpenReturn: null,
            NextCloseReturn: null,
            NextHighReturn: null,
            NextLowReturn: null,
            IsCloseSuccess: null,
            IsIntradaySuccess: null,
            VerifyStatus: "待验证",
            CreatedAt: DateTimeOffset.Now,
            VerifiedAt: null);
    }

    private static string MapQlibDirection(string value)
    {
        return value.Trim() switch
        {
            "偏上涨" => "继续上涨",
            "偏下跌" => "回落风险",
            "震荡" => "震荡观察",
            _ => value
        };
    }

    private static string BuildPredictionReason(
        QlibTomorrowPrediction prediction,
        IReadOnlyList<HistoricalSignalItem> signals,
        int strategyCount)
    {
        var best = signals.OrderByDescending(item => item.Score).First();
        var parts = new List<string>
        {
            $"Qlib 明日预测：上涨概率 {prediction.UpProbability:P2}，下跌概率 {prediction.DownProbability:P2}",
            $"判断 {prediction.Direction}，置信度 {prediction.Confidence}",
            $"当天最高信号分 {best.Score:F2}",
            $"命中 {signals.Count} 次",
            $"覆盖 {strategyCount} 个策略"
        };
        if (signals.Any(item => item.EventType is "Strengthened" or "ReHit"))
        {
            parts.Add("盘中出现加强或重新命中");
        }

        return string.Join("；", parts);
    }

    private static string BuildRiskNote(QlibTomorrowPrediction prediction, IReadOnlyList<HistoricalSignalItem> signals)
    {
        var risks = new List<string>();
        if (prediction.Executable == false)
        {
            risks.Add(string.IsNullOrWhiteSpace(prediction.BlockReason)
                ? "模型执行过滤判定不可执行"
                : prediction.BlockReason!);
        }
        else if (!string.IsNullOrWhiteSpace(prediction.BlockReason))
        {
            risks.Add(prediction.BlockReason!);
        }

        if (prediction.Confidence == "低")
        {
            risks.Add("模型方向置信度低");
        }

        if (signals.Any(item => item.EventType is "Weakened" or "Disappeared"))
        {
            risks.Add("当天存在降级或消失信号");
        }

        if (signals.Select(item => item.StrategyCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            risks.Add("仅单一策略命中");
        }

        return risks.Count == 0 ? "暂无明显规则风险。" : string.Join("；", risks);
    }

    private static string NormalizeSymbol(string value)
    {
        var text = value.Trim().ToUpperInvariant();
        if (text.Length >= 8 && (text.StartsWith("SH", StringComparison.Ordinal) || text.StartsWith("SZ", StringComparison.Ordinal)))
        {
            return text[2..8];
        }

        if (text.Contains('.', StringComparison.Ordinal))
        {
            return text.Split('.', 2)[0].PadLeft(6, '0');
        }

        return text.PadLeft(6, '0');
    }

    private static PredictionReview BuildReview(DateOnly signalDate, IReadOnlyList<PredictionRecord> records, string message)
    {
        var verified = records.Where(item => item.IsCloseSuccess.HasValue).ToArray();
        var upPredictions = records.Where(item => item.PredictionDirection == "继续上涨").ToArray();
        var closeSuccessCount = verified.Count(item => item.IsCloseSuccess == true);
        var intradaySuccessCount = verified.Count(item => item.IsIntradaySuccess == true);
        var verifyDates = records
            .Where(item => item.VerifyDate.HasValue)
            .Select(item => item.VerifyDate!.Value)
            .ToArray();
        var closeReturns = verified
            .Where(item => item.NextCloseReturn.HasValue)
            .Select(item => item.NextCloseReturn!.Value)
            .ToArray();

        return new PredictionReview(
            signalDate,
            verifyDates.Length == 0 ? null : verifyDates.Max(),
            records.Count,
            upPredictions.Length,
            verified.Length,
            closeSuccessCount,
            intradaySuccessCount,
            verified.Length == 0 ? null : closeSuccessCount * 100m / verified.Length,
            verified.Length == 0 ? null : intradaySuccessCount * 100m / verified.Length,
            closeReturns.Length == 0 ? null : closeReturns.Average(),
            message,
            records
                .OrderByDescending(item => item.PredictionScore)
                .ThenByDescending(item => item.Score)
                .ToArray());
    }

    private static decimal CalculateReturn(decimal value, decimal basis)
    {
        return basis <= 0 ? 0m : Math.Round((value - basis) / basis * 100m, 4);
    }
}
