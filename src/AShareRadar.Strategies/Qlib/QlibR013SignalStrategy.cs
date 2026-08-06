using AShareRadar.Application.MarketData;
using AShareRadar.Application.Qlib;
using AShareRadar.Application.Strategies;
using AShareRadar.Domain.MarketData;
using AShareRadar.Domain.Strategies;

namespace AShareRadar.Strategies.Qlib;

public sealed class QlibR013SignalStrategy : ISignalStrategy
{
    private readonly QlibSignalOptions _options;
    private readonly QlibSignalFileReader _reader;

    public QlibR013SignalStrategy(QlibSignalOptions options, QlibSignalFileReader reader)
    {
        _options = options;
        _reader = reader;
    }

    public string Code => _options.StrategyCode;

    public string Name => _options.StrategyName;

    public StrategyType Type => StrategyType.Experimental;

    public StrategyDefinition Definition => new(
        Code,
        Name,
        Type,
        StrategyStage.TriggerConfirmation,
        StrategySignalAction.Candidate,
        new StrategyDataRequirement(
            RequiresRealtimeQuote: true,
            RequiresDailyKLine: false,
            RequiresMinuteKLine: false,
            RequiresSectorData: false,
            RequiresCapitalFlow: false,
            MinDailyBarCount: 0),
        new Dictionary<string, string>
        {
            ["topk"] = _options.TopK.ToString(),
            ["candidate_topk"] = _options.CandidateTopK.ToString(),
            ["confirm_topk"] = _options.ConfirmTopK.ToString(),
            ["min_realtime_amount"] = _options.MinRealtimeAmount.ToString("F0"),
            ["max_realtime_change_percent"] = _options.MaxRealtimeChangePercent.ToString("F1")
        },
        "Qlib daily model produces the watchlist; AShareRadar confirms it with realtime amount, change percent and volume ratio.");

    public Task<IReadOnlyList<StrategySignal>> EvaluateAsync(StrategyContext context, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult<IReadOnlyList<StrategySignal>>([]);
        }

        QlibSignalSnapshot snapshot;
        try
        {
            snapshot = _reader.LoadLatest();
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<StrategySignal>>([]);
        }

        if ((context.TradingDate.DayNumber - snapshot.SignalDate.DayNumber) > _options.MaxSignalAgeDays)
        {
            return Task.FromResult<IReadOnlyList<StrategySignal>>([]);
        }

