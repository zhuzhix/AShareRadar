using AShareRadar.Application.MarketData;

namespace AShareRadar.Infrastructure.MarketData;

public sealed class EastMoneyQuantDotNetAuctionProvider : IAuctionDataProvider
{
    private readonly EastMoneyQuantDotNetClient _client;

    public EastMoneyQuantDotNetAuctionProvider(EastMoneyQuantDotNetClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlyList<AuctionTickSnapshot>> LoadCurrentAuctionAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        var snapshots = await _client.LoadCurrentAuctionAsync(symbols, cancellationToken);
        return snapshots
            .Select(snapshot => new AuctionTickSnapshot(
                snapshot.Symbol,
                snapshot.Name,
                snapshot.EventTime,
                snapshot.Price,
                snapshot.PreClose,
                snapshot.CumVolume,
                snapshot.CumAmount,
                snapshot.Quotes
                    .Select(level => new AuctionQuoteLevel(level.Price, level.Volume, level.IsBid))
                    .ToArray()))
            .ToArray();
    }
}
