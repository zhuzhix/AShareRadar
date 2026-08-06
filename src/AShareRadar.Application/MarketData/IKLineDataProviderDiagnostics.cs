namespace AShareRadar.Application.MarketData;

public interface IKLineDataProviderDiagnostics
{
    bool LastFallbackUsed { get; }

    void Reset();
}