        var quotesByCode = context.Snapshot.Quotes
            .GroupBy(item => StockSymbolNormalizer.NormalizeCode(item.Symbol), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var signals = snapshot.Records
            .Take(_options.TopK)
            .Select(record =>
            {
                quotesByCode.TryGetValue(record.Code, out var quote);
                return quote is null ? null : BuildSignal(record, quote);
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Score)
            .ToArray();

        return Task.FromResult<IReadOnlyList<StrategySignal>>(signals);
    }

    private StrategySignal? BuildSignal(QlibSignalRecord record, StockQuote quote)
    {
        var failed = BuildHardRisk(record, quote);
        if (failed.Count > 0)
        {
            return null;
        }

        var action = StrategySignalAction.Watch;
        var confidence = StrategySignalConfidence.Low;
        if (record.ModelRank <= _options.ConfirmTopK
            && quote.Amount >= _options.ConfirmRealtimeAmount
            && quote.VolumeRatio >= _options.ConfirmVolumeRatio
            && quote.ChangePercent > 0m
            && quote.ChangePercent <= _options.MaxRealtimeChangePercent)
        {
            action = StrategySignalAction.Confirm;
            confidence = StrategySignalConfidence.High;
        }
        else if (record.ModelRank <= _options.CandidateTopK
            && quote.Amount >= _options.MinRealtimeAmount
            && quote.VolumeRatio >= _options.CandidateVolumeRatio
            && quote.ChangePercent <= _options.MaxRealtimeChangePercent)
        {
            action = StrategySignalAction.Candidate;
            confidence = StrategySignalConfidence.Medium;
        }

        var score = record.ModelScore100
            + Math.Min(Math.Max(quote.ChangePercent, 0m) * 0.8m, 4m)
            + Math.Min(Math.Max(quote.VolumeRatio - 1m, 0m) * 2m, 4m);
        score = Math.Min(Math.Round(score, 2), 100m);

        var reason = $"Qlib r013 Rank {record.ModelRank}, model score {record.ModelScore100:F2}; realtime change {quote.ChangePercent:F2}%, volume ratio {quote.VolumeRatio:F2}, amount {quote.Amount / 100000000m:F2} Yi.";
        var risk = BuildSoftRisk(quote);

        return new StrategySignal(
            record.Code,
            string.IsNullOrWhiteSpace(quote.Name) ? record.Name : quote.Name,
            Code,
            Name,
            Type,
            score,
            quote.Price,
            reason,
            risk,
            action,
            confidence,
            action == StrategySignalAction.Watch ? StrategyStage.CandidateRanking : StrategyStage.TriggerConfirmation,
            new Dictionary<string, decimal>
            {
                ["qlib_rank"] = record.ModelRank,
                ["qlib_score"] = record.ModelScore100,
                ["pred_score"] = record.PredScore,
                ["target_weight"] = record.TargetWeight,
                ["change_percent"] = quote.ChangePercent,
                ["volume_ratio"] = quote.VolumeRatio,
                ["amount_yi"] = quote.Amount / 100000000m
            },
            ["Qlib", "r013", $"Top{record.ModelRank}", action.ToString()],
            BuildPassedConditions(record, quote, action),
            []);
    }

    private IReadOnlyList<string> BuildHardRisk(QlibSignalRecord record, StockQuote quote)
    {
        var failed = new List<string>();
        if (_options.ExcludeBeijingExchange && (record.Code.StartsWith('4') || record.Code.StartsWith('8')))
        {
            failed.Add("Beijing exchange is excluded.");
        }

        if (_options.ExcludeSt && quote.Name.Contains("ST", StringComparison.OrdinalIgnoreCase))
        {
            failed.Add("ST stock is excluded.");
        }

        if (quote.Price <= 0m)
        {
            failed.Add("Realtime price is invalid.");
        }

        if (quote.Amount < _options.MinRealtimeAmount)
        {
            failed.Add($"Amount below {_options.MinRealtimeAmount / 100000000m:F2} Yi.");
        }

        if (quote.ChangePercent > _options.MaxWatchChangePercent)
        {
            failed.Add($"Change percent above {_options.MaxWatchChangePercent:F1}%, no chase.");
        }

        return failed;
    }

    private static string? BuildSoftRisk(StockQuote quote)
    {
        var risks = new List<string>();
        if (quote.ChangePercent < -3m)
        {
            risks.Add("Intraday drawdown is large; check breakdown risk.");
        }

        if (quote.VolumeRatio < 1m)
        {
            risks.Add("Volume ratio is weak.");
        }

        return risks.Count == 0 ? null : string.Join("; ", risks);
    }

    private IReadOnlyList<string> BuildPassedConditions(QlibSignalRecord record, StockQuote quote, StrategySignalAction action)
    {
        var result = new List<string>
        {
            $"Qlib rank {record.ModelRank} <= {_options.TopK}",
            $"Amount {quote.Amount / 100000000m:F2} Yi >= {_options.MinRealtimeAmount / 100000000m:F2} Yi",
            $"Change {quote.ChangePercent:F2}% <= {_options.MaxWatchChangePercent:F1}%"
        };

        if (action != StrategySignalAction.Watch)
        {
            result.Add($"Volume ratio {quote.VolumeRatio:F2} >= {_options.CandidateVolumeRatio:F2}");
        }

        return result;
    }
}
