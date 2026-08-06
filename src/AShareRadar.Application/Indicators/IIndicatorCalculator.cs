using AShareRadar.Application.MarketData;

namespace AShareRadar.Application.Indicators;

public interface IIndicatorCalculator
{
    IndicatorSeries Calculate(IReadOnlyList<KLineBar> bars, string indicatorType);
}
