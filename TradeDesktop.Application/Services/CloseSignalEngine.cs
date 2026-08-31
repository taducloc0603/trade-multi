using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public sealed class CloseSignalEngine : ICloseSignalEngine
{
    // Trigger diagnostics remain bounded even when signal_cycle_size is configured high.
    private const int MaxTpDiagnosticSamples = 1_024;

    // Compatibility only for direct callers that bypass ConfigService validation.
    private readonly GapSignalConfirmationEngine.SideWindowState _buyState = new();
    private readonly GapSignalConfirmationEngine.SideWindowState _sellState = new();
    private readonly GapCycleState _closeByGapSellCycle = new();
    private readonly GapCycleState _closeByGapBuyCycle = new();
    private readonly GapCycleState _sosCloseByGapSellCycle = new();
    private readonly GapCycleState _sosCloseByGapBuyCycle = new();
    private FixedSizeSignalCycle<double>? _tpCycle;
    private SignalCycleEventSnapshot _closeBuyObservation = new();
    private SignalCycleEventSnapshot _closeSellObservation = new();
    private SignalCycleEventSnapshot _sosCloseBuyObservation = new();
    private SignalCycleEventSnapshot _sosCloseSellObservation = new();
    private SignalCycleEventSnapshot _tpObservation = new();
    private readonly ISlotLogger? _logger;
    private int? _slotId;
    private GapCycleDiagnostics.PolicyContext? _lastDiagnosticPolicy;

    public CloseSignalEngine(ISlotLogger? logger = null)
    {
        _logger = logger;
    }

    internal void SetSlotId(int slotId) => _slotId = slotId;

    public IReadOnlyList<SignalCycleStatus> GetCycleStatuses() =>
    [
        CreateGapCycleStatus(_closeByGapSellCycle, _closeBuyObservation, SignalCycleKind.NormalCloseBuy, "Normal Close Buy"),
        CreateGapCycleStatus(_closeByGapBuyCycle, _closeSellObservation, SignalCycleKind.NormalCloseSell, "Normal Close Sell"),
        CreateGapCycleStatus(_sosCloseByGapSellCycle, _sosCloseBuyObservation, SignalCycleKind.SosCloseBuy, "SOS Close Buy"),
        CreateGapCycleStatus(_sosCloseByGapBuyCycle, _sosCloseSellObservation, SignalCycleKind.SosCloseSell, "SOS Close Sell"),
        CreateTpCycleStatus()
    ];

    private SignalCycleStatus CreateGapCycleStatus(
        GapCycleState state,
        SignalCycleEventSnapshot observation,
        SignalCycleKind kind,
        string displayName)
    {
        var cycle = state.Current;
        return new SignalCycleStatus(
            kind,
            displayName,
            _slotId,
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

    private SignalCycleStatus CreateTpCycleStatus() => new(
        SignalCycleKind.Tp,
        "TP",
        _slotId,
        _tpCycle?.CycleId ?? string.Empty,
        _tpCycle?.Count ?? 0,
        _tpCycle?.RequiredSize ?? 0,
        (_tpCycle?.Status ?? FixedSizeCycleStatus.Empty).ToString(),
        _tpCycle is { Values.Count: > 0 } ? _tpCycle.Values[^1] : null,
        _tpCycle?.LastResetReason ?? string.Empty,
        _tpCycle?.LastUpdatedAtUtc,
        _tpObservation.DisplayEventName,
        _tpObservation.DisplayCycleId,
        _tpObservation.DisplayCount,
        _tpObservation.DisplayValue,
        _tpObservation.DisplayReason,
        _tpObservation.DisplayAtUtc);

    public GapSignalTriggerResult? ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        double? slotProfit = null)
    {
        var normalizedCloseConfirm = Math.Abs(config.CloseConfirmGapPts);
        var normalizedClose = Math.Abs(config.ClosePts);
        var usesSos = config.CloseGapMode == CloseGapMode.Sos;

        // Fixed-size is the only supported signal-confirmation mode. Hold/max-tick
        // columns remain mapped temporarily for DB compatibility but are ignored here.
        var gapResult = config.CloseGapStability is not null
            ? ProcessStableGap(
                snapshot,
                config,
                config.CloseGapStability,
                openMode,
                normalizedCloseConfirm,
                normalizedClose,
                usesSos)
            : ProcessLegacyGap(
                snapshot,
                config,
                openMode,
                normalizedCloseConfirm,
                normalizedClose,
                usesSos);

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
            EffectiveCloseHoldMs = 0
        };
    }

    public void Reset()
    {
        ResetGapState();
        ResetTpCycle("ENGINE_RESET");
    }

    public void ResetGapState()
    {
        _buyState.Reset();
        _sellState.Reset();
        var minimumSamples = _lastDiagnosticPolicy?.Stability.MinStableSamples ?? 3;
        LogCloseTransition(
            "BUY",
            _closeByGapSellCycle.Reset("Reset Normal Close GapSell Cycle."),
            null,
            minimumSamples,
            _lastDiagnosticPolicy);
        LogCloseTransition(
            "BUY",
            _sosCloseByGapSellCycle.Reset("Reset SOS Close GapSell Cycle."),
            null,
            minimumSamples,
            _lastDiagnosticPolicy,
            action: "SOS_CLOSE");
        LogCloseTransition(
            "SELL",
            _sosCloseByGapBuyCycle.Reset("Reset SOS Close GapBuy Cycle."),
            null,
            minimumSamples,
            _lastDiagnosticPolicy,
            action: "SOS_CLOSE");
        LogCloseTransition(
            "SELL",
            _closeByGapBuyCycle.Reset("Reset Normal Close GapBuy Cycle."),
            null,
            minimumSamples,
            _lastDiagnosticPolicy);
    }

    private GapSignalTriggerResult? ProcessStableGap(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        GapStabilityConfig stabilityConfig,
        TradingOpenMode openMode,
        int normalizedCloseConfirm,
        int normalizedClose,
        bool usesSos)
    {
        return openMode switch
        {
            TradingOpenMode.GapBuy => ProcessStableCloseSide(
                snapshot,
                config,
                stabilityConfig,
                usesSos ? _sosCloseByGapSellCycle : _closeByGapSellCycle,
                snapshot.GapSell,
                hasRequiredData: snapshot.GapSell.HasValue
                    && snapshot.ExchangeABid.HasValue
                    && snapshot.ExchangeBAsk.HasValue,
                confirmSatisfied: snapshot.GapSell is int sellGap
                    && (usesSos ? sellGap <= normalizedCloseConfirm : sellGap <= -normalizedCloseConfirm),
                closeSatisfied: value => usesSos ? value <= normalizedClose : value <= -normalizedClose,
                GapSignalTriggerType.CloseByGapSell,
                GapSignalSide.Buy,
                usesSos,
                effectiveConfirm: usesSos ? normalizedCloseConfirm : -normalizedCloseConfirm,
                effectiveClose: usesSos ? normalizedClose : -normalizedClose),

            TradingOpenMode.GapSell => ProcessStableCloseSide(
                snapshot,
                config,
                stabilityConfig,
                usesSos ? _sosCloseByGapBuyCycle : _closeByGapBuyCycle,
                snapshot.GapBuy,
                hasRequiredData: snapshot.GapBuy.HasValue
                    && snapshot.ExchangeAAsk.HasValue
                    && snapshot.ExchangeBBid.HasValue,
                confirmSatisfied: snapshot.GapBuy is int buyGap
                    && (usesSos ? buyGap >= -normalizedCloseConfirm : buyGap >= normalizedCloseConfirm),
                closeSatisfied: value => usesSos ? value >= -normalizedClose : value >= normalizedClose,
                GapSignalTriggerType.CloseByGapBuy,
                GapSignalSide.Sell,
                usesSos,
                effectiveConfirm: usesSos ? -normalizedCloseConfirm : normalizedCloseConfirm,
                effectiveClose: usesSos ? -normalizedClose : normalizedClose),

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
        bool usesSos,
        int effectiveConfirm,
        int effectiveClose)
    {
        var diagnosticPolicy = new GapCycleDiagnostics.PolicyContext(
            stabilityConfig,
            0,
            config.LimitMaxGap,
            config.DiagnosticMaxGap,
            config.DiagnosticConfigId,
            config.DiagnosticSymbol,
            ConfirmationMode: "FIXED_SIZE",
            SignalCycleSize: config.SignalCycleSize,
            HoldConfirmIgnored: true);
        _lastDiagnosticPolicy = diagnosticPolicy;
        var sideName = positionSide == GapSignalSide.Buy ? "BUY" : "SELL";
        var update = state.ProcessFixedSize(
            snapshot.TimestampUtc,
            primaryGap,
            hasRequiredData,
            confirmSatisfied,
            stabilityConfig,
            config.SignalCycleSize,
            CreateNormalCloseFingerprint(sideName, snapshot, primaryGap),
            config.LimitMaxGap);
        if (usesSos && positionSide == GapSignalSide.Buy)
        {
            _sosCloseBuyObservation = SignalCycleObservation.ObserveGap(
                _sosCloseBuyObservation, update, primaryGap, snapshot.TimestampUtc);
        }
        else if (usesSos)
        {
            _sosCloseSellObservation = SignalCycleObservation.ObserveGap(
                _sosCloseSellObservation, update, primaryGap, snapshot.TimestampUtc);
        }
        else if (positionSide == GapSignalSide.Buy)
        {
            _closeBuyObservation = SignalCycleObservation.ObserveGap(
                _closeBuyObservation, update, primaryGap, snapshot.TimestampUtc);
        }
        else
        {
            _closeSellObservation = SignalCycleObservation.ObserveGap(
                _closeSellObservation, update, primaryGap, snapshot.TimestampUtc);
        }
        LogCloseTransition(
            sideName,
            update,
            primaryGap,
            config.SignalCycleSize,
            diagnosticPolicy,
            action: usesSos ? "SOS_CLOSE" : "CLOSE");
        var cycle = update.CurrentCycle;
        if (cycle.Status != GapCycleStatus.Stable || cycle.Gaps.Count == 0)
        {
            return null;
        }

        var lastGap = cycle.Gaps[^1];
        if (!closeSatisfied(lastGap))
        {
            state.Reset(usesSos
                ? "Fixed-size SOS Close Cycle completed but final Gap did not reach sos_close_gap_pts."
                : "Fixed-size Normal Close Cycle completed but final Gap did not reach close_pts.");
            return null;
        }

        var closesByGapSell = triggerType == GapSignalTriggerType.CloseByGapSell;
        var diagnosticSignalId = Guid.NewGuid().ToString("N");
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
            CloseGapMode: usesSos ? CloseGapMode.Sos : CloseGapMode.Normal,
            EffectiveCloseConfirmGapPts: effectiveConfirm,
            EffectiveCloseGapPts: effectiveClose,
            EffectiveCloseHoldMs: 0,
            DiagnosticCycleId: cycle.CycleId,
            DiagnosticSignalId: diagnosticSignalId);

        GapCycleDiagnostics.LogTrigger(
            _logger,
            usesSos ? "SOS_CLOSE" : "CLOSE",
            sideName,
            _slotId,
            cycle,
            lastGap,
            usesSos ? "SOS Close trigger emitted." : "Normal Close trigger emitted.",
            diagnosticPolicy,
            diagnosticSignalId);
        var previousObservation = usesSos
            ? positionSide == GapSignalSide.Buy ? _sosCloseBuyObservation : _sosCloseSellObservation
            : positionSide == GapSignalSide.Buy ? _closeBuyObservation : _closeSellObservation;
        var triggeredObservation = SignalCycleObservation.Record(
            previousObservation,
            "Triggered",
            cycle.CycleId,
            cycle.SampleCount,
            lastGap,
            usesSos ? "SOS Close trigger emitted." : "Normal Close trigger emitted.",
            snapshot.TimestampUtc);
        if (usesSos && positionSide == GapSignalSide.Buy)
        {
            _sosCloseBuyObservation = triggeredObservation;
        }
        else if (usesSos)
        {
            _sosCloseSellObservation = triggeredObservation;
        }
        else if (positionSide == GapSignalSide.Buy)
        {
            _closeBuyObservation = triggeredObservation;
        }
        else
        {
            _closeSellObservation = triggeredObservation;
        }
        state.Reset(usesSos ? "SOS Close trigger emitted; reset Cycle." : "Normal Close trigger emitted; reset Cycle.");
        return result;
    }

    private GapSignalTriggerResult? ProcessLegacyGap(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        int normalizedCloseConfirm,
        int normalizedClose,
        bool usesSos) =>
        openMode switch
        {
            TradingOpenMode.GapBuy => GapSignalConfirmationEngine.ProcessSide(
                GapSignalTriggerType.CloseByGapSell, GapSignalSide.Buy, GapSignalAction.Close,
                snapshot.ExchangeABid, snapshot.ExchangeAAsk, snapshot.ExchangeBBid, snapshot.ExchangeBAsk,
                snapshot.GapBuy, snapshot.GapSell, snapshot.PointMultiplier, snapshot.GapSell,
                snapshot.TimestampUtc, _buyState, Math.Max(0, config.CloseHoldConfirmMs), config.CloseMaxTimesTick,
                value => usesSos ? value <= normalizedCloseConfirm : value <= -normalizedCloseConfirm,
                value => usesSos ? value <= normalizedClose : value <= -normalizedClose,
                config.LimitMaxGap),
            TradingOpenMode.GapSell => GapSignalConfirmationEngine.ProcessSide(
                GapSignalTriggerType.CloseByGapBuy, GapSignalSide.Sell, GapSignalAction.Close,
                snapshot.ExchangeABid, snapshot.ExchangeAAsk, snapshot.ExchangeBBid, snapshot.ExchangeBAsk,
                snapshot.GapBuy, snapshot.GapSell, snapshot.PointMultiplier, snapshot.GapBuy,
                snapshot.TimestampUtc, _sellState, Math.Max(0, config.CloseHoldConfirmMs), config.CloseMaxTimesTick,
                value => usesSos ? value >= -normalizedCloseConfirm : value >= normalizedCloseConfirm,
                value => usesSos ? value >= -normalizedClose : value >= normalizedClose,
                config.LimitMaxGap),
            _ => null
        };

    private string CreateNormalCloseFingerprint(
        string side,
        GapSignalSnapshot snapshot,
        int? primaryGap) =>
        string.Join(
            '|',
            "CLOSE",
            "FIXED",
            _slotId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            side,
            snapshot.TimestampUtc.Ticks,
            primaryGap?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeABid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeAAsk?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeBBid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeBAsk?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");

    private void LogCloseTransition(
        string side,
        GapCycleUpdateResult update,
        int? newGap,
        int minimumSamplesToLog = 3,
        GapCycleDiagnostics.PolicyContext? policy = null,
        string action = "CLOSE") =>
        GapCycleDiagnostics.LogTransition(
            _logger,
            action,
            side,
            _slotId,
            update,
            newGap,
            minimumSamplesToLog,
            policy);

    private GapSignalTriggerResult? ProcessTp(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        double? slotProfit)
    {
        EnsureTpCycleSize(config.SignalCycleSize, snapshot.TimestampUtc);

        if (openMode == TradingOpenMode.None || !slotProfit.HasValue)
        {
            ObserveTpReset("INCOMPLETE_PROFIT", slotProfit, snapshot.TimestampUtc);
            ResetTpCycle("INCOMPLETE_PROFIT");
            return null;
        }

        var confirmProfit = Math.Abs(config.CloseConfirmTpProfit);
        var targetProfit = Math.Abs(config.CloseTpProfit);
        if (targetProfit <= 0d)
        {
            ObserveTpReset("TP_DISABLED", currentValue: null, snapshot.TimestampUtc);
            ResetTpCycle("TP_DISABLED");
            return null;
        }

        var currentProfit = slotProfit.Value;
        if (currentProfit < confirmProfit)
        {
            ObserveTpReset("BELOW_CONFIRM", currentProfit, snapshot.TimestampUtc);
            ResetTpCycle("BELOW_CONFIRM");
            return null;
        }

        var limitMaxTp = Math.Abs(config.LimitMaxTp);
        if (limitMaxTp > 0d && currentProfit > limitMaxTp)
        {
            ObserveTpReset("LIMIT_MAX_TP_EXCEEDED", currentProfit, snapshot.TimestampUtc);
            ResetTpCycle("LIMIT_MAX_TP_EXCEEDED");
            return null;
        }

        var update = _tpCycle!.Add(
            currentProfit,
            snapshot.TimestampUtc,
            CreateTpFingerprint(snapshot, currentProfit));
        if (update.DuplicateIgnored)
        {
            return null;
        }

        if (update.Reason == "TIMESTAMP_REGRESSION")
        {
            _tpObservation = SignalCycleObservation.Record(
                _tpObservation,
                "Reset", update.CycleId, update.Count, currentProfit, update.Reason, snapshot.TimestampUtc);
            LogTpCycle("RESET", update, currentProfit, "TIMESTAMP_REGRESSION");
            return null;
        }

        if (update.Reason == "CYCLE_STARTED")
        {
            _tpObservation = SignalCycleObservation.Record(
                _tpObservation,
                "Started", update.CycleId, update.Count, currentProfit, update.Reason, snapshot.TimestampUtc);
            LogTpCycle("STARTED", update, currentProfit, "COLLECTING");
        }
        else if (!_tpCycle.IsComplete)
        {
            _tpObservation = SignalCycleObservation.Record(
                _tpObservation,
                "Progress", update.CycleId, update.Count, currentProfit, update.Reason, snapshot.TimestampUtc);
            LogTpCycle("PROGRESS", update, currentProfit, "COLLECTING");
        }

        if (!_tpCycle.IsComplete)
        {
            return null;
        }

        var lastProfit = currentProfit;
        var cycleId = _tpCycle.CycleId ?? string.Empty;
        var diagnosticProfits = _tpCycle.Values
            .Skip(Math.Max(0, _tpCycle.Values.Count - MaxTpDiagnosticSamples))
            .ToArray();
        _tpObservation = SignalCycleObservation.Record(
            _tpObservation,
            "Completed", cycleId, update.Count, lastProfit, update.Reason, snapshot.TimestampUtc);
        if (lastProfit < targetProfit)
        {
            LogTpCycle("COMPLETED", update, lastProfit, "TARGET_NOT_REACHED");
            ResetTpCycle("TARGET_NOT_REACHED", log: false);
            return null;
        }

        var maxTpProfit = Math.Abs(config.CloseMaxTpProfit);
        if (maxTpProfit > 0d && lastProfit > maxTpProfit)
        {
            LogTpCycle("COMPLETED", update, lastProfit, "CLOSE_MAX_TP_EXCEEDED");
            ResetTpCycle("CLOSE_MAX_TP_EXCEEDED", log: false);
            return null;
        }

        var isClosingBuyPosition = openMode == TradingOpenMode.GapBuy;
        var diagnosticSignalId = Guid.NewGuid().ToString("N");
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
            CloseTpProfits: diagnosticProfits,
            DiagnosticCycleId: cycleId,
            DiagnosticSignalId: diagnosticSignalId);

        LogTpCycle("COMPLETED", update, lastProfit, "TARGET_REACHED", diagnosticSignalId);
        LogTpCycle("TRIGGERED", update, lastProfit, "TRIGGERED", diagnosticSignalId);
        _tpObservation = SignalCycleObservation.Record(
            _tpObservation,
            "Triggered", cycleId, update.Count, lastProfit, "TP target reached.", snapshot.TimestampUtc);
        ResetTpCycle("TP_TRIGGERED", log: false);
        return result;
    }

    private void EnsureTpCycleSize(int requiredSize, DateTime timestampUtc)
    {
        if (_tpCycle is null)
        {
            _tpCycle = new FixedSizeSignalCycle<double>(requiredSize);
            return;
        }

        if (_tpCycle.RequiredSize != requiredSize)
        {
            var previousCount = _tpCycle.Count;
            var oldSize = _tpCycle.RequiredSize;
            var previousCycleId = _tpCycle.CycleId ?? string.Empty;
            _tpCycle.Resize(requiredSize);
            if (previousCount > 0)
            {
                _tpObservation = SignalCycleObservation.Record(
                    _tpObservation,
                    "Reset", previousCycleId, previousCount, null, "CYCLE_SIZE_CHANGED", timestampUtc);
                _logger?.Log(
                    $"[TP_CYCLE][RESET] slot_id={_slotId?.ToString() ?? "-"} " +
                    $"count={previousCount}/{oldSize} reason=CYCLE_SIZE_CHANGED new_size={requiredSize}");
            }
        }
    }

    private void ObserveTpReset(string reason, double? currentValue, DateTime timestampUtc)
    {
        if (_tpCycle is not { Count: > 0 })
        {
            return;
        }

        _tpObservation = SignalCycleObservation.Record(
            _tpObservation,
            "Reset",
            _tpCycle.CycleId ?? string.Empty,
            _tpCycle.Count,
            currentValue,
            reason,
            timestampUtc);
    }

    private void ResetTpCycle(string reason, bool log = true)
    {
        if (_tpCycle is null)
        {
            return;
        }

        var count = _tpCycle.Count;
        var required = _tpCycle.RequiredSize;
        if (log && count > 0)
        {
            _logger?.Log(
                $"[TP_CYCLE][RESET] slot_id={_slotId?.ToString() ?? "-"} " +
                $"count={count}/{required} reason={reason}");
        }

        _tpCycle.Reset(reason);
    }

    private string CreateTpFingerprint(GapSignalSnapshot snapshot, double profit) =>
        string.Join(
            '|',
            "CLOSE",
            "TP",
            _slotId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            snapshot.TimestampUtc.Ticks,
            profit.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            snapshot.ExchangeABid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeAAsk?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeBBid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            snapshot.ExchangeBAsk?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");

    private void LogTpCycle(
        string eventName,
        FixedSizeCycleUpdate update,
        double profit,
        string result,
        string signalId = "") =>
        _logger?.Log(
            $"[TP_CYCLE][{eventName}] cycle_id={update.CycleId} " +
            $"signal_id={(string.IsNullOrWhiteSpace(signalId) ? "-" : signalId)} " +
            $"slot_id={_slotId?.ToString() ?? "-"} count={update.Count}/{update.RequiredSize} " +
            $"profit={profit.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"confirmation_mode=FIXED_SIZE result={result}");
}
