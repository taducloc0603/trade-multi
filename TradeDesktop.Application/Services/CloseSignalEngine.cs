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
    // Nhánh TIME: TP chốt theo cửa sổ close_hold_confirm_ms, không theo số mẫu cố định.
    private readonly TpWindowState _tpState = new();
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

    // Nhánh TIME: TP không có số mẫu đích nên RequiredCount = 0; tiến độ nằm ở LastReason.
    private SignalCycleStatus CreateTpCycleStatus() => new(
        SignalCycleKind.Tp,
        "TP",
        _slotId,
        _tpState.CycleId,
        (int)Math.Min(int.MaxValue, _tpState.TickCount),
        0,
        _tpState.Status,
        _tpState.LastProfit,
        _tpState.LastReason,
        _tpState.LastTickUtc,
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
        var usesSos = config.CloseGapMode == CloseGapMode.Sos;

        // Ngưỡng mang dấu. Nhánh GapBuy dùng "gap >= threshold", nhánh GapSell dùng
        // "gap <= -threshold"; ngưỡng dương giữ nguyên hành vi cũ, ngưỡng âm nới về phía trong.
        // SOS luôn quy về hướng hồi vào trong, tức tương đương ngưỡng âm -> hành vi SOS không đổi.
        var signedCloseConfirm = usesSos
            ? -Math.Abs(config.CloseConfirmGapPts)
            : config.CloseConfirmGapPts;
        var signedClose = usesSos
            ? -Math.Abs(config.ClosePts)
            : config.ClosePts;

        // Nhánh TIME: Normal Close, SOS Close và TP đều chốt chu kỳ theo close_hold_confirm_ms
        // cộng MinStableSamples của Gap Stability.
        var gapResult = config.CloseGapStability is not null
            ? ProcessStableGap(
                snapshot,
                config,
                config.CloseGapStability,
                openMode,
                signedCloseConfirm,
                signedClose,
                usesSos)
            : ProcessLegacyGap(
                snapshot,
                config,
                openMode,
                signedCloseConfirm,
                signedClose);

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
        var effectiveConfirm = closesByGapSell ? -signedCloseConfirm : signedCloseConfirm;
        var effectiveClose = closesByGapSell ? -signedClose : signedClose;
        return gapResult with
        {
            CloseGapMode = config.CloseGapMode,
            EffectiveCloseConfirmGapPts = effectiveConfirm,
            EffectiveCloseGapPts = effectiveClose,
            EffectiveCloseHoldMs = Math.Max(0, config.CloseHoldConfirmMs)
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
        int signedCloseConfirm,
        int signedClose,
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
                    && sellGap <= -signedCloseConfirm,
                closeSatisfied: value => value <= -signedClose,
                GapSignalTriggerType.CloseByGapSell,
                GapSignalSide.Buy,
                usesSos,
                effectiveConfirm: -signedCloseConfirm,
                effectiveClose: -signedClose),

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
                    && buyGap >= signedCloseConfirm,
                closeSatisfied: value => value >= signedClose,
                GapSignalTriggerType.CloseByGapBuy,
                GapSignalSide.Sell,
                usesSos,
                effectiveConfirm: signedCloseConfirm,
                effectiveClose: signedClose),

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
        var normalizedHoldMs = Math.Max(0, config.CloseHoldConfirmMs);
        var diagnosticPolicy = new GapCycleDiagnostics.PolicyContext(
            stabilityConfig,
            normalizedHoldMs,
            config.LimitMaxGap,
            config.DiagnosticMaxGap,
            config.DiagnosticConfigId,
            config.DiagnosticSymbol,
            // Nhánh TIME: chu kỳ chốt theo hold-time + MinStableSamples.
            // SignalCycleSize chỉ đi kèm log để đối chiếu với nhánh TICK.
            ConfirmationMode: "TIME_AND_MIN_SAMPLES",
            SignalCycleSize: config.SignalCycleSize,
            HoldConfirmIgnored: false);
        _lastDiagnosticPolicy = diagnosticPolicy;
        var sideName = positionSide == GapSignalSide.Buy ? "BUY" : "SELL";
        var update = state.Process(
            snapshot.TimestampUtc,
            primaryGap,
            hasRequiredData,
            confirmSatisfied,
            stabilityConfig,
            normalizedHoldMs,
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
            stabilityConfig.MinStableSamples,
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
            // Chế độ TIME: Cycle vẫn ổn định, chỉ là mẫu cuối chưa đạt ngưỡng close.
            // KHÔNG reset — Cycle tiếp tục thu mẫu cho tới khi Tolerance/Dispersion/Drift phá vỡ nó.
            return null;
        }

        var normalizedMaxTimesTick = Math.Max(0, config.CloseMaxTimesTick);
        if (normalizedMaxTimesTick > 0 && cycle.Gaps.Count > normalizedMaxTimesTick)
        {
            state.Reset(usesSos
                ? "SOS Close Cycle vượt close_max_times_tick."
                : "Normal Close Cycle vượt close_max_times_tick.");
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
            EffectiveCloseHoldMs: normalizedHoldMs,
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
        int signedCloseConfirm,
        int signedClose) =>
        openMode switch
        {
            TradingOpenMode.GapBuy => GapSignalConfirmationEngine.ProcessSide(
                GapSignalTriggerType.CloseByGapSell, GapSignalSide.Buy, GapSignalAction.Close,
                snapshot.ExchangeABid, snapshot.ExchangeAAsk, snapshot.ExchangeBBid, snapshot.ExchangeBAsk,
                snapshot.GapBuy, snapshot.GapSell, snapshot.PointMultiplier, snapshot.GapSell,
                snapshot.TimestampUtc, _buyState, Math.Max(0, config.CloseHoldConfirmMs), config.CloseMaxTimesTick,
                value => value <= -signedCloseConfirm,
                value => value <= -signedClose,
                config.LimitMaxGap),
            TradingOpenMode.GapSell => GapSignalConfirmationEngine.ProcessSide(
                GapSignalTriggerType.CloseByGapBuy, GapSignalSide.Sell, GapSignalAction.Close,
                snapshot.ExchangeABid, snapshot.ExchangeAAsk, snapshot.ExchangeBBid, snapshot.ExchangeBAsk,
                snapshot.GapBuy, snapshot.GapSell, snapshot.PointMultiplier, snapshot.GapBuy,
                snapshot.TimestampUtc, _sellState, Math.Max(0, config.CloseHoldConfirmMs), config.CloseMaxTimesTick,
                value => value >= signedCloseConfirm,
                value => value >= signedClose,
                config.LimitMaxGap),
            _ => null
        };

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
        var normalizedHoldMs = Math.Max(0, config.CloseHoldConfirmMs);

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

        // Timestamp lùi -> coi như cửa sổ cũ không còn tin cậy, mở lại từ mẫu này.
        if (_tpState.LastTickUtc.HasValue && snapshot.TimestampUtc < _tpState.LastTickUtc.Value)
        {
            ObserveTpReset("TIMESTAMP_REGRESSION", currentProfit, snapshot.TimestampUtc);
            LogTpCycle("RESET", currentProfit, "TIMESTAMP_REGRESSION");
            ResetTpCycle("TIMESTAMP_REGRESSION", log: false);
            return null;
        }

        var isNewWindow = !_tpState.WindowStartUtc.HasValue;
        if (isNewWindow)
        {
            _tpState.StartWindow(snapshot.TimestampUtc);
        }

        _tpState.Add(currentProfit, snapshot.TimestampUtc, MaxTpDiagnosticSamples);

        var elapsedMs = (snapshot.TimestampUtc - _tpState.WindowStartUtc!.Value).TotalMilliseconds;
        _tpState.LastReason =
            $"Hold Confirm: {elapsedMs:0.####}/{normalizedHoldMs} ms.";

        if (isNewWindow)
        {
            _tpObservation = SignalCycleObservation.Record(
                _tpObservation,
                "Started", _tpState.CycleId, (int)_tpState.TickCount, currentProfit,
                "CYCLE_STARTED", snapshot.TimestampUtc);
            LogTpCycle("STARTED", currentProfit, "COLLECTING", holdProgressMs: elapsedMs, holdTargetMs: normalizedHoldMs);
        }
        else if (elapsedMs < normalizedHoldMs)
        {
            _tpObservation = SignalCycleObservation.Record(
                _tpObservation,
                "Progress", _tpState.CycleId, (int)_tpState.TickCount, currentProfit,
                _tpState.LastReason, snapshot.TimestampUtc);
            LogTpCycle("PROGRESS", currentProfit, "COLLECTING", holdProgressMs: elapsedMs, holdTargetMs: normalizedHoldMs);
        }

        if (elapsedMs < normalizedHoldMs)
        {
            return null;
        }

        if (_tpState.TickCount == 0)
        {
            ResetTpCycle("NO_SAMPLES");
            return null;
        }

        var lastProfit = currentProfit;
        var cycleId = _tpState.CycleId;
        var diagnosticProfits = _tpState.DiagnosticProfits.ToArray();
        _tpObservation = SignalCycleObservation.Record(
            _tpObservation,
            "Completed", cycleId, (int)_tpState.TickCount, lastProfit,
            _tpState.LastReason, snapshot.TimestampUtc);
        if (lastProfit < targetProfit)
        {
            LogTpCycle("COMPLETED", lastProfit, "TARGET_NOT_REACHED", holdProgressMs: elapsedMs, holdTargetMs: normalizedHoldMs);
            ResetTpCycle("TARGET_NOT_REACHED", log: false);
            return null;
        }

        var maxTpProfit = Math.Abs(config.CloseMaxTpProfit);
        if (maxTpProfit > 0d && lastProfit > maxTpProfit)
        {
            // Bản TIME cũ: chỉ bỏ qua tick này, KHÔNG reset cửa sổ.
            LogTpCycle("COMPLETED", lastProfit, "CLOSE_MAX_TP_EXCEEDED", holdProgressMs: elapsedMs, holdTargetMs: normalizedHoldMs);
            return null;
        }

        var normalizedMaxTimesTick = Math.Max(0, config.CloseMaxTimesTick);
        if (normalizedMaxTimesTick > 0 && _tpState.TickCount > normalizedMaxTimesTick)
        {
            LogTpCycle("COMPLETED", lastProfit, "CLOSE_MAX_TIMES_TICK_EXCEEDED", holdProgressMs: elapsedMs, holdTargetMs: normalizedHoldMs);
            ResetTpCycle("CLOSE_MAX_TIMES_TICK_EXCEEDED", log: false);
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

        LogTpCycle("COMPLETED", lastProfit, "TARGET_REACHED", diagnosticSignalId, elapsedMs, normalizedHoldMs);
        LogTpCycle("TRIGGERED", lastProfit, "TRIGGERED", diagnosticSignalId, elapsedMs, normalizedHoldMs);
        _tpObservation = SignalCycleObservation.Record(
            _tpObservation,
            "Triggered", cycleId, (int)_tpState.TickCount, lastProfit, "TP target reached.", snapshot.TimestampUtc);
        ResetTpCycle("TP_TRIGGERED", log: false);
        return result;
    }

    private void ObserveTpReset(string reason, double? currentValue, DateTime timestampUtc)
    {
        if (_tpState.TickCount == 0)
        {
            return;
        }

        _tpObservation = SignalCycleObservation.Record(
            _tpObservation,
            "Reset",
            _tpState.CycleId,
            (int)Math.Min(int.MaxValue, _tpState.TickCount),
            currentValue,
            reason,
            timestampUtc);
    }

    private void ResetTpCycle(string reason, bool log = true)
    {
        var count = _tpState.TickCount;
        if (log && count > 0)
        {
            _logger?.Log(
                $"[TP_CYCLE][RESET] slot_id={_slotId?.ToString() ?? "-"} " +
                $"count={count} reason={reason}");
        }

        _tpState.Reset(reason);
    }

    private void LogTpCycle(
        string eventName,
        double profit,
        string result,
        string signalId = "",
        double holdProgressMs = 0d,
        int holdTargetMs = 0)
    {
        if (_logger is null)
        {
            return;
        }

        var line =
            $"[TP_CYCLE][{eventName}] cycle_id={_tpState.CycleId} " +
            $"signal_id={(string.IsNullOrWhiteSpace(signalId) ? "-" : signalId)} " +
            $"slot_id={_slotId?.ToString() ?? "-"} count={_tpState.TickCount} " +
            $"hold={holdProgressMs:0.####}/{holdTargetMs}ms " +
            $"profit={profit.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"confirmation_mode=TIME_AND_MIN_SAMPLES result={result}";

        // PROGRESS, và COMPLETED không đạt đích, đều lặp lại MỖI TICK cho MỖI slot khi cửa sổ
        // đã đủ hold nhưng profit chưa tới ngưỡng -> chỉ ghi file, không đẩy lên realtime UI.
        // TARGET_REACHED / TRIGGERED tần suất thấp nên vẫn giữ realtime.
        if (eventName == "PROGRESS"
            || result is "TARGET_NOT_REACHED" or "CLOSE_MAX_TP_EXCEEDED" or "CLOSE_MAX_TIMES_TICK_EXCEEDED")
        {
            _logger.LogVerbose(line);
            return;
        }

        _logger.Log(line);
    }

    // Cửa sổ TP theo thời gian: mở tại mẫu profit hợp lệ đầu tiên, đóng khi đủ
    // close_hold_confirm_ms. Không có số mẫu đích.
    private sealed class TpWindowState
    {
        public DateTime? WindowStartUtc { get; private set; }
        public DateTime? LastTickUtc { get; private set; }
        public long TickCount { get; private set; }
        public string CycleId { get; private set; } = string.Empty;
        public string Status { get; private set; } = "Empty";
        public double? LastProfit { get; private set; }
        public string LastReason { get; set; } = string.Empty;
        public Queue<double> DiagnosticProfits { get; } = [];

        public void StartWindow(DateTime timestampUtc)
        {
            WindowStartUtc = timestampUtc;
            CycleId = Guid.NewGuid().ToString("N");
            TickCount = 0;
            DiagnosticProfits.Clear();
            Status = "Collecting";
        }

        public void Add(double profit, DateTime timestampUtc, int maxDiagnosticSamples)
        {
            TickCount++;
            LastTickUtc = timestampUtc;
            LastProfit = profit;
            Status = "Collecting";
            if (DiagnosticProfits.Count >= maxDiagnosticSamples)
            {
                DiagnosticProfits.Dequeue();
            }

            DiagnosticProfits.Enqueue(profit);
        }

        public void Reset(string reason)
        {
            WindowStartUtc = null;
            LastTickUtc = null;
            TickCount = 0;
            CycleId = string.Empty;
            LastProfit = null;
            LastReason = reason;
            Status = "Empty";
            DiagnosticProfits.Clear();
        }
    }
}
