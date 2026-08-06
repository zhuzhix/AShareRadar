using AShareRadar.Domain.MarketData;

namespace AShareRadar.Application.MarketData;

public enum LimitStatus
{
    None,
    LimitUp,
    LimitDown
}

public sealed record LimitPriceRule(decimal UpLimitPercent, decimal DownLimitPercent, string Board);

public static class LimitStatusCalculator
{
    private const decimal TickSize = 0.01m;
    private const decimal PreviousCloseCentTolerance = 0.002m;

    public static LimitStatus Calculate(StockQuote quote)
    {
        if (quote.Price <= 0)
        {
            return LimitStatus.None;
        }

        var rule = GetRule(quote.Symbol, quote.Name);
        var previousClose = InferPreviousClose(quote);
        if (previousClose <= 0)
        {
            return LimitStatus.None;
        }

        var limitUpPrice = RoundToCent(previousClose * (1m + rule.UpLimitPercent / 100m));
        var limitDownPrice = RoundToCent(previousClose * (1m - rule.DownLimitPercent / 100m));
        if (quote.Price >= limitUpPrice - TickSize / 2m)
        {
            return LimitStatus.LimitUp;
        }

        if (quote.Price <= limitDownPrice + TickSize / 2m)
        {
            return LimitStatus.LimitDown;
        }

        return LimitStatus.None;
    }

    public static LimitPriceRule GetRule(string symbol, string name)
    {
        var code = StockSymbolNormalizer.NormalizeCode(symbol);
        if (IsRiskWarningStock(name))
        {
            return new LimitPriceRule(5m, 5m, "ST");
        }

        if (code.StartsWith("300", StringComparison.Ordinal) ||
            code.StartsWith("301", StringComparison.Ordinal) ||
            code.StartsWith("688", StringComparison.Ordinal) ||
            code.StartsWith("689", StringComparison.Ordinal))
        {
            return new LimitPriceRule(20m, 20m, "20cm");
        }

        if (code.StartsWith("43", StringComparison.Ordinal) ||
            code.StartsWith("83", StringComparison.Ordinal) ||
            code.StartsWith("87", StringComparison.Ordinal) ||
            code.StartsWith("88", StringComparison.Ordinal))
        {
            return new LimitPriceRule(30m, 30m, "30cm");
        }

        return new LimitPriceRule(10m, 10m, "10cm");
    }

    private static bool IsRiskWarningStock(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("ST", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("退", StringComparison.Ordinal);
    }

    private static decimal InferPreviousClose(StockQuote quote)
    {
        var ratio = 1m + quote.ChangePercent / 100m;
        if (ratio <= 0)
        {
            return 0m;
        }

        var inferred = quote.Price / ratio;
        var rounded = RoundToCent(inferred);
        return Math.Abs(inferred - rounded) <= PreviousCloseCentTolerance ? rounded : 0m;
    }

    private static decimal RoundToCent(decimal value)
    {
        return Math.Round(value / TickSize, 0, MidpointRounding.AwayFromZero) * TickSize;
    }
}
