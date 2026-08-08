using System.Net.Http;
using System.Net.Http.Json;
using AShareRadar.Contracts.Backtesting;
using AShareRadar.Contracts.History;
using AShareRadar.Contracts.Jobs;
using AShareRadar.Contracts.MarketData;
using AShareRadar.Contracts.Monitoring;
using AShareRadar.Contracts.Opportunities;
using AShareRadar.Contracts.Review;
using AShareRadar.Contracts.Strategies;

namespace AShareRadar.Desktop.Services;

public sealed class RadarApiClient
{
    private readonly HttpClient _httpClient;

    public RadarApiClient(string baseAddress)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    public async Task<MonitorStatusDto?> GetMonitorStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<MonitorStatusDto>(
            "/api/monitor/status",
            cancellationToken);
    }

    public async Task<MarketDataStatusDto?> GetMarketDataStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<MarketDataStatusDto>(
            "/api/market-data/status",
            cancellationToken);
    }

    public async Task<MarketSentimentSnapshotDto?> GetMarketSentimentAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<MarketSentimentSnapshotDto>(
            "/api/market-sentiment/snapshot",
            cancellationToken);
    }

    public async Task<MarketSentimentSnapshotDto?> RefreshMarketSentimentAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<MarketSentimentSnapshotDto>(
            "/api/market-sentiment/snapshot?refresh=true",
            cancellationToken);
    }

    public async Task<IReadOnlyList<MarketSentimentSnapshotDto>> GetMarketSentimentHistoryAsync(
        DateOnly? tradingDate,
        int count,
        CancellationToken cancellationToken)
    {
        var path = tradingDate.HasValue
            ? $"/api/market-sentiment/history?tradingDate={tradingDate.Value:yyyy-MM-dd}&count={count}"
            : $"/api/market-sentiment/history?count={count}";

        return await _httpClient.GetFromJsonAsync<MarketSentimentSnapshotDto[]>(
            path,
            cancellationToken) ?? [];
    }

    public async Task<MarketSentimentStatusDto?> GetMarketSentimentStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<MarketSentimentStatusDto>(
            "/api/market-sentiment/status",
            cancellationToken);
    }

    public async Task<IReadOnlyList<MarketSentimentDataSourceStatusDto>> GetMarketSentimentDataSourcesAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<MarketSentimentDataSourceStatusDto[]>(
            "/api/market-sentiment/data-sources",
            cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<MarketSentimentRegimeDto>> GetMarketSentimentRegimesAsync(
        int count,
        CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<MarketSentimentRegimeDto[]>(
            $"/api/market-sentiment/regimes?count={count}",
            cancellationToken) ?? [];
    }

    public async Task<MarketSentimentStrategyRulesDto?> GetMarketSentimentStrategyRulesAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<MarketSentimentStrategyRulesDto>(
            "/api/market-sentiment/strategy-rules",
            cancellationToken);
    }

    public async Task<IReadOnlyList<HeatBoardItemDto>> GetSectorHeatAsync(
        int count,
        CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<HeatBoardItemDto[]>(
            $"/api/market-data/sectors?count={count}",
            cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<HeatBoardItemDto>> GetConceptHeatAsync(
        int count,
        CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<HeatBoardItemDto[]>(
            $"/api/market-data/concepts?count={count}",
            cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<KLineBarDto>> GetKLineAsync(
        string symbol,
        string period,
        int count,
        CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<KLineBarDto[]>(
            $"/api/market-data/kline?symbol={Uri.EscapeDataString(symbol)}&period={Uri.EscapeDataString(period)}&count={count}",
            cancellationToken) ?? [];
    }

    public async Task<IndicatorSeriesDto?> GetIndicatorsAsync(
        string symbol,
        string period,
        string type,
        int count,
        CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<IndicatorSeriesDto>(
            $"/api/market-data/indicators?symbol={Uri.EscapeDataString(symbol)}&period={Uri.EscapeDataString(period)}&type={Uri.EscapeDataString(type)}&count={count}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<OpportunityDto>> GetOpportunitiesAsync(
        string view,
        CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<OpportunityDto[]>(
            $"/api/opportunities?view={Uri.EscapeDataString(view)}",
            cancellationToken) ?? [];
    }

    public async Task<OpportunityDetailDto?> GetOpportunityDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<OpportunityDetailDto>(
            $"/api/opportunities/{id}",
            cancellationToken);
    }

    public async Task<OpportunityDto?> SaveDecisionAsync(
        Guid id,
        string decisionType,
        string? note,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/opportunities/{id}/decision",
            new DecisionRequest(decisionType, note),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OpportunityDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<SignalEventDto>> GetSignalEventsAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<SignalEventDto[]>(
            "/api/signals/events?count=80",
            cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<HistoricalSignalDto>> GetHistoricalSignalsAsync(
        DateOnly? tradingDate,
        string? symbol,
        string? strategyCode,
        int count,
        CancellationToken cancellationToken)
    {
        var query = BuildHistoryQuery(tradingDate, symbol, strategyCode, count);
        return await _httpClient.GetFromJsonAsync<HistoricalSignalDto[]>(
            $"/api/history/signals?{query}",
            cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<StrategyPerformanceDto>> GetStrategyPerformanceAsync(
        DateOnly? tradingDate,
        int count,
        CancellationToken cancellationToken)
    {
        var query = tradingDate.HasValue
            ? $"tradingDate={Uri.EscapeDataString(tradingDate.Value.ToString("yyyy-MM-dd"))}&count={count}"
            : $"count={count}";
        return await _httpClient.GetFromJsonAsync<StrategyPerformanceDto[]>(
            $"/api/history/strategies?{query}",
            cancellationToken) ?? [];
    }

    public async Task<TodayReviewDto?> GetTodayReviewAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<TodayReviewDto>(
            "/api/review/today",
            cancellationToken);
    }

    public async Task<PredictionReviewDto?> GetPredictionReviewAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<PredictionReviewDto>(
            $"/api/review/predictions?date={Uri.EscapeDataString(date.ToString("yyyy-MM-dd"))}",
            cancellationToken);
    }

    public async Task<CreateBackgroundJobResponse?> StartNextDayPredictionJobAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            $"/api/jobs/next-day-prediction?date={Uri.EscapeDataString(date.ToString("yyyy-MM-dd"))}",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateBackgroundJobResponse>(cancellationToken);
    }

    public async Task<PredictionReviewDto?> VerifyPredictionReviewAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            $"/api/review/predictions/verify?date={Uri.EscapeDataString(date.ToString("yyyy-MM-dd"))}",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PredictionReviewDto>(cancellationToken);
    }

    public async Task<LongTermTrackingQueryResultDto?> GetLongTermTrackingAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        string? symbol,
        string? strategyCode,
        string? status,
        string sortBy,
        bool descending,
        int count,
        CancellationToken cancellationToken)
    {
        var parameters = new List<string>();
        if (fromDate.HasValue)
        {
            parameters.Add($"fromDate={Uri.EscapeDataString(fromDate.Value.ToString("yyyy-MM-dd"))}");
        }

        if (toDate.HasValue)
        {
            parameters.Add($"toDate={Uri.EscapeDataString(toDate.Value.ToString("yyyy-MM-dd"))}");
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            parameters.Add($"symbol={Uri.EscapeDataString(symbol.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(strategyCode))
        {
            parameters.Add($"strategyCode={Uri.EscapeDataString(strategyCode.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            parameters.Add($"status={Uri.EscapeDataString(status.Trim())}");
        }

        parameters.Add($"sortBy={Uri.EscapeDataString(sortBy)}");
        parameters.Add($"descending={descending.ToString().ToLowerInvariant()}");
        parameters.Add($"count={count}");

        return await _httpClient.GetFromJsonAsync<LongTermTrackingQueryResultDto>(
            $"/api/review/long-term-tracking?{string.Join("&", parameters)}",
            cancellationToken);
    }

    public async Task<LongTermTrackingBackfillResultDto?> BackfillLongTermTrackingAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            "/api/review/long-term-tracking/backfill",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LongTermTrackingBackfillResultDto>(cancellationToken);
    }

    public async Task<LongTermTrackingItemDto?> UpdateLongTermTrackingStatusAsync(
        Guid id,
        string status,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/review/long-term-tracking/{id}/status",
            new UpdateLongTermTrackingStatusRequest(status),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LongTermTrackingItemDto>(cancellationToken);
    }

    public async Task<LongTermTrackingItemDto?> UpdateLongTermTrackingNoteAsync(
        Guid id,
        string? note,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/review/long-term-tracking/{id}/note",
            new UpdateLongTermTrackingNoteRequest(note),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LongTermTrackingItemDto>(cancellationToken);
    }

    public async Task<HistoricalDataUpdateStatusDto?> GetHistoricalDataUpdateStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<HistoricalDataUpdateStatusDto>(
            "/api/history-data/status",
            cancellationToken);
    }

    public async Task<CreateBackgroundJobResponse?> TriggerHistoricalDataUpdateAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            "/api/history-data/update-now",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateBackgroundJobResponse>(cancellationToken);
    }

    public async Task<CreateBackgroundJobResponse?> StartM30KLineUpdateJobAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            "/api/jobs/m30-kline-update",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateBackgroundJobResponse>(cancellationToken);
    }

    public async Task<BackgroundJobDto?> GetJobAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<BackgroundJobDto>(
            $"/api/jobs/{id}",
            cancellationToken);
    }

    public async Task<BackgroundJobDto?> GetLatestJobAsync(string? type, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(type)
            ? "/api/jobs/latest"
            : $"/api/jobs/latest?type={Uri.EscapeDataString(type)}";
        return await _httpClient.GetFromJsonAsync<BackgroundJobDto>(path, cancellationToken);
    }

    public async Task<IReadOnlyList<BackgroundJobDto>> GetActiveJobsAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<BackgroundJobDto[]>(
            "/api/jobs/active",
            cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<BackgroundJobLogDto>> GetJobLogsAsync(
        Guid id,
        int count,
        CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<BackgroundJobLogDto[]>(
            $"/api/jobs/{id}/logs?count={count}",
            cancellationToken) ?? [];
    }


    public async Task<IReadOnlyList<StrategyDefinitionDto>> GetStrategiesAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<StrategyDefinitionDto[]>(
            "/api/strategies",
            cancellationToken) ?? [];
    }

    public async Task<BacktestReplayResultDto?> ReplayBacktestAsync(
        BacktestReplayRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/backtest/replay",
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BacktestReplayResultDto>(cancellationToken);
    }

    private static string BuildHistoryQuery(DateOnly? tradingDate, string? symbol, string? strategyCode, int count)
    {
        var parts = new List<string>
        {
            $"count={count}"
        };

        if (tradingDate.HasValue)
        {
            parts.Add($"tradingDate={Uri.EscapeDataString(tradingDate.Value.ToString("yyyy-MM-dd"))}");
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            parts.Add($"symbol={Uri.EscapeDataString(symbol.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(strategyCode))
        {
            parts.Add($"strategyCode={Uri.EscapeDataString(strategyCode.Trim())}");
        }

        return string.Join("&", parts);
    }
}


