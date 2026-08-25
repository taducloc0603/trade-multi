using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public sealed class GapSignalConfirmationEngine : IGapSignalConfirmationEngine, IOpenSignalEngine
{
    private readonly ISlotLogger? _logger;
    private readonly SideWindowState _buyState = new();
    private readonly SideWindowState _sellState = new();
    private readonly GapCycleState _buyCycle = new();
    private readonly GapCycleState _sellCycle = new();

    public GapSignalConfirmationEngine(ISlotLogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<GapSignalTriggerResult> ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config)
    {
        var normalizedConfirm = Math.Abs(config.ConfirmGapPts);
        var normalizedOpen = Math.Abs(config.OpenPts);
        var normalizedHoldMs = Math.Max(0, config.HoldConfirmMs);

        if (config.OpenGapStability is not null)
        {
            return ProcessStableCycles(
                snapshot,
                config,
                config.OpenGapStability,
                normalizedConfirm,
                normalizedOpen,
                normalizedHoldMs);
        }

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
            isOpenSatisfied: value => value >= normalizedOpen,
            limitMaxGap: config.LimitMaxGap);
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
            isOpenSatisfied: value => value <= -normalizedOpen,
            limitMaxGap: config.LimitMaxGap);
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
        LogOpenTransition("BUY", _buyCycle.Reset("Reset Open GapBuy Cycle."), null);
        LogOpenTransition("SELL", _sellCycle.Reset("Reset Open GapSell Cycle."), null);
    }

    private IReadOnlyList<GapSignalTriggerResult> ProcessStableCycles(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        GapStabilityConfig stabilityConfig,
        int normalizedConfirm,
        int normalizedOpen,
        int normalizedHoldMs)
    {
        var results = new List<GapSignalTriggerResult>(capacity: 2);

        var buyUpdate = _buyCycle.Process(
            snapshot.TimestampUtc,
            snapshot.GapBuy,
            hasRequiredData: snapshot.GapBuy.HasValue
                && snapshot.ExchangeAAsk.HasValue
                && snapshot.ExchangeBBid.HasValue,
            confirmSatisfied: snapshot.GapBuy is int buyGap && buyGap >= normalizedConfirm,
            stabilityConfig,
            normalizedHoldMs,
            config.LimitMaxGap);
        LogOpenTransition("BUY", buyUpdate, snapshot.GapBuy);
        var buyResult = TryCreateStableOpenResult(
            snapshot,
            config,
            buyUpdate.CurrentCycle,
            GapSignalTriggerType.OpenByGapBuy,
            GapSignalSide.Buy,
            value => value >= normalizedOpen,
            _buyCycle,
            _logger);
        if (buyResult is not null)
        {
            results.Add(buyResult);
        }

        var sellUpdate = _sellCycle.Process(
            snapshot.TimestampUtc,
            snapshot.GapSell,
            hasRequiredData: snapshot.GapSell.HasValue
                && snapshot.ExchangeABid.HasValue
                && snapshot.ExchangeBAsk.HasValue,
            confirmSatisfied: snapshot.GapSell is int sellGap && sellGap <= -normalizedConfirm,
            stabilityConfig,
            normalizedHoldMs,
            config.LimitMaxGap);
        LogOpenTransition("SELL", sellUpdate, snapshot.GapSell);
        var sellResult = TryCreateStableOpenResult(
            snapshot,
            config,
            sellUpdate.CurrentCycle,
            GapSignalTriggerType.OpenByGapSell,
            GapSignalSide.Sell,
            value => value <= -normalizedOpen,
            _sellCycle,
            _logger);
        if (sellResult is not null)
        {
            results.Add(sellResult);
        }

        return results;
    }

    private static GapSignalTriggerResult? TryCreateStableOpenResult(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        GapCycleSnapshot cycle,
        GapSignalTriggerType triggerType,
        GapSignalSide side,
        Func<int, bool> isOpenSatisfied,
        GapCycleState state,
        ISlotLogger? logger)
    {
        if (cycle.Status != GapCycleStatus.Stable || cycle.Gaps.Count == 0)
        {
            return null;
        }

        var lastGap = cycle.Gaps[^1];
        if (!isOpenSatisfied(lastGap))
        {
            return null;
        }

        var normalizedMaxTimesTick = Math.Max(0, config.OpenMaxTimesTick);
        if (normalizedMaxTimesTick > 0 && cycle.Gaps.Count > normalizedMaxTimesTick)
        {
            state.Reset("Open Cycle vượt open_max_times_tick.");
            return null;
        }

        var isBuy = side == GapSignalSide.Buy;
        var result = new GapSignalTriggerResult(
            Triggered: true,
            Action: GapSignalAction.Open,
            TriggerType: triggerType,
            PrimarySide: side,
            BuyGaps: isBuy ? cycle.Gaps.ToArray() : [],
            SellGaps: isBuy ? [] : cycle.Gaps.ToArray(),
            LastBuyGap: isBuy ? lastGap : null,
            LastSellGap: isBuy ? null : lastGap,
            TriggeredAtUtc: snapshot.TimestampUtc,
            LastABid: snapshot.ExchangeABid,
            LastAAsk: snapshot.ExchangeAAsk,
            LastBBid: snapshot.ExchangeBBid,
            LastBAsk: snapshot.ExchangeBAsk,
            GapBuySourceBBid: snapshot.ExchangeBBid,
            GapBuySourceAAsk: snapshot.ExchangeAAsk,
            GapSellSourceBAsk: snapshot.ExchangeBAsk,
            GapSellSourceABid: snapshot.ExchangeABid,
            PointMultiplier: snapshot.PointMultiplier);

        var sideName = side == GapSignalSide.Buy ? "BUY" : "SELL";
        GapCycleDiagnostics.LogTrigger(
            logger, "OPEN", sideName, null, cycle, lastGap, "Open trigger đã phát.");
        GapCycleDiagnostics.LogTransition(
            logger,
            "OPEN",
            sideName,
            null,
            state.Reset("Open trigger đã phát; reset Cycle."),
            lastGap);
        return result;
    }

    private void LogOpenTransition(string side, GapCycleUpdateResult update, int? newGap) =>
        GapCycleDiagnostics.LogTransition(_logger, "OPEN", side, null, update, newGap);

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
        Func<int, bool> isOpenSatisfied,
        int limitMaxGap = 0)
    {
        if (!primaryGap.HasValue || !isConfirmSatisfied(primaryGap.Value))
        {
            state.Reset();
            return null;
        }

        if (limitMaxGap > 0 && Math.Abs(primaryGap.Value) > limitMaxGap)
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

        var primaryGaps = triggerType switch
        {
            GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.CloseByGapBuy => state.BuyGaps,
            GapSignalTriggerType.OpenByGapSell or GapSignalTriggerType.CloseByGapSell => state.SellGaps,
            _ => side == GapSignalSide.Buy ? state.BuyGaps : state.SellGaps
        };
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
