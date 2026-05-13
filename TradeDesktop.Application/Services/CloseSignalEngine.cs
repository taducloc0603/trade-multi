using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public sealed class CloseSignalEngine : ICloseSignalEngine
{
    private readonly GapSignalConfirmationEngine.SideWindowState _buyState = new();
    private readonly GapSignalConfirmationEngine.SideWindowState _sellState = new();

    public GapSignalTriggerResult? ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode)
    {
        var normalizedCloseConfirm = Math.Abs(config.CloseConfirmGapPts);
        var normalizedClose = Math.Abs(config.ClosePts);
        var normalizedHoldMs = Math.Max(0, config.CloseHoldConfirmMs);

        return openMode switch
        {
            TradingOpenMode.GapBuy => GapSignalConfirmationEngine.ProcessSide(
                triggerType: GapSignalTriggerType.CloseByGapSell,
                side: GapSignalSide.Buy,
                action: GapSignalAction.Close,
                exchangeABid: snapshot.ExchangeABid,
                exchangeAAsk: snapshot.ExchangeAAsk,
                exchangeBBid: snapshot.ExchangeBBid,
                exchangeBAsk: snapshot.ExchangeBAsk,
                gapBuy: snapshot.GapBuy,
                gapSell: snapshot.GapSell,
                pointMultiplier: snapshot.PointMultiplier,
                primaryGap: snapshot.GapSell,
                timestampUtc: snapshot.TimestampUtc,
                state: _buyState,
                holdConfirmMs: normalizedHoldMs,
                maxTimesTick: config.CloseMaxTimesTick,
                isConfirmSatisfied: value => value <= -normalizedCloseConfirm,
                isOpenSatisfied: value => value <= -normalizedClose),

            TradingOpenMode.GapSell => GapSignalConfirmationEngine.ProcessSide(
                triggerType: GapSignalTriggerType.CloseByGapBuy,
                side: GapSignalSide.Sell,
                action: GapSignalAction.Close,
                exchangeABid: snapshot.ExchangeABid,
                exchangeAAsk: snapshot.ExchangeAAsk,
                exchangeBBid: snapshot.ExchangeBBid,
                exchangeBAsk: snapshot.ExchangeBAsk,
                gapBuy: snapshot.GapBuy,
                gapSell: snapshot.GapSell,
                pointMultiplier: snapshot.PointMultiplier,
                primaryGap: snapshot.GapBuy,
                timestampUtc: snapshot.TimestampUtc,
                state: _sellState,
                holdConfirmMs: normalizedHoldMs,
                maxTimesTick: config.CloseMaxTimesTick,
                isConfirmSatisfied: value => value >= normalizedCloseConfirm,
                isOpenSatisfied: value => value >= normalizedClose),

            _ => null
        };
    }

    public void Reset()
    {
        _buyState.Reset();
        _sellState.Reset();
    }
}
