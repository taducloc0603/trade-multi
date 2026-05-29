using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public sealed class CloseSignalEngine : ICloseSignalEngine
{
    private readonly GapSignalConfirmationEngine.SideWindowState _buyState = new();
    private readonly GapSignalConfirmationEngine.SideWindowState _sellState = new();
    private readonly TpWindowState _tpState = new();

    public GapSignalTriggerResult? ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        double? slotProfit = null)
    {
        var normalizedCloseConfirm = Math.Abs(config.CloseConfirmGapPts);
        var normalizedClose = Math.Abs(config.ClosePts);
        var normalizedHoldMs = Math.Max(0, config.CloseHoldConfirmMs);

        var gapResult = openMode switch
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

        var tpResult = ProcessTp(snapshot, config, openMode, slotProfit);

        return tpResult ?? gapResult;
    }

    public void Reset()
    {
        _buyState.Reset();
        _sellState.Reset();
        _tpState.Reset();
    }

    private GapSignalTriggerResult? ProcessTp(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        double? slotProfit)
    {
        if (openMode == TradingOpenMode.None || !slotProfit.HasValue)
        {
            _tpState.Reset();
            return null;
        }

        var confirmProfit = Math.Abs(config.CloseConfirmTpProfit);
        var targetProfit = Math.Abs(config.CloseTpProfit);
        if (targetProfit <= 0d)
        {
            _tpState.Reset();
            return null;
        }

        var currentProfit = slotProfit.Value;
        if (currentProfit < confirmProfit)
        {
            _tpState.Reset();
            return null;
        }

        if (!_tpState.WindowStartUtc.HasValue)
        {
            _tpState.WindowStartUtc = snapshot.TimestampUtc;
            _tpState.Profits.Clear();
        }

        _tpState.LastTickUtc = snapshot.TimestampUtc;
        _tpState.Profits.Add(currentProfit);

        var elapsedMs = (snapshot.TimestampUtc - _tpState.WindowStartUtc.Value).TotalMilliseconds;
        if (elapsedMs < Math.Max(0, config.CloseHoldConfirmMs))
        {
            return null;
        }

        if (_tpState.Profits.Count == 0 || _tpState.Profits.Any(v => v < confirmProfit))
        {
            _tpState.Reset();
            return null;
        }

        var lastProfit = _tpState.Profits[^1];
        if (lastProfit < targetProfit)
        {
            _tpState.Reset();
            return null;
        }

        var maxTpProfit = Math.Abs(config.CloseMaxTpProfit);
        if (maxTpProfit > 0d && lastProfit > maxTpProfit)
        {
            return null;
        }

        var normalizedMaxTimesTick = Math.Max(0, config.CloseMaxTimesTick);
        if (normalizedMaxTimesTick > 0 && _tpState.Profits.Count > normalizedMaxTimesTick)
        {
            _tpState.Reset();
            return null;
        }

        var isClosingBuyPosition = openMode == TradingOpenMode.GapBuy;
        var result = new GapSignalTriggerResult(
            Triggered: true,
            Action: GapSignalAction.Close,
            TriggerType: isClosingBuyPosition
                ? GapSignalTriggerType.CloseByGapSell
                : GapSignalTriggerType.CloseByGapBuy,
            PrimarySide: isClosingBuyPosition ? GapSignalSide.Buy : GapSignalSide.Sell,
            BuyGaps: snapshot.GapBuy.HasValue ? [snapshot.GapBuy.Value] : [],
            SellGaps: snapshot.GapSell.HasValue ? [snapshot.GapSell.Value] : [],
            LastBuyGap: snapshot.GapBuy,
            LastSellGap: snapshot.GapSell,
            TriggeredAtUtc: snapshot.TimestampUtc,
            LastABid: snapshot.ExchangeABid,
            LastAAsk: snapshot.ExchangeAAsk,
            LastBBid: snapshot.ExchangeBBid,
            LastBAsk: snapshot.ExchangeBAsk,
            GapBuySourceBBid: snapshot.ExchangeBBid,
            GapBuySourceAAsk: snapshot.ExchangeAAsk,
            GapSellSourceBAsk: snapshot.ExchangeBAsk,
            GapSellSourceABid: snapshot.ExchangeABid,
            PointMultiplier: snapshot.PointMultiplier,
            CloseReason: CloseSignalReason.Tp,
            CloseTpProfit: lastProfit,
            CloseTpTarget: targetProfit,
            CloseTpProfits: _tpState.Profits.ToArray());

        _tpState.Reset();
        return result;
    }

    private sealed class TpWindowState
    {
        public DateTime? WindowStartUtc { get; set; }
        public DateTime? LastTickUtc { get; set; }
        public List<double> Profits { get; } = [];

        public void Reset()
        {
            WindowStartUtc = null;
            LastTickUtc = null;
            Profits.Clear();
        }
    }
}
