namespace AShareRadar.Application.MarketData;

public static class StockSymbolNormalizer
{
    public static string NormalizeCode(string symbol)
    {
        var value = symbol.Trim().ToLowerInvariant();
        if ((value.StartsWith("sh.") || value.StartsWith("sz.")) && value.Length == 9)
        {
            value = value[3..];
        }
        else if ((value.StartsWith("shse.") || value.StartsWith("szse.")) && value.Length == 11)
        {
            value = value[5..];
        }
        else if ((value.StartsWith("sh") || value.StartsWith("sz")) && value.Length == 8)
        {
            value = value[2..];
        }

        return value;
    }

    public static string ToPrefixedCode(string symbol)
    {
        var code = NormalizeCode(symbol);
        if (code.Length != 6)
        {
            return code;
        }

        return code.StartsWith('6') ? "sh" + code : "sz" + code;
    }
}