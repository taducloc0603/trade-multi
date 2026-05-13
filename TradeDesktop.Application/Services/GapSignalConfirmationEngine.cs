using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public sealed class GapSignalConfirmationEngine : IGapSignalConfirmationEngine, IOpenSignalEngine
{
    private readonly SideWindowState _buyState = new();
    private readonly SideWindowState _sellState = new();

    public IReadOnlyList<GapSignalTriggerResult> ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config)
    {
        var normalizedConfirm = Math.Abs(config.ConfirmGapPts);
        var normalizedOpen = Math.Abs(config.OpenPts);
        var normalizedHoldMs = Math.Max(0, config.HoldConfirmMs);

        var results = new List<GapSignalTriggerResult>(capacity: 2);

        var buyResult = ProcessSide(
            triggerType: GapSignalTriggerType.OpenByGapBuy,
            side: GapSignalSide.Buy,
            action: GapSignalAction.Open,
            exchangeABid: snapshot.ExchangeABid,
            exchangeAAsk: snapshot.ExchangeAAsk,
            exchangeBBid: snapshot.ExchangeBBid,
            exchangeBAsk: snapshot.ExchangeBAsk,
            gapBuy: snapshot.GapBuy,
            gapSell: snapshot.GapSell,
            pointMultiplier: snapshot.PointMultiplier,
            primaryGap: snapshot.GapBuy,
            timestampUtc: snapshot.TimestampUtc,
            state: _buyState,
            holdConfirmMs: normalizedHoldMs,
            maxTimesTick: config.OpenMaxTimesTick,
            isConfirmSatisfied: value => value >= normalizedConfirm,
            isOpenSatisfied: value => value >= normalizedOpen);
        if (buyResult is not null)
        {
            results.Add(buyResult);
        }

        var sellResult = ProcessSide(
            triggerType: GapSignalTriggerType.OpenByGapSell,
            side: GapSignalSide.Sell,
            action: GapSignalAction.Open,
            exchangeABid: snapshot.ExchangeABid,
            exchangeAAsk: snapshot.ExchangeAAsk,
            exchangeBBid: snapshot.ExchangeBBid,
            exchangeBAsk: snapshot.ExchangeBAsk,
            gapBuy: snapshot.GapBuy,
            gapSell: snapshot.GapSell,
            pointMultiplier: snapshot.PointMultiplier,
            primaryGap: snapshot.GapSell,
            timestampUtc: snapshot.TimestampUtc,
            state: _sellState,
            holdConfirmMs: normalizedHoldMs,
            maxTimesTick: config.OpenMaxTimesTick,
            isConfirmSatisfied: value => value <= -normalizedConfirm,
            isOpenSatisfied: value => value <= -normalizedOpen);
        if (sellResult is not null)
        {
            results.Add(sellResult);
        }

        return results;
    }

    public void Reset()
    {
        _buyState.Reset();
        _sellState.Reset();
    }

    internal static GapSignalTriggerResult? ProcessSide(
        GapSignalTriggerType triggerType,
        GapSignalSide side,
        GapSignalAction action,
        decimal? exchangeABid,
        decimal? exchangeAAsk,
        decimal? exchangeBBid,
        decimal? exchangeBAsk,
        int? gapBuy,
        int? gapSell,
        int pointMultiplier,
        int? primaryGap,
        DateTime timestampUtc,
        SideWindowState state,
        int holdConfirmMs,
        int maxTimesTick,
        Func<int, bool> isConfirmSatisfied,
        Func<int, bool> isOpenSatisfied)
    {
        if (!primaryGap.HasValue || !isConfirmSatisfied(primaryGap.Value))
        {
            state.Reset();
            return null;
        }

        var normalizedBuyGap = gapBuy ?? 0;
        var normalizedSellGap = gapSell ?? 0;

        if (!state.WindowStartUtc.HasValue)
        {
            state.WindowStartUtc = timestampUtc;
            state.BuyGaps.Clear();
            state.SellGaps.Clear();
        }

        state.LastTickUtc = timestampUtc;
        state.BuyGaps.Add(normalizedBuyGap);
        state.SellGaps.Add(normalizedSellGap);

        var elapsedMs = (timestampUtc - state.WindowStartUtc.Value).TotalMilliseconds;
        if (elapsedMs < holdConfirmMs)
        {
            return null;
        }

        var primaryGaps = side == GapSignalSide.Buy ? state.BuyGaps : state.SellGaps;
        if (primaryGaps.Count == 0 || primaryGaps.Any(v => !isConfirmSatisfied(v)))
        {
            state.Reset();
            return null;
        }

        var lastGap = primaryGaps[^1];
        if (!isOpenSatisfied(lastGap))
        {
            state.Reset();
            return null;
        }

        var normalizedMaxTimesTick = Math.Max(0, maxTimesTick);
        if (normalizedMaxTimesTick > 0 && primaryGaps.Count > normalizedMaxTimesTick)
        {
            state.Reset();
            return null;
        }

        var result = new GapSignalTriggerResult(
            Triggered: true,
            Action: action,
            TriggerType: triggerType,
            PrimarySide: side,
            BuyGaps: state.BuyGaps.ToArray(),
            SellGaps: state.SellGaps.ToArray(),
            LastBuyGap: state.BuyGaps.Count > 0 ? state.BuyGaps[^1] : null,
            LastSellGap: state.SellGaps.Count > 0 ? state.SellGaps[^1] : null,
            TriggeredAtUtc: timestampUtc,
            LastABid: exchangeABid,
            LastAAsk: exchangeAAsk,
            LastBBid: exchangeBBid,
            LastBAsk: exchangeBAsk,
            GapBuySourceBBid: exchangeBBid,
            GapBuySourceAAsk: exchangeAAsk,
            GapSellSourceBAsk: exchangeBAsk,
            GapSellSourceABid: exchangeABid,
            PointMultiplier: pointMultiplier);

        state.Reset();
        return result;
    }

    internal sealed class SideWindowState
    {
        public DateTime? WindowStartUtc { get; set; }
        public DateTime? LastTickUtc { get; set; }
        public List<int> BuyGaps { get; } = [];
        public List<int> SellGaps { get; } = [];

        public void Reset()
        {
            WindowStartUtc = null;
            LastTickUtc = null;
            BuyGaps.Clear();
            SellGaps.Clear();
        }
    }
}
