using AShareRadar.Application.MarketData;

namespace AShareRadar.Application.Monitoring;

public sealed class DailyLimitUpExclusionService
{
    private readonly object _gate = new();
    private DateOnly? _tradingDate;
    private readonly HashSet<string> _symbols = new(StringComparer.OrdinalIgnoreCase);

    public void MarkLimitUp(DateOnly tradingDate, IEnumerable<string> symbols)
    {
        lock (_gate)
        {
            EnsureTradingDate(tradingDate);
            foreach (var symbol in symbols)
            {
                var normalized = StockSymbolNormalizer.NormalizeCode(symbol);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    _symbols.Add(normalized);
                }
            }
        }
    }

    public bool IsExcluded(DateOnly tradingDate, string symbol)
    {
        lock (_gate)
        {
            EnsureTradingDate(tradingDate);
            return _symbols.Contains(StockSymbolNormalizer.NormalizeCode(symbol));
        }
    }

    public IReadOnlySet<string> GetExcludedSymbols(DateOnly tradingDate)
    {
        lock (_gate)
        {
            EnsureTradingDate(tradingDate);
            return _symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void EnsureTradingDate(DateOnly tradingDate)
    {
        if (_tradingDate == tradingDate)
        {
            return;
        }

        _tradingDate = tradingDate;
        _symbols.Clear();
    }
}
