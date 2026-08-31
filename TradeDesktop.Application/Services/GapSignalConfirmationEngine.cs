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
    private SignalCycleEventSnapshot _buyObservation = new();
    private SignalCycleEventSnapshot _sellObservation = new();
    private GapCycleDiagnostics.PolicyContext? _lastDiagnosticPolicy;

    public GapSignalConfirmationEngine(ISlotLogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<SignalCycleStatus> GetCycleStatuses() =>
    [
        CreateCycleStatus(_buyCycle, _buyObservation, SignalCycleKind.OpenBuy, "Open Buy"),
        CreateCycleStatus(_sellCycle, _sellObservation, SignalCycleKind.OpenSell, "Open Sell")
    ];

    private static SignalCycleStatus CreateCycleStatus(
        GapCycleState state,
        SignalCycleEventSnapshot observation,
        SignalCycleKind kind,
        string displayName)
    {
        var cycle = state.Current;
        return new SignalCycleStatus(
            kind,
            displayName,
            SlotId: null,
            cycle.CycleId,
            cycle.SampleCount,
            state.FixedRequiredSize,
            cycle.Status.ToString(),
            cycle.Gaps.Count > 0 ? cycle.Gaps[^1] : null,
            cycle.Reason,
            cycle.LastTickUtc,
            observation.DisplayEventName,
            observation.DisplayCycleId,
            observation.DisplayCount,
            observation.DisplayValue,
            observation.DisplayReason,
            observation.DisplayAtUtc);
    }

    public IReadOnlyList<GapSignalTriggerResult> ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config)
    {
        var normalizedConfirm = Math.Abs(config.ConfirmGapPts);
        var normalizedOpen = Math.Abs(config.OpenPts);

        if (config.OpenGapStability is not null)
        {
            return ProcessStableCycles(
                snapshot,
                config,
                config.OpenGapStability,
                normalizedConfirm,
                normalizedOpen,
                normalizedHoldMs: 0);
        }

        // Compatibility-only path for direct legacy callers. ConfigService rejects
        // missing stability policies, so valid Supabase runtime configs never enter it.
        var results = new List<GapSignalTriggerResult>(capacity: 2);
        var buyResult = ProcessSide(
            GapSignalTriggerType.OpenByGapBuy, GapSignalSide.Buy, GapSignalAction.Open,
            snapshot.ExchangeABid, snapshot.ExchangeAAsk, snapshot.ExchangeBBid, snapshot.ExchangeBAsk,
            snapshot.GapBuy, snapshot.GapSell, snapshot.PointMultiplier, snapshot.GapBuy,
            snapshot.TimestampUtc, _buyState, Math.Max(0, config.HoldConfirmMs), config.OpenMaxTimesTick,
            value => value >= normalizedConfirm, value => value >= normalizedOpen, config.LimitMaxGap);
        if (buyResult is not null) results.Add(buyResult);

        var sellResult = ProcessSide(
            GapSignalTriggerType.OpenByGapSell, GapSignalSide.Sell, GapSignalAction.Open,
            snapshot.ExchangeABid, snapshot.ExchangeAAsk, snapshot.ExchangeBBid, snapshot.ExchangeBAsk,
            snapshot.GapBuy, snapshot.GapSell, snapshot.PointMultiplier, snapshot.GapSell,
            snapshot.TimestampUtc, _sellState, Math.Max(0, config.HoldConfirmMs), config.OpenMaxTimesTick,
            value => value <= -normalizedConfirm, value => value <= -normalizedOpen, config.LimitMaxGap);
        if (sellResult is not null) results.Add(sellResult);
        return results;
    }

    public void Reset()
    {
        _buyState.Reset();
        _sellState.Reset();
        var minimumSamples = _lastDiagnosticPolicy?.Stability.MinStableSamples ?? 3;
        LogOpenTransition(
            "BUY",
            _buyCycle.Reset("Reset Open GapBuy Cycle."),
            null,
            minimumSamples,
            _lastDiagnosticPolicy);
        LogOpenTransition(
            "SELL",
            _sellCycle.Reset("Reset Open GapSell Cycle."),
            null,
            minimumSamples,
            _lastDiagnosticPolicy);
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
        var diagnosticPolicy = CreateDiagnosticPolicy(
            config,
            stabilityConfig,
            normalizedHoldMs);
        _lastDiagnosticPolicy = diagnosticPolicy;

        var buyUpdate = _buyCycle.ProcessFixedSize(
            snapshot.TimestampUtc,
            snapshot.GapBuy,
            hasRequiredData: snapshot.GapBuy.HasValue
                && snapshot.ExchangeAAsk.HasValue
                && snapshot.ExchangeBBid.HasValue,
            confirmSatisfied: snapshot.GapBuy is int buyGap && buyGap >= normalizedConfirm,
            stabilityConfig,
            config.SignalCycleSize,
            CreateOpenFingerprint("BUY", snapshot, snapshot.GapBuy),
            config.LimitMaxGap);
        _buyObservation = SignalCycleObservation.ObserveGap(
            _buyObservation, buyUpdate, snapshot.GapBuy, snapshot.TimestampUtc);
        LogOpenTransition(
            "BUY",
            buyUpdate,
            snapshot.GapBuy,
            config.SignalCycleSize,
            diagnosticPolicy);
        var buyResult = TryCreateStableOpenResult(
            snapshot,
            buyUpdate.CurrentCycle,
            GapSignalTriggerType.OpenByGapBuy,
            GapSignalSide.Buy,
            value => value >= normalizedOpen,
            _buyCycle,
            ref _buyObservation,
            _logger,
            diagnosticPolicy);
        if (buyResult is not null)
        {
            results.Add(buyResult);
        }

        var sellUpdate = _sellCycle.ProcessFixedSize(
            snapshot.TimestampUtc,
            snapshot.GapSell,
            hasRequiredData: snapshot.GapSell.HasValue
                && snapshot.ExchangeABid.HasValue
                && snapshot.ExchangeBAsk.HasValue,
            confirmSatisfied: snapshot.GapSell is int sellGap && sellGap <= -normalizedConfirm,
            stabilityConfig,
            config.SignalCycleSize,
            CreateOpenFingerprint("SELL", snapshot, snapshot.GapSell),
            config.LimitMaxGap);
        _sellObservation = SignalCycleObservation.ObserveGap(
            _sellObservation, sellUpdate, snapshot.GapSell, snapshot.TimestampUtc);
        LogOpenTransition(
            "SELL",
            sellUpdate,
            snapshot.GapSell,
            config.SignalCycleSize,
            diagnosticPolicy);
        var sellResult = TryCreateStableOpenResult(
            snapshot,
            sellUpdate.CurrentCycle,
            GapSignalTriggerType.OpenByGapSell,
            GapSignalSide.Sell,
            value => value <= -normalizedOpen,
            _sellCycle,
            ref _sellObservation,
            _logger,
            diagnosticPolicy);
        if (sellResult is not null)
        {
            results.Add(sellResult);
        }

        return results;
    }

    private static GapSignalTriggerResult? TryCreateStableOpenResult(
        GapSignalSnapshot snapshot,
        GapCycleSnapshot cycle,
        GapSignalTriggerType triggerType,
        GapSignalSide side,
        Func<int, bool> isOpenSatisfied,
        GapCycleState state,
        ref SignalCycleEventSnapshot observation,
        ISlotLogger? logger,
        GapCycleDiagnostics.PolicyContext diagnosticPolicy)
    {
        if (cycle.Status != GapCycleStatus.Stable || cycle.Gaps.Count == 0)
        {
            return null;
        }

        var lastGap = cycle.Gaps[^1];
        if (!isOpenSatisfied(lastGap))
        {
            state.Reset("Fixed-size Open Cycle completed but final Gap did not reach open_pts.");
            return null;
        }

        var isBuy = side == GapSignalSide.Buy;
        var diagnosticSignalId = Guid.NewGuid().ToString("N");
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
            PointMultiplier: snapshot.PointMultiplier,
            DiagnosticCycleId: cycle.CycleId,
            DiagnosticSignalId: diagnosticSignalId);

        var sideName = side == GapSignalSide.Buy ? "BUY" : "SELL";
        GapCycleDiagnostics.LogTrigger(
            logger,
            "OPEN",
            sideName,
            null,
            cycle,
            lastGap,
            "Open trigger đã phát.",
            diagnosticPolicy,
            diagnosticSignalId);
        observation = SignalCycleObservation.Record(
            observation,
            "Triggered",
            cycle.CycleId,
            cycle.SampleCount,
            lastGap,
            "Open trigger emitted.",
            snapshot.TimestampUtc);
        state.Reset("Open trigger đã phát; reset Cycle.");
        return result;
    }

    private static string CreateOpenFingerprint(
        string side,
        GapSignalSnapshot snapshot,
        int? primaryGap) =>
        string.Join(
            '|',
            "OPEN",
            side,
            snapshot.TimestampUtc.Ticks,
            primaryGap?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeABid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeAAsk?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeBBid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeBAsk?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");

    private void LogOpenTransition(
        string side,
        GapCycleUpdateResult update,
        int? newGap,
        int minimumSamplesToLog = 3,
        GapCycleDiagnostics.PolicyContext? policy = null) =>
        GapCycleDiagnostics.LogTransition(
            _logger,
            "OPEN",
            side,
            null,
            update,
            newGap,
            minimumSamplesToLog,
            policy);

    private static GapCycleDiagnostics.PolicyContext CreateDiagnosticPolicy(
        GapSignalConfirmationConfig config,
        GapStabilityConfig stability,
        int holdConfirmMs) =>
        new(
            stability,
            holdConfirmMs,
            config.LimitMaxGap,
            config.DiagnosticMaxGap,
            config.DiagnosticConfigId,
            config.DiagnosticSymbol,
            ConfirmationMode: "FIXED_SIZE",
            SignalCycleSize: config.SignalCycleSize,
            HoldConfirmIgnored: true);

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
