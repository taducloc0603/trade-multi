using TradeDesktop.Application.Models;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Application.Services;

public interface IGapCalculator
{
    (int? GapBuy, int? GapSell) Calculate(ExchangeMetrics sanA, ExchangeMetrics sanB);

    // Gap cùng-chân giữa 1 sàn (primary) và sàn tham chiếu (reference):
    // GapBuy = primary.Ask - reference.Ask, GapSell = primary.Bid - reference.Bid.
    // Dùng cho gap A-C/B-C (monitor-only). KHÔNG dùng cho A-B (A-B chéo chân, xem Calculate).
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

        // Buy dùng Ask, Sell dùng Bid — cùng chân, primary trừ reference.
        if (primary.Ask.HasValue && reference.Ask.HasValue)
        {
            var primaryAskPts = (int)(primary.Ask.Value * pointMultiplier);
            var referenceAskPts = (int)(reference.Ask.Value * pointMultiplier);
            gapBuy = primaryAskPts - referenceAskPts;
        }

        if (primary.Bid.HasValue && reference.Bid.HasValue)
        {
            var primaryBidPts = (int)(primary.Bid.Value * pointMultiplier);
            var referenceBidPts = (int)(reference.Bid.Value * pointMultiplier);
            gapSell = primaryBidPts - referenceBidPts;
        }

        return (gapBuy, gapSell);
    }
}
