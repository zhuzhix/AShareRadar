using System.Net.Http;
using System.Net.Http.Json;
using AShareRadar.Contracts.Backtesting;
using AShareRadar.Contracts.History;
using AShareRadar.Contracts.MarketData;
using AShareRadar.Contracts.Monitoring;
using AShareRadar.Contracts.Opportunities;
using AShareRadar.Contracts.Review;
using AShareRadar.Contracts.Qlib;
using AShareRadar.Contracts.Strategies;
using AShareRadar.Contracts.StrategyTraining;

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

    public async Task StartAsync(int scanIntervalSeconds, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/monitor/start",
            new StartMonitorRequest(scanIntervalSeconds),
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            "/api/monitor/pause",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            "/api/monitor/scan-once",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
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

    public async Task<PredictionReviewDto?> GeneratePredictionReviewAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            $"/api/review/predictions/generate?date={Uri.EscapeDataString(date.ToString("yyyy-MM-dd"))}",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PredictionReviewDto>(cancellationToken);
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

    public async Task<HistoricalDataUpdateStatusDto?> GetHistoricalDataUpdateStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<HistoricalDataUpdateStatusDto>(
            "/api/history-data/status",
            cancellationToken);
    }

    public async Task<HistoricalDataUpdateStatusDto?> TriggerHistoricalDataUpdateAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            "/api/history-data/update-now",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HistoricalDataUpdateStatusDto>(cancellationToken);
    }

    public async Task<MarketMappingUpdateStatusDto?> GetMarketMappingUpdateStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<MarketMappingUpdateStatusDto>(
            "/api/market-data/mapping-update/status",
            cancellationToken);
    }

    public async Task<MarketMappingUpdateStatusDto?> TriggerMarketMappingUpdateAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            "/api/market-data/mapping-update/run",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MarketMappingUpdateStatusDto>(cancellationToken);
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

    public async Task<StrategyTrainingDatasetDto?> BuildStrategyTrainingDatasetAsync(
        StrategyTrainingDatasetRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/strategy-training/dataset",
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StrategyTrainingDatasetDto>(cancellationToken);
    }

    public async Task<StrategyTrainingRunDto?> RunStrategyTrainingAsync(
        StrategyTrainingRunRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/strategy-training/run",
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StrategyTrainingRunDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<StrategyParameterProfileDto>> GetStrategyParameterProfilesAsync(
        string? strategyCode,
        CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(strategyCode)
            ? "/api/strategy-parameters"
            : $"/api/strategy-parameters?strategyCode={Uri.EscapeDataString(strategyCode.Trim())}";
        return await _httpClient.GetFromJsonAsync<StrategyParameterProfileDto[]>(path, cancellationToken) ?? [];
    }

    public async Task<StrategyParameterProfileDto?> SaveStrategyParameterProfileAsync(
        SaveStrategyParameterProfileRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/strategy-parameters",
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StrategyParameterProfileDto>(cancellationToken);
    }

    public async Task<StrategyParameterProfileDto?> ActivateStrategyParameterProfileAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            $"/api/strategy-parameters/{id}/activate",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StrategyParameterProfileDto>(cancellationToken);
    }

    public async Task<QlibSignalStatusDto?> GetQlibR013SignalStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<QlibSignalStatusDto>(
            "/api/qlib-signals/r013/status",
            cancellationToken);
    }

    public async Task<QlibSignalSnapshotDto?> GetQlibR013LatestAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<QlibSignalSnapshotDto>(
            "/api/qlib-signals/r013/latest",
            cancellationToken);
    }

    public async Task<QlibSignalSnapshotDto?> GetQlibR013RebalancePlanAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<QlibSignalSnapshotDto>(
            "/api/qlib-signals/r013/rebalance-plan",
            cancellationToken);
    }

    public async Task<IReadOnlyList<QlibSignalSeedDto>> GetQlibR013SeedsAsync(
        DateOnly? signalDate,
        int count,
        CancellationToken cancellationToken)
    {
        var path = signalDate.HasValue
            ? $"/api/qlib-signals/r013/seeds?signalDate={Uri.EscapeDataString(signalDate.Value.ToString("yyyy-MM-dd"))}&count={count}"
            : $"/api/qlib-signals/r013/seeds?count={count}";

        return await _httpClient.GetFromJsonAsync<QlibSignalSeedDto[]>(
            path,
            cancellationToken) ?? [];
    }

    public async Task<QlibSignalSeedImportResultDto?> ImportQlibR013SeedsAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            "/api/qlib-signals/r013/import-seeds",
            content: null,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<QlibSignalSeedImportResultDto>(cancellationToken);
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


