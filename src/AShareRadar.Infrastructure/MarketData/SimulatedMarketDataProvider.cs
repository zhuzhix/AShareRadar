using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class SimulatedMarketDataProvider : IMarketDataProvider
{
    private readonly Random _random = new(20260727);
    private int _tick;

    public string ProviderName => "Simulation";

    public Task<MarketSnapshot> LoadMarketSnapshotAsync(CancellationToken cancellationToken)
    {
        _tick++;
        var now = DateTimeOffset.Now;

        var quotes = new[]
        {
            CreateQuote("300750", "宁德时代", 188.20m, now, 0),
            CreateQuote("600519", "贵州茅台", 1428.00m, now, 1),
            CreateQuote("000001", "平安银行", 10.42m, now, 2),
            CreateQuote("002415", "海康威视", 31.80m, now, 3),
            CreateQuote("300059", "东方财富", 13.26m, now, 4),
            CreateQuote("600000", "浦发银行", 9.05m, now, 5),
            CreateQuote("002230", "科大讯飞", 42.18m, now, 6),
            CreateQuote("601318", "中国平安", 46.70m, now, 7)
        };

        return Task.FromResult(new MarketSnapshot(now, ProviderName, quotes));
    }

    private StockQuote CreateQuote(string symbol, string name, decimal basePrice, DateTimeOffset now, int offset)
    {
        var wave = (decimal)Math.Sin((_tick + offset) / 3.0) * 1.8m;
        var noise = (decimal)(_random.NextDouble() - 0.5) * 0.4m;
        var changePercent = decimal.Round(wave + noise, 2);
        var price = decimal.Round(basePrice * (1 + changePercent / 100), 2);
        var volumeRatio = decimal.Round(0.8m + Math.Abs(changePercent) / 2 + offset * 0.08m, 2);
        var turnoverRate = decimal.Round(0.4m + Math.Abs(changePercent) / 3 + offset * 0.05m, 2);
        var amount = decimal.Round((offset + 1) * 120_000_000m * (1 + Math.Abs(changePercent) / 10), 0);

        return new StockQuote(
            symbol,
            name,
            price,
            changePercent,
            volumeRatio,
            turnoverRate,
            amount,
            now);
    }
}
