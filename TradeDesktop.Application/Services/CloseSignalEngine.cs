using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public sealed class CloseSignalEngine : ICloseSignalEngine
{
    // Diagnostic samples are included in the trigger log only. Trading decisions use
    // TickCount/WindowStartUtc, so a long suspended TP window cannot grow memory forever.
    private const int MaxTpDiagnosticSamples = 1_024;

    private readonly GapSignalConfirmationEngine.SideWindowState _buyState = new();
    private readonly GapSignalConfirmationEngine.SideWindowState _sellState = new();
    private readonly GapCycleState _closeByGapSellCycle = new();
    private readonly GapCycleState _closeByGapBuyCycle = new();
    private readonly TpWindowState _tpState = new();
    private readonly ISlotLogger? _logger;
    private int? _slotId;

    public CloseSignalEngine(ISlotLogger? logger = null)
    {
        _logger = logger;
    }

    internal void SetSlotId(int slotId) => _slotId = slotId;

    public GapSignalTriggerResult? ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        double? slotProfit = null)
    {
        var normalizedCloseConfirm = Math.Abs(config.CloseConfirmGapPts);
        var normalizedClose = Math.Abs(config.ClosePts);
        var normalizedHoldMs = Math.Max(0, config.CloseHoldConfirmMs);
        var usesSos = config.CloseGapMode == CloseGapMode.Sos;

        var gapResult = !usesSos && config.CloseGapStability is not null
            ? ProcessStableGap(
                snapshot,
                config,
                config.CloseGapStability,
                openMode,
                normalizedCloseConfirm,
                normalizedClose,
                normalizedHoldMs)
            : openMode switch
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
                isConfirmSatisfied: value => usesSos
                    ? value <= normalizedCloseConfirm
                    : value <= -normalizedCloseConfirm,
                isOpenSatisfied: value => usesSos
                    ? value <= normalizedClose
                    : value <= -normalizedClose,
                limitMaxGap: config.LimitMaxGap),

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
                isConfirmSatisfied: value => usesSos
                    ? value >= -normalizedCloseConfirm
                    : value >= normalizedCloseConfirm,
                isOpenSatisfied: value => usesSos
                    ? value >= -normalizedClose
                    : value >= normalizedClose,
                limitMaxGap: config.LimitMaxGap),

            _ => null
        };

        var tpResult = ProcessTp(snapshot, config, openMode, slotProfit);

        if (tpResult is not null)
        {
            return tpResult;
        }

        if (gapResult is null)
        {
            return null;
        }

        var closesByGapSell = gapResult.TriggerType == GapSignalTriggerType.CloseByGapSell;
        var effectiveConfirm = closesByGapSell
            ? usesSos ? normalizedCloseConfirm : -normalizedCloseConfirm
            : usesSos ? -normalizedCloseConfirm : normalizedCloseConfirm;
        var effectiveClose = closesByGapSell
            ? usesSos ? normalizedClose : -normalizedClose
            : usesSos ? -normalizedClose : normalizedClose;

        return gapResult with
        {
            CloseGapMode = config.CloseGapMode,
            EffectiveCloseConfirmGapPts = effectiveConfirm,
            EffectiveCloseGapPts = effectiveClose,
            EffectiveCloseHoldMs = normalizedHoldMs
        };
    }

    public void Reset()
    {
        ResetGapState();
        _tpState.Reset();
    }

    public void ResetGapState()
    {
        _buyState.Reset();
        _sellState.Reset();
        LogCloseTransition("BUY", _closeByGapSellCycle.Reset("Reset Normal Close GapSell Cycle."), null);
        LogCloseTransition("SELL", _closeByGapBuyCycle.Reset("Reset Normal Close GapBuy Cycle."), null);
    }

    private GapSignalTriggerResult? ProcessStableGap(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        GapStabilityConfig stabilityConfig,
        TradingOpenMode openMode,
        int normalizedCloseConfirm,
        int normalizedClose,
        int normalizedHoldMs)
    {
        return openMode switch
        {
            TradingOpenMode.GapBuy => ProcessStableCloseSide(
                snapshot,
                config,
                stabilityConfig,
                _closeByGapSellCycle,
                snapshot.GapSell,
                hasRequiredData: snapshot.GapSell.HasValue
                    && snapshot.ExchangeABid.HasValue
                    && snapshot.ExchangeBAsk.HasValue,
                confirmSatisfied: snapshot.GapSell is int sellGap
                    && sellGap <= -normalizedCloseConfirm,
                closeSatisfied: value => value <= -normalizedClose,
                GapSignalTriggerType.CloseByGapSell,
                GapSignalSide.Buy,
                normalizedCloseHoldMs: normalizedHoldMs,
                effectiveConfirm: -normalizedCloseConfirm,
                effectiveClose: -normalizedClose),

            TradingOpenMode.GapSell => ProcessStableCloseSide(
                snapshot,
                config,
                stabilityConfig,
                _closeByGapBuyCycle,
                snapshot.GapBuy,
                hasRequiredData: snapshot.GapBuy.HasValue
                    && snapshot.ExchangeAAsk.HasValue
                    && snapshot.ExchangeBBid.HasValue,
                confirmSatisfied: snapshot.GapBuy is int buyGap
                    && buyGap >= normalizedCloseConfirm,
                closeSatisfied: value => value >= normalizedClose,
                GapSignalTriggerType.CloseByGapBuy,
                GapSignalSide.Sell,
                normalizedCloseHoldMs: normalizedHoldMs,
                effectiveConfirm: normalizedCloseConfirm,
                effectiveClose: normalizedClose),

            _ => null
        };
    }

    private GapSignalTriggerResult? ProcessStableCloseSide(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        GapStabilityConfig stabilityConfig,
        GapCycleState state,
        int? primaryGap,
        bool hasRequiredData,
        bool confirmSatisfied,
        Func<int, bool> closeSatisfied,
        GapSignalTriggerType triggerType,
        GapSignalSide positionSide,
        int normalizedCloseHoldMs,
        int effectiveConfirm,
        int effectiveClose)
    {
        var update = state.Process(
            snapshot.TimestampUtc,
            primaryGap,
            hasRequiredData,
            confirmSatisfied,
            stabilityConfig,
            normalizedCloseHoldMs,
            config.LimitMaxGap);
        var sideName = positionSide == GapSignalSide.Buy ? "BUY" : "SELL";
        LogCloseTransition(sideName, update, primaryGap);
        var cycle = update.CurrentCycle;
        if (cycle.Status != GapCycleStatus.Stable || cycle.Gaps.Count == 0)
        {
            return null;
        }

        var lastGap = cycle.Gaps[^1];
        if (!closeSatisfied(lastGap))
        {
            return null;
        }

        var normalizedMaxTimesTick = Math.Max(0, config.CloseMaxTimesTick);
        if (normalizedMaxTimesTick > 0 && cycle.Gaps.Count > normalizedMaxTimesTick)
        {
            state.Reset("Normal Close Cycle vượt close_max_times_tick.");
            return null;
        }

        var closesByGapSell = triggerType == GapSignalTriggerType.CloseByGapSell;
        var result = new GapSignalTriggerResult(
            Triggered: true,
            Action: GapSignalAction.Close,
            TriggerType: triggerType,
            PrimarySide: positionSide,
            BuyGaps: closesByGapSell ? [] : cycle.Gaps.ToArray(),
            SellGaps: closesByGapSell ? cycle.Gaps.ToArray() : [],
            LastBuyGap: closesByGapSell ? null : lastGap,
            LastSellGap: closesByGapSell ? lastGap : null,
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
            CloseGapMode: CloseGapMode.Normal,
            EffectiveCloseConfirmGapPts: effectiveConfirm,
            EffectiveCloseGapPts: effectiveClose,
            EffectiveCloseHoldMs: normalizedCloseHoldMs);

        GapCycleDiagnostics.LogTrigger(
            _logger, "CLOSE", sideName, _slotId, cycle, lastGap, "Normal Close trigger đã phát.");
        LogCloseTransition(
            sideName,
            state.Reset("Normal Close trigger đã phát; reset Cycle."),
            lastGap);
        return result;
    }

    private void LogCloseTransition(string side, GapCycleUpdateResult update, int? newGap) =>
        GapCycleDiagnostics.LogTransition(_logger, "CLOSE", side, _slotId, update, newGap);

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

        var limitMaxTp = Math.Abs(config.LimitMaxTp);
        if (limitMaxTp > 0d && currentProfit > limitMaxTp)
        {
            _tpState.Reset();
            return null;
        }

        if (!_tpState.WindowStartUtc.HasValue)
        {
            _tpState.WindowStartUtc = snapshot.TimestampUtc;
            _tpState.DiagnosticProfits.Clear();
        }

        _tpState.LastTickUtc = snapshot.TimestampUtc;
        _tpState.Add(currentProfit, MaxTpDiagnosticSamples);

        var elapsedMs = (snapshot.TimestampUtc - _tpState.WindowStartUtc.Value).TotalMilliseconds;
        if (elapsedMs < Math.Max(0, config.CloseHoldConfirmMs))
        {
            return null;
        }

        if (_tpState.TickCount == 0)
        {
            _tpState.Reset();
            return null;
        }

        var lastProfit = currentProfit;
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
        if (normalizedMaxTimesTick > 0 && _tpState.TickCount > normalizedMaxTimesTick)
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
            CloseTpProfits: _tpState.DiagnosticProfits.ToArray());

        _tpState.Reset();
        return result;
    }

    private sealed class TpWindowState
    {
        public DateTime? WindowStartUtc { get; set; }
        public DateTime? LastTickUtc { get; set; }
        public long TickCount { get; private set; }
        public Queue<double> DiagnosticProfits { get; } = [];

        public void Add(double profit, int maxDiagnosticSamples)
        {
            TickCount++;
            if (DiagnosticProfits.Count == maxDiagnosticSamples)
            {
                DiagnosticProfits.Dequeue();
            }

            DiagnosticProfits.Enqueue(profit);
        }

        public void Reset()
        {
            WindowStartUtc = null;
            LastTickUtc = null;
            TickCount = 0;
            DiagnosticProfits.Clear();
        }
    }
}
