using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.FastLeg;

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
    // Sàn C (Phase 4): trạng thái hoãn theo từng chiều. Chỉ hoạt động khi snapshot.FastLeg chặn Open.
    private readonly FastLegDeferState _buyFastDefer = new();
    private readonly FastLegDeferState _sellFastDefer = new();

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
        // Ngưỡng mang dấu. Nhánh GapBuy dùng "gap >= threshold", nhánh GapSell dùng
        // "gap <= -threshold"; ngưỡng dương giữ nguyên hành vi cũ, ngưỡng âm nới về phía trong
        // (cùng hướng với SOS Close). Đừng thêm lại Math.Abs ở đây.
        var signedConfirm = config.ConfirmGapPts;
        var signedOpen = config.OpenPts;

        if (config.OpenGapStability is not null)
        {
            return ProcessStableCycles(
                snapshot,
                config,
                config.OpenGapStability,
                signedConfirm,
                signedOpen,
                normalizedHoldMs: Math.Max(0, config.HoldConfirmMs));
        }

        // Compatibility-only path for direct legacy callers. ConfigService rejects
        // missing stability policies, so valid Supabase runtime configs never enter it.
        var results = new List<GapSignalTriggerResult>(capacity: 2);
        var buyResult = ProcessSide(
            GapSignalTriggerType.OpenByGapBuy, GapSignalSide.Buy, GapSignalAction.Open,
            snapshot.ExchangeABid, snapshot.ExchangeAAsk, snapshot.ExchangeBBid, snapshot.ExchangeBAsk,
            snapshot.GapBuy, snapshot.GapSell, snapshot.PointMultiplier, snapshot.GapBuy,
            snapshot.TimestampUtc, _buyState, Math.Max(0, config.HoldConfirmMs), config.OpenMaxTimesTick,
            value => value >= signedConfirm, value => value >= signedOpen, config.LimitMaxGap);
        if (buyResult is not null) results.Add(buyResult);

        var sellResult = ProcessSide(
            GapSignalTriggerType.OpenByGapSell, GapSignalSide.Sell, GapSignalAction.Open,
            snapshot.ExchangeABid, snapshot.ExchangeAAsk, snapshot.ExchangeBBid, snapshot.ExchangeBAsk,
            snapshot.GapBuy, snapshot.GapSell, snapshot.PointMultiplier, snapshot.GapSell,
            snapshot.TimestampUtc, _sellState, Math.Max(0, config.HoldConfirmMs), config.OpenMaxTimesTick,
            value => value <= -signedConfirm, value => value <= -signedOpen, config.LimitMaxGap);
        if (sellResult is not null) results.Add(sellResult);
        return results;
    }

    public void Reset()
    {
        _buyFastDefer.Abandon(_logger, "OPEN", "BUY", null, "engine_reset");
        _sellFastDefer.Abandon(_logger, "OPEN", "SELL", null, "engine_reset");
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
        int signedConfirm,
        int signedOpen,
        int normalizedHoldMs)
    {
        var results = new List<GapSignalTriggerResult>(capacity: 2);
        var diagnosticPolicy = CreateDiagnosticPolicy(
            config,
            stabilityConfig,
            normalizedHoldMs);
        _lastDiagnosticPolicy = diagnosticPolicy;

        var buyUpdate = _buyCycle.Process(
            snapshot.TimestampUtc,
            snapshot.GapBuy,
            hasRequiredData: snapshot.GapBuy.HasValue
                && snapshot.ExchangeAAsk.HasValue
                && snapshot.ExchangeBBid.HasValue,
            confirmSatisfied: snapshot.GapBuy is int buyGap && buyGap >= signedConfirm,
            stabilityConfig,
            normalizedHoldMs,
            config.LimitMaxGap);
        _buyObservation = SignalCycleObservation.ObserveGap(
            _buyObservation, buyUpdate, snapshot.GapBuy, snapshot.TimestampUtc);
        LogOpenTransition(
            "BUY",
            buyUpdate,
            snapshot.GapBuy,
            stabilityConfig.MinStableSamples,
            diagnosticPolicy);
        SyncFastDefer(_buyFastDefer, buyUpdate.CurrentCycle, "BUY", snapshot.TimestampUtc);
        var buyResult = TryCreateStableOpenResult(
            snapshot,
            config,
            buyUpdate.CurrentCycle,
            GapSignalTriggerType.OpenByGapBuy,
            GapSignalSide.Buy,
            value => value >= signedOpen,
            _buyCycle,
            ref _buyObservation,
            _logger,
            diagnosticPolicy,
            _buyFastDefer);
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
            confirmSatisfied: snapshot.GapSell is int sellGap && sellGap <= -signedConfirm,
            stabilityConfig,
            normalizedHoldMs,
            config.LimitMaxGap);
        _sellObservation = SignalCycleObservation.ObserveGap(
            _sellObservation, sellUpdate, snapshot.GapSell, snapshot.TimestampUtc);
        LogOpenTransition(
            "SELL",
            sellUpdate,
            snapshot.GapSell,
            stabilityConfig.MinStableSamples,
            diagnosticPolicy);
        SyncFastDefer(_sellFastDefer, sellUpdate.CurrentCycle, "SELL", snapshot.TimestampUtc);
        var sellResult = TryCreateStableOpenResult(
            snapshot,
            config,
            sellUpdate.CurrentCycle,
            GapSignalTriggerType.OpenByGapSell,
            GapSignalSide.Sell,
            value => value <= -signedOpen,
            _sellCycle,
            ref _sellObservation,
            _logger,
            diagnosticPolicy,
            _sellFastDefer);
        if (sellResult is not null)
        {
            results.Add(sellResult);
        }

        return results;
    }

    // Cycle đang hoãn mà bị vỡ / thay bằng cycle mới (Tolerance, Dispersion, Drift, confirm hỏng...) ⇒ bỏ hoãn.
    // Signal kế tiếp phải đi lại từ đầu như mọi cycle khác.
    private void SyncFastDefer(FastLegDeferState defer, GapCycleSnapshot cycle, string side, DateTime nowUtc)
    {
        if (defer.IsActive && (defer.CycleId != cycle.CycleId || cycle.Status != GapCycleStatus.Stable))
        {
            defer.Abandon(_logger, "OPEN", side, null, "cycle_reset", nowUtc);
        }
    }

    private static GapSignalTriggerResult? TryCreateStableOpenResult(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        GapCycleSnapshot cycle,
        GapSignalTriggerType triggerType,
        GapSignalSide side,
        Func<int, bool> isOpenSatisfied,
        GapCycleState state,
        ref SignalCycleEventSnapshot observation,
        ISlotLogger? logger,
        GapCycleDiagnostics.PolicyContext diagnosticPolicy,
        FastLegDeferState fastDefer)
    {
        if (cycle.Status != GapCycleStatus.Stable || cycle.Gaps.Count == 0)
        {
            return null;
        }

        var sideName = side == GapSignalSide.Buy ? "BUY" : "SELL";

        var lastGap = cycle.Gaps[^1];
        if (!isOpenSatisfied(lastGap))
        {
            // Chế độ TIME: Cycle vẫn ổn định, chỉ là mẫu cuối chưa đạt open_pts.
            // KHÔNG reset — Cycle tiếp tục thu mẫu cho tới khi Tolerance/Dispersion/Drift phá vỡ nó.
            return null;
        }

        // Trần cho GAP CUỐI của Cycle (signed, đối xứng). null = tắt gate; 0 và số âm vẫn hiệu lực.
        // Vi phạm => reset Cycle ngay để mở Cycle mới, KHÁC với gate open_pts ở trên (chỉ chờ tiếp).
        if (config.OpenMaxLastGapPts is { } maxLastGap)
        {
            var withinCeiling = side == GapSignalSide.Buy
                ? lastGap < maxLastGap
                : lastGap > -maxLastGap;
            if (!withinCeiling)
            {
                fastDefer.Abandon(logger, "OPEN", sideName, null, "max_last_gap", snapshot.TimestampUtc);
                state.Reset("Gap cuối vượt open_max_last_gap_pts; mở Cycle mới.");
                return null;
            }
        }

        var normalizedMaxTimesTick = Math.Max(0, config.OpenMaxTimesTick);
        if (normalizedMaxTimesTick > 0 && cycle.Gaps.Count > normalizedMaxTimesTick)
        {
            fastDefer.Abandon(logger, "OPEN", sideName, null, "max_times_tick", snapshot.TimestampUtc);
            state.Reset("Open Cycle vượt open_max_times_tick.");
            return null;
        }

        // Sàn C (Phase 4, D4): signal đã đủ điều kiện nhưng giá thật đang chạy (hoặc C không có ý kiến và fail_open tắt)
        // ⇒ HOÃN: giữ nguyên cycle, không tạo GUID / trigger, tick sau hỏi lại sàn C. Quá MaxDeferMs ⇒ bỏ cycle (D8),
        // chống giữ cycle quá lâu làm phình mẫu (CLAUDE.md §5, treo UI).
        if (snapshot.FastLeg is { BlocksOpen: true } verdict)
        {
            if (fastDefer.Defer(cycle.CycleId, snapshot.TimestampUtc))
            {
                FastLegDeferState.LogDefer(logger, "OPEN", sideName, null, cycle.CycleId, verdict);
            }

            if (fastDefer.IsExpired(verdict.MaxDeferMs, snapshot.TimestampUtc))
            {
                fastDefer.Abandon(logger, "OPEN", sideName, null, "defer_timeout", snapshot.TimestampUtc);
                state.Reset("Sàn C hoãn quá max_defer_ms; mở Cycle mới.");
            }

            return null;
        }

        var deferredTicks = fastDefer.Ticks;
        var deferredMs = fastDefer.IsActive ? fastDefer.ElapsedMs(snapshot.TimestampUtc) : 0L;
        fastDefer.LogRelease(logger, "OPEN", sideName, null, snapshot.FastLeg, snapshot.TimestampUtc);
        fastDefer.Clear();

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
            DiagnosticSignalId: diagnosticSignalId,
            FastLeg: snapshot.FastLeg,
            FastLegDeferredTicks: deferredTicks,
            FastLegDeferredMs: deferredMs);

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
            // Nhánh TIME: chu kỳ chốt theo hold-time + MinStableSamples.
            // SignalCycleSize chỉ đi kèm log để đối chiếu với nhánh TICK.
            ConfirmationMode: "TIME_AND_MIN_SAMPLES",
            SignalCycleSize: config.SignalCycleSize,
            HoldConfirmIgnored: false);

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
