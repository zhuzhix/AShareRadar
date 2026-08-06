using System.Text.Json;
using AShareRadar.Application.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyLimitPoolProvider : ILimitPoolProvider
{
    private readonly HttpClient _httpClient;
    private MarketSentimentDataSourceStatus _status = MarketSentimentDataSourceStatus.Unavailable(
        "EastMoneyLimitPool",
        "EastMoney limit up/down pool has not been refreshed.");

    public EastMoneyLimitPoolProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string ProviderName => "EastMoneyLimitPool";

    public async Task<LimitPoolSnapshot?> LoadAsync(DateOnly tradingDate, CancellationToken cancellationToken)
    {
        try
        {
            var date = tradingDate.ToString("yyyyMMdd");
            var limitUpTask = LoadPoolCountAsync("getTopicZTPool", "fbt:asc", date, cancellationToken);
            var limitDownTask = LoadPoolCountAsync("getTopicDTPool", "fund:asc", date, cancellationToken);

            await Task.WhenAll(limitUpTask, limitDownTask);

            var snapshot = new LimitPoolSnapshot(
                tradingDate,
                limitUpTask.Result,
                limitDownTask.Result,
                ProviderName);

            _status = MarketSentimentDataSourceStatus.Available(
                "EastMoneyLimitPool",
                $"Loaded limit pool counts: up {snapshot.LimitUpCount}, down {snapshot.LimitDownCount}.");

            return snapshot;
        }
        catch (Exception ex)
        {
            _status = MarketSentimentDataSourceStatus.Unavailable(
                "EastMoneyLimitPool",
                $"EastMoney limit pool unavailable, fallback to local price rules. {ex.Message}");
            return null;
        }
    }

    public MarketSentimentDataSourceStatus GetStatus()
    {
        return _status;
    }

    private async Task<int> LoadPoolCountAsync(
        string endpoint,
        string sort,
        string date,
        CancellationToken cancellationToken)
    {
        var url = $"https://push2ex.eastmoney.com/{endpoint}?ut=7eea3edcaed734bea9cbfc24409ed989&dpt=wz.ztzt&Pageindex=0&pagesize=10000&sort={Uri.EscapeDataString(sort)}&date={date}&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("pool", out var pool) ||
            pool.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return pool.GetArrayLength();
    }
}

