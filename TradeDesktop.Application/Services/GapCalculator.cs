using TradeDesktop.Application.Models;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Application.Services;

public interface IGapCalculator
{
    (int? GapBuy, int? GapSell) Calculate(ExchangeMetrics sanA, ExchangeMetrics sanB);
}

public sealed class GapCalculator(IRuntimeConfigProvider runtimeConfigProvider) : IGapCalculator
{
    public (int? GapBuy, int? GapSell) Calculate(ExchangeMetrics sanA, ExchangeMetrics sanB)
    {
        var pointMultiplier = runtimeConfigProvider.CurrentPoint > 0
            ? runtimeConfigProvider.CurrentPoint
            : 1;

        int? gapBuy = null;
        int? gapSell = null;

        if (sanB.Bid.HasValue && sanA.Ask.HasValue)
        {
            var bBidPts = (int)(sanB.Bid.Value * pointMultiplier);
            var aAskPts = (int)(sanA.Ask.Value * pointMultiplier);
            gapBuy = bBidPts - aAskPts;
        }

        if (sanB.Ask.HasValue && sanA.Bid.HasValue)
        {
            var bAskPts = (int)(sanB.Ask.Value * pointMultiplier);
            var aBidPts = (int)(sanA.Bid.Value * pointMultiplier);
            gapSell = bAskPts - aBidPts;
        }

        return (gapBuy, gapSell);
    }
}
