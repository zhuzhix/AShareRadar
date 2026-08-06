using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AShareRadar.Application.MarketData;
using AShareRadar.Domain.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyRealtimeProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly MarketDataOptions _options;

    public EastMoneyRealtimeProvider(HttpClient httpClient, MarketDataOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public string ProviderName => "EastMoney";

    public async Task<MarketSnapshot> LoadMarketSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var quotes = new List<StockQuote>();

        foreach (var symbol in _options.SeedSymbols)
        {
            try
            {
                var secId = ToEastMoneySecId(symbol);
                if (secId is null)
                {
                    continue;
                }

                var url = $"https://push2.eastmoney.com/api/qt/stock/get?secid={secId}&fields=f43,f57,f58,f170,f168,f116";
                using var payload = await _httpClient.GetFromJsonAsync<JsonDocument>(url, cancellationToken);
                if (payload is null
                    || !payload.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }

                var price = Scale(ReadDecimal(data, "f43"));
                var changePercent = Scale(ReadDecimal(data, "f170"));
                var turnoverRate = Scale(ReadDecimal(data, "f168"));
                var amount = ReadDecimal(data, "f116");
                if (price <= 0)
                {
                    continue;
                }

                quotes.Add(new StockQuote(
                    ReadString(data, "f57") ?? symbol,
                    ReadString(data, "f58") ?? symbol,
                    price,
                    changePercent,
                    VolumeRatio: 0,
                    turnoverRate,
                    amount,
                    now));
            }
            catch
            {
                continue;
            }
        }

        return new MarketSnapshot(now, ProviderName, quotes);
    }

    private static string? ToEastMoneySecId(string symbol)
    {
        if (symbol.StartsWith("sh", StringComparison.OrdinalIgnoreCase))
        {
            return "1." + symbol[2..];
        }

        if (symbol.StartsWith("sz", StringComparison.OrdinalIgnoreCase))
        {
            return "0." + symbol[2..];
        }

        if (symbol.Length == 6)
        {
            return symbol.StartsWith('6') ? "1." + symbol : "0." + symbol;
        }

        return null;
    }

    private static decimal Scale(decimal value)
    {
        return decimal.Round(value / 100m, 2);
    }

    private static decimal ReadDecimal(JsonElement data, string fieldName)
    {
        return data.TryGetProperty(fieldName, out var element) ? ReadDecimal(element) : 0;
    }

    private static decimal ReadDecimal(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.String => decimal.TryParse(element.GetString(), out var textNumber) ? textNumber : 0,
            _ => 0
        };
    }

    private static string? ReadString(JsonElement data, string fieldName)
    {
        if (!data.TryGetProperty(fieldName, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            _ => null
        };
    }
}
