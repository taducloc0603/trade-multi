using TradeDesktop.Application.Models;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Application.Services;

public interface IGapCalculator
{
    (int? GapBuy, int? GapSell) Calculate(ExchangeMetrics sanA, ExchangeMetrics sanB);

    // Gap giữa 1 sàn (primary) và sàn tham chiếu (reference):
    // GapBuy = reference.Bid - primary.Ask, GapSell = primary.Bid - reference.Ask.
    // Dùng cho gap A-C/B-C (monitor-only). KHÔNG dùng cho A-B (xem Calculate).
    (int? GapBuy, int? GapSell) CalculateVsReference(ExchangeMetrics primary, ExchangeMetrics reference);
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

    public (int? GapBuy, int? GapSell) CalculateVsReference(ExchangeMetrics primary, ExchangeMetrics reference)
    {
        var pointMultiplier = runtimeConfigProvider.CurrentPoint > 0
            ? runtimeConfigProvider.CurrentPoint
            : 1;

        int? gapBuy = null;
        int? gapSell = null;

        // GapBuy = reference.Bid - primary.Ask (vd A-C: C.Bid - A.Ask).
        if (reference.Bid.HasValue && primary.Ask.HasValue)
        {
            var referenceBidPts = (int)(reference.Bid.Value * pointMultiplier);
            var primaryAskPts = (int)(primary.Ask.Value * pointMultiplier);
            gapBuy = referenceBidPts - primaryAskPts;
        }

        // GapSell = primary.Bid - reference.Ask (vd A-C: A.Bid - C.Ask).
        if (primary.Bid.HasValue && reference.Ask.HasValue)
        {
            var primaryBidPts = (int)(primary.Bid.Value * pointMultiplier);
            var referenceAskPts = (int)(reference.Ask.Value * pointMultiplier);
            gapSell = primaryBidPts - referenceAskPts;
        }

        return (gapBuy, gapSell);
    }
}
