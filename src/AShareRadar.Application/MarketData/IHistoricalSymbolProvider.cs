namespace AShareRadar.Application.MarketData;

public interface IHistoricalSymbolProvider
{
    Task<IReadOnlyList<string>> LoadSymbolsAsync(
        string stockPool,
        int count,
        CancellationToken cancellationToken);
}
