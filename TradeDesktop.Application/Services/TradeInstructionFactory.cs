using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public sealed class TradeInstructionFactory : ITradeInstructionFactory
{
    public TradeSignalInstruction Create(GapSignalTriggerResult triggerResult)
    {
        var triggerGaps = ResolveTriggerGaps(triggerResult);
        var lastTriggerGap = ResolveLastTriggerGap(triggerResult);
        var (triggerLeftPrice, triggerRightPrice, triggerLeftLabel, triggerRightLabel) = ResolveTriggerExpression(triggerResult);
        var exchangeASide = triggerResult.PrimarySide;
        var exchangeBSide = OppositeSide(exchangeASide);

        var exchangeA = BuildLeg("A", triggerResult.Action, exchangeASide, triggerResult);
        var exchangeB = BuildLeg("B", triggerResult.Action, exchangeBSide, triggerResult);

        return new TradeSignalInstruction(
            TriggeredAtUtc: triggerResult.TriggeredAtUtc,
            TriggerType: triggerResult.TriggerType,
            Action: triggerResult.Action,
            PrimarySide: triggerResult.PrimarySide,
            TriggerGaps: triggerGaps,
            LastTriggerGap: lastTriggerGap,
            TriggerLeftPrice: triggerLeftPrice,
            TriggerRightPrice: triggerRightPrice,
            TriggerLeftLabel: triggerLeftLabel,
            TriggerRightLabel: triggerRightLabel,
            PointMultiplier: triggerResult.PointMultiplier,
            ExchangeA: exchangeA,
            ExchangeB: exchangeB);
    }

    private static (decimal? LeftPrice, decimal? RightPrice, string LeftLabel, string RightLabel) ResolveTriggerExpression(
        GapSignalTriggerResult triggerResult)
        => triggerResult.TriggerType is GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.CloseByGapBuy
            ? (triggerResult.GapBuySourceBBid, triggerResult.GapBuySourceAAsk, "B.Bid", "A.Ask")
            : (triggerResult.GapSellSourceBAsk, triggerResult.GapSellSourceABid, "B.Ask", "A.Bid");

    private static IReadOnlyList<int> ResolveTriggerGaps(GapSignalTriggerResult triggerResult)
        => triggerResult.TriggerType is GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.CloseByGapBuy
            ? triggerResult.BuyGaps
            : triggerResult.SellGaps;

    private static int? ResolveLastTriggerGap(GapSignalTriggerResult triggerResult)
        => triggerResult.TriggerType is GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.CloseByGapBuy
            ? triggerResult.LastBuyGap
            : triggerResult.LastSellGap;

    private static TradeInstructionLeg BuildLeg(
        string exchange,
        GapSignalAction action,
        GapSignalSide side,
        GapSignalTriggerResult triggerResult)
    {
        var gaps = side == GapSignalSide.Buy ? triggerResult.BuyGaps : triggerResult.SellGaps;
        var lastGap = side == GapSignalSide.Buy ? triggerResult.LastBuyGap : triggerResult.LastSellGap;
        var price = ResolveGapSourcePrice(exchange, triggerResult);

        return new TradeInstructionLeg(
            Exchange: exchange,
            Action: action,
            Side: side,
            Gaps: gaps,
            LastGap: lastGap,
            Price: price);
    }

    private static decimal? ResolveGapSourcePrice(string exchange, GapSignalTriggerResult triggerResult)
    {
        var isExchangeA = string.Equals(exchange, "A", StringComparison.OrdinalIgnoreCase);

        return triggerResult.TriggerType switch
        {
            GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.CloseByGapBuy
                => isExchangeA ? triggerResult.GapBuySourceAAsk : triggerResult.GapBuySourceBBid,
            GapSignalTriggerType.OpenByGapSell or GapSignalTriggerType.CloseByGapSell
                => isExchangeA ? triggerResult.GapSellSourceABid : triggerResult.GapSellSourceBAsk,
            _ => null
        };
    }

    private static GapSignalSide OppositeSide(GapSignalSide side)
        => side == GapSignalSide.Buy ? GapSignalSide.Sell : GapSignalSide.Buy;
}
