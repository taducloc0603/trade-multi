using System.Globalization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Multi-slot portfolio orchestrator (Phase 0 §0.1).
///
/// Phase 0: cap default = 1, behavior identical to TradingFlowEngine khi chạy đơn slot.
/// Phase 2: cap raised to 7 via UpdateQuotaConfig, full Rules A/B/C/D enabled.
///
/// ProcessSnapshot loop:
///   1. Check startup cooldown + non-auto barrier.
///   2. CLOSE path: iterate Live slots, ask each slot's own CloseSignalEngine.
///      Pick winner by LastProfitSnapshot (Rule D framework, trivial at cap=1).
///   3. OPEN path: only when no close is eligible and quota allows.
/// </summary>
public sealed class PortfolioCoordinator : IPortfolioCoordinator
{
    // Rule C — default fallback khi DB thiếu cột; runtime override từ DB column.
    // opposite_side_lock_seconds → lock sau OPEN (chỉ chặn chiều ngược).
    public const int DefaultOppositeSideLockSeconds = 300;
    // Random post-close range → lock sau CLOSE (chặn cả 2 chiều).
    public const int DefaultPostCloseLockSeconds = 300;

    // Wall-clock fallback tolerance for stale snapshot timestamps (mirrors TradingFlowEngine).
    private static readonly TimeSpan SnapshotWallClockTolerance = TimeSpan.FromMinutes(5);

    private readonly PortfolioState _state = new();
    private readonly IOpenSignalEngine _openSignalEngine;
    private readonly ICloseSignalEngineFactory _closeSignalEngineFactory;
    private readonly ISlotLogger? _logger;
    private readonly Random _random;
    // Phase 3 §3.6 Risk 4: lock chống race khi 2 callers cùng pass CanOpenNewSlot rồi cùng allocate.
    private readonly object _allocateLock = new();
    private readonly object _tradeActionGateLock = new();
    private readonly HashSet<string> _nonAutoCloseOperations = new(StringComparer.Ordinal);
    private string _scheduleSleepingJson = string.Empty;

    // Phase 7 metrics — monotonic counters; reset on Coordinator.Reset.
    private long _totalOpensAllTime;
    private long _totalClosesAllTime;
    private long _quotaSkipCount;
    private long _oppositeLockSkipCount;
    private long _cooldownSkipCount;

    // Cached config holding range for close-gate fallback when slot.HoldingSeconds=0
    // (e.g., ForceWaitingClose path with no prior open trigger).
    private int _lastSeenStartTimeHold;
    private int _lastSeenEndTimeHold;
    // Phase 8: track cooldown block state changes để log entry/exit (tránh spam tick log).
    private bool _wasBlockedByCooldownLastTick;

    // Log throttle: TP_CHECK heartbeat — mỗi slot tối đa 1 dòng / TpCheckLogMinIntervalSeconds
    // (profit dao động quanh ngưỡng confirm/TP nên KHÔNG trigger theo band-change, chỉ theo interval).
    private readonly Dictionary<int, DateTime> _lastTpCheckLogAtUtc = new();
    private readonly Dictionary<int, string> _lastMinProfitStatusBySlot = [];
    private const int TpCheckLogMinIntervalSeconds = 60;

    // Log throttle: [SLOT][SKIP] Open blocked — log khi (side|reason) đổi HOẶC quá interval (tránh spam mỗi tick).
    private string _lastOpenSkipSignature = string.Empty;
    private DateTime _lastOpenSkipLogAtUtc;
    private const int OpenSkipLogMinIntervalSeconds = 30;

    public PortfolioCoordinator(
        IOpenSignalEngine openSignalEngine,
        ICloseSignalEngineFactory closeSignalEngineFactory,
        ISlotLogger? logger = null,
        Random? random = null)
    {
        _openSignalEngine = openSignalEngine;
        _closeSignalEngineFactory = closeSignalEngineFactory;
        _logger = logger;
        _random = random ?? new Random();
    }

    // ===== State queries =====
    public int LiveCount => _state.GetLiveSlots().Count();
    public int PendingCount => _state.Slots.Count(s =>
        s.Status == PositionSlotStatus.PendingOpen || s.Status == PositionSlotStatus.PendingClose);
    public int LiveBuyCount => _state.Slots.Count(s =>
        s.Status == PositionSlotStatus.Live && s.Side == TradingPositionSide.Buy);
    public int LiveSellCount => _state.Slots.Count(s =>
        s.Status == PositionSlotStatus.Live && s.Side == TradingPositionSide.Sell);
    public int LiveAndPendingTotalCount => _state.CountLiveAndPendingTotal();
    public IReadOnlyList<PositionSlot> LiveSlots => _state.GetLiveSlots().ToList();
    public IReadOnlyList<PositionSlot> PendingOpenSlots =>
        _state.Slots.Where(s => s.Status == PositionSlotStatus.PendingOpen).ToList();
    public IReadOnlyList<PositionSlot> PendingCloseSlots =>
        _state.Slots.Where(s => s.Status == PositionSlotStatus.PendingClose).ToList();

    public IReadOnlyList<SignalCycleStatus> GetSignalCycleStatuses()
    {
        var statuses = new List<SignalCycleStatus>(_openSignalEngine.GetCycleStatuses());
        foreach (var slot in _state.Slots.Where(slot =>
                     slot.Status is PositionSlotStatus.Live or PositionSlotStatus.PendingClose))
        {
            var closeKind = slot.IsSosActive
                ? slot.Side == TradingPositionSide.Buy
                    ? SignalCycleKind.SosCloseBuy
                    : SignalCycleKind.SosCloseSell
                : slot.Side == TradingPositionSide.Buy
                    ? SignalCycleKind.NormalCloseBuy
                    : SignalCycleKind.NormalCloseSell;
            foreach (var status in slot.CloseSignalEngine.GetCycleStatuses().Where(status =>
                         status.Kind == closeKind || status.Kind == SignalCycleKind.Tp))
            {
                statuses.Add(status with
                {
                    DisplayName = status.Kind == SignalCycleKind.Tp
                        ? $"Slot {slot.SlotId} TP"
                        : slot.IsSosActive
                            ? $"Slot {slot.SlotId} SOS Close"
                            : $"Slot {slot.SlotId} Normal Close"
                });
            }
        }

        return statuses;
    }

    public DateTime? GlobalActionLockUntilUtc => _state.GlobalActionLockUntilUtc;
    public bool HasNonAutoCloseInFlight
    {
        get
        {
            lock (_tradeActionGateLock)
            {
                return _nonAutoCloseOperations.Count > 0;
            }
        }
    }
    public DateTime? LastOpenConfirmedAtUtc => _state.LastOpenConfirmedAtUtc;
    public TradingPositionSide LastOpenConfirmedSide => _state.LastOpenConfirmedSide;
    public DateTime? LastCloseConfirmedAtUtc => _state.LastCloseConfirmedAtUtc;
    public int OppositeSideLockSeconds => _state.OppositeSideLockSeconds;
    public int RdStartPostCloseLockSeconds => _state.RdStartPostCloseLockSeconds;
    public int RdEndPostCloseLockSeconds => _state.RdEndPostCloseLockSeconds;
    public int LastSelectedPostCloseLockSeconds => _state.LastSelectedPostCloseLockSeconds;
    public int RdStartPostOpenLockSeconds => _state.RdStartPostOpenLockSeconds;
    public int RdEndPostOpenLockSeconds => _state.RdEndPostOpenLockSeconds;
    public int RdStartSameActionLockSeconds => _state.RdStartSameActionLockSeconds;
    public int RdEndSameActionLockSeconds => _state.RdEndSameActionLockSeconds;
    public int GlobalCooldownMinSec => _state.GlobalCooldownMinSec;
    public int GlobalCooldownMaxSec => _state.GlobalCooldownMaxSec;
    public RandomQuotaState RandomQuotaState => new(
        _state.IsRandomQuotaEnabled,
        _state.EffectiveMaxBuyOpens,
        _state.EffectiveMaxSellOpens,
        _state.QuotaOpenCountSinceRandom,
        _state.QuotaRandomAfterOpens,
        _state.QuotaCycleNumber,
        _state.PreviousMaxBuyOpens,
        _state.PreviousMaxSellOpens);

    internal PortfolioState State => _state;
    internal int LastSeenStartTimeHold => _lastSeenStartTimeHold;
    internal int LastSeenEndTimeHold => _lastSeenEndTimeHold;

    public PositionSlot? GetSlotByPairId(string pairId) => _state.GetSlotByPairId(pairId);
    public PositionSlot? GetSlotByTicket(ulong ticket) => _state.GetSlotByTicket(ticket);

    // ===== ProcessSnapshot (Phase 0 §0.1) =====
    public PortfolioSnapshotResult ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config)
    {

        // 1. Cache hold range for fallback (CanCheckClose floor protection).
        _lastSeenStartTimeHold = Math.Max(0, config.StartTimeHold);
        _lastSeenEndTimeHold = Math.Max(_lastSeenStartTimeHold, config.EndTimeHold);

        var effectiveNow = ResolveEffectiveNowUtc(snapshot.TimestampUtc);

        // 2. Startup/recovery cooldown is the only remaining portfolio-wide timer.
        if (HasNonAutoCloseInFlight)
        {
            return PortfolioSnapshotResult.Empty;
        }

        var globalCooldownUntilUtc = _state.GlobalActionLockUntilUtc;
        var globalCooldownActive = globalCooldownUntilUtc.HasValue
            && effectiveNow < globalCooldownUntilUtc.Value;
        if (globalCooldownActive)
        {
            // Phase 8: throttled log — chỉ log lần ĐẦU tiên bị block (tránh spam mỗi tick).
            // Log entry này giúp điều tra "tại sao không có trade nào dispatch" trong 1 window.
            if (!_wasBlockedByCooldownLastTick)
            {
                var remaining = (globalCooldownUntilUtc!.Value - effectiveNow).TotalSeconds;
                _logger?.Log(
                    $"[SLOT][COOLDOWN][BLOCK] ProcessSnapshot bị skip — cooldown active " +
                    $"(remaining {remaining:F1}s, until {_state.GlobalActionLockUntilUtc:HH:mm:ss} UTC). " +
                    $"Open/close thường bị chặn; slot SOS vẫn được xét close signal.");
                _wasBlockedByCooldownLastTick = true;
            }
        }

        // Phase 8: log khi cooldown vừa hết → giúp xác định thời điểm chính xác auto resumed.
        if (!globalCooldownActive && _wasBlockedByCooldownLastTick)
        {
            _logger?.Log(
                $"[SLOT][COOLDOWN][CLEAR] ProcessSnapshot resumed — cooldown đã hết " +
                $"(effectiveNow={effectiveNow:HH:mm:ss} UTC). Open/close signals sẽ được xử lý.");
            _wasBlockedByCooldownLastTick = false;
        }

        // 3. CLOSE path: iterate each Live slot's own CloseSignalEngine.
        var maxLifeTimeSec = _state.MaxLifeTimeBySecond;
        var eligibleCloses = new List<(PositionSlot slot, GapSignalTriggerResult trigger)>();
        var uiNotices = new List<PortfolioUiNotice>();
        // CHỈ LOGGING: gom các signal đã xác nhận nhưng bị vứt bỏ bên dưới, để caller ghi được
        // vào signal-outcome log. Lazy nên tick không có block nào thì không allocate.
        List<PortfolioBlockedSignal>? blockedSignals = null;
        foreach (var slot in _state.GetLiveSlots())
        {
            if (slot.IsCloseExecutionPending) continue;
            if (!IsHoldingElapsedOrFloorReached(slot, effectiveNow)) continue;
            if (!IsPostOpenCloseLockElapsed(slot, effectiveNow)) continue;

            var aOpenDistanceSos = config.SosTriggerAOpenDistancePts > 0d
                && slot.LastProfitA.HasValue
                && Math.Abs(slot.LastProfitA.Value) >= config.SosTriggerAOpenDistancePts;
            var timeSos = config.SosTriggerAfterSeconds > 0
                && slot.OpenConfirmedAtUtc.HasValue
                && (effectiveNow - slot.OpenConfirmedAtUtc.Value).TotalSeconds >= config.SosTriggerAfterSeconds;
            var sosActive = aOpenDistanceSos || timeSos;
            var sosSource = aOpenDistanceSos && timeSos
                ? "A_OPEN_DISTANCE_AND_TIME"
                : aOpenDistanceSos ? "A_OPEN_DISTANCE" : timeSos ? "TIME" : "NONE";
            if (slot.UpdateSosMode(sosActive, sosSource))
            {
                slot.CloseSignalEngine.ResetGapState();
                _logger?.Log(
                    $"[SOS][{(sosActive ? "ACTIVATED" : "DEACTIVATED")}] slot={slot.SlotId} pairId={slot.PairId} " +
                    $"reason={(sosActive ? SosReasonVietnamese(sosSource) : "Khoảng cách giá chân A đã xuống dưới ngưỡng và chưa quá thời gian kích hoạt")} " +
                    $"aMovePts={(slot.LastProfitA.HasValue ? slot.LastProfitA.Value.ToString("0.##") : "null")} " +
                    $"aDistancePts={(slot.LastProfitA.HasValue ? Math.Abs(slot.LastProfitA.Value).ToString("0.##") : "null")} " +
                    $"aDistanceThreshold={config.SosTriggerAOpenDistancePts:0.##} ageSeconds={GetSlotAgeSeconds(slot, effectiveNow):0.##} " +
                    $"timeThreshold={config.SosTriggerAfterSeconds} " +
                    $"gapMode={(sosActive
                        ? SosCloseConfigResolver.HasUsableSosGapThresholds(config.SosCloseConfirmGapPts, config.SosCloseGapPts) ? "SOS" : "NORMAL_FALLBACK"
                        : "NORMAL")}");
            }

            // Trong global startup/recovery cooldown chỉ slot đang thỏa SOS mới được tiếp tục
            // đánh giá close signal. Holding/post-open và toàn bộ close guard khác vẫn giữ nguyên.
            if (globalCooldownActive && !sosActive)
            {
                continue;
            }

            var resolvedGap = SosCloseConfigResolver.ResolveGapThresholds(
                sosActive,
                config.CloseConfirmGapPts,
                config.ClosePts,
                config.SosCloseConfirmGapPts,
                config.SosCloseGapPts);
            var effectiveCloseConfig = config with
            {
                CloseConfirmGapPts = resolvedGap.ConfirmGapPts,
                ClosePts = resolvedGap.CloseGapPts,
                CloseGapMode = resolvedGap.UsesSos ? CloseGapMode.Sos : CloseGapMode.Normal
            };

            LogTpCheck(slot, effectiveCloseConfig, effectiveNow);

            var closeTrigger = slot.CloseSignalEngine.ProcessSnapshot(
                snapshot,
                effectiveCloseConfig,
                slot.OpenMode,
                slot.HasCompleteProfitSnapshot ? slot.LastProfitSnapshot : null);
            if (closeTrigger is null || !closeTrigger.Triggered || closeTrigger.Action != GapSignalAction.Close)
            {
                continue;
            }

            var ageSeconds = GetSlotAgeSeconds(slot, effectiveNow);
            var minProfitGuardActive = _state.MinProfitToClose > 0d
                && (maxLifeTimeSec <= 0 || ageSeconds < maxLifeTimeSec);
            if (minProfitGuardActive &&
                (!slot.LastProfitA.HasValue || Math.Abs(slot.LastProfitA.Value) < _state.MinProfitToClose))
            {
                if (!_lastMinProfitStatusBySlot.TryGetValue(slot.SlotId, out var lastStatus) || lastStatus != "BLOCK")
                {
                    var closeMode = CloseModeLabel(closeTrigger);
                    var gapSummary = CloseGapSummary(closeTrigger);
                    var absoluteAMovePts = slot.LastProfitA.HasValue ? Math.Abs(slot.LastProfitA.Value) : (double?)null;
                    _logger?.Log(
                        $"[MIN_PROFIT][WAITING][{closeMode}] slot={slot.SlotId} pairId={slot.PairId} " +
                        $"description=\"Đã đạt điều kiện đóng {closeMode} nhưng chân A chưa dịch chuyển đủ từ Open Price\" " +
                        $"{gapSummary} " +
                        $"profitA={slot.LastProfitA?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable"} " +
                        $"absoluteAMovePts={absoluteAMovePts?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable"} " +
                        $"requiredAMovePts={_state.MinProfitToClose.ToString("0.##", CultureInfo.InvariantCulture)} " +
                        $"profitB={slot.LastProfitB?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable"} " +
                        $"combinedProfit={slot.LastProfitSnapshot?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable"} " +
                        $"ageSeconds={ageSeconds:0.##} maxLifeTimeSeconds={maxLifeTimeSec}");
                    uiNotices.Add(new PortfolioUiNotice(
                        "MIN_PROFIT_WAITING",
                        slot.SlotId,
                        BuildMinProfitUiNotice(
                            slot,
                            closeTrigger,
                            absoluteAMovePts,
                            _state.MinProfitToClose,
                            ageSeconds,
                            maxLifeTimeSec)));
                    _lastMinProfitStatusBySlot[slot.SlotId] = "BLOCK";
                }
                // Đặt NGOÀI khối throttle ở trên: tracker tự gộp theo lý do chặn, không mượn
                // throttle của log MIN_PROFIT.
                (blockedSignals ??= new(2)).Add(
                    new PortfolioBlockedSignal(closeTrigger, "MIN_PROFIT_NOT_REACHED", slot));
                continue;
            }

            if (_state.MinProfitToClose > 0d)
            {
                var status = maxLifeTimeSec > 0 && ageSeconds >= maxLifeTimeSec ? "EXPIRED" : "PASS";
                if (!_lastMinProfitStatusBySlot.TryGetValue(slot.SlotId, out var lastStatus) || lastStatus != status)
                {
                    if (status == "EXPIRED")
                    {
                        var closeMode = CloseModeLabel(closeTrigger);
                        _logger?.Log(
                            $"[MIN_PROFIT][EXPIRED][{closeMode}] slot={slot.SlotId} pairId={slot.PairId} " +
                            $"description=\"Slot đã đạt Max Lifetime; điều kiện dịch chuyển tuyệt đối chân A không còn chặn\" " +
                            $"{CloseGapSummary(closeTrigger)} " +
                            $"profitA={slot.LastProfitA?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable"} " +
                            $"absoluteAMovePts={(slot.LastProfitA.HasValue ? Math.Abs(slot.LastProfitA.Value).ToString("0.##", CultureInfo.InvariantCulture) : "unavailable")} " +
                            $"requiredAMovePts={_state.MinProfitToClose.ToString("0.##", CultureInfo.InvariantCulture)} " +
                            $"profitB={slot.LastProfitB?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable"} " +
                            $"combinedProfit={slot.LastProfitSnapshot?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable"} " +
                            $"ageSeconds={ageSeconds:0.##} maxLifeTimeSeconds={maxLifeTimeSec}");
                        uiNotices.Add(new PortfolioUiNotice(
                            "MIN_PROFIT_EXPIRED",
                            slot.SlotId,
                            $"[MAX LIFETIME][{closeMode}] Slot {slot.SlotId} đã đạt {maxLifeTimeSec} giây. Min Profit không còn chặn;"));
                    }
                    _lastMinProfitStatusBySlot[slot.SlotId] = status;
                }
            }

            eligibleCloses.Add((slot, closeTrigger));
        }

        if (eligibleCloses.Count > 0)
        {
            // Rule D (extended): if max_life_time_by_second > 0, prioritize slots whose age
            // (effectiveNow - OpenConfirmedAtUtc) exceeds the threshold. Among those overtime
            // slots, pick the OLDEST (largest age from OpenConfirmedAtUtc); if two are the same
            // age, tiebreak by highest profit. If none are overtime, fall back to plain Rule D
            // (highest profit across all eligible).
            var overtime = maxLifeTimeSec > 0
                ? eligibleCloses
                    .Where(x => x.slot.OpenConfirmedAtUtc.HasValue &&
                                (effectiveNow - x.slot.OpenConfirmedAtUtc.Value).TotalSeconds > maxLifeTimeSec)
                    .ToList()
                : new List<(PositionSlot slot, GapSignalTriggerResult trigger)>();

            (PositionSlot slot, GapSignalTriggerResult trigger) winner;
            if (overtime.Count > 0)
            {
                // Overtime tier: oldest first (smallest OpenConfirmedAtUtc), tiebreak highest profit.
                // OpenConfirmedAtUtc is guaranteed non-null by the overtime filter above.
                winner = overtime
                    .OrderBy(x => x.slot.OpenConfirmedAtUtc!.Value)
                    .ThenByDescending(x => x.slot.LastProfitSnapshot ?? double.MinValue)
                    .First();
            }
            else
            {
                winner = eligibleCloses
                    .OrderByDescending(x => x.slot.LastProfitSnapshot ?? double.MinValue)
                    .First();
            }

            // Claim auto ownership + mark PendingClose immediately so next tick/manual path
            // cannot dispatch the same pair concurrently.
            bool claimed;
            lock (_allocateLock)
            {
                claimed = winner.slot.Status == PositionSlotStatus.Live
                    && winner.slot.TryMarkCloseTriggered(
                        winner.trigger.TriggeredAtUtc,
                        CloseExecutionOwner.Auto,
                        winner.trigger.CloseReason,
                        winner.trigger.CloseGapMode,
                        winner.trigger.EffectiveCloseConfirmGapPts,
                        winner.trigger.EffectiveCloseGapPts,
                        winner.trigger.EffectiveCloseHoldMs);
            }
            if (!claimed)
            {
                (blockedSignals ??= new(1)).Add(
                    new PortfolioBlockedSignal(winner.trigger, "CLOSE_CLAIM_LOST", winner.slot));
                // GIỮ NGUYÊN hành vi cũ: đường này vốn trả Empty, tức KHÔNG mang uiNotices ra
                // ngoài. Truyền null cho UiNotices — không được "sửa cho đẹp" thành uiNotices.
                return new PortfolioSnapshotResult(null, null, null, null, blockedSignals);
            }

            if (globalCooldownActive)
            {
                var remainingSeconds = Math.Max(
                    0d,
                    (globalCooldownUntilUtc!.Value - effectiveNow).TotalSeconds);
                _logger?.Log(
                    $"[SLOT][COOLDOWN][SOS_BYPASS] slot={winner.slot.SlotId} pairId={winner.slot.PairId} " +
                    $"side={winner.slot.Side} source={winner.slot.SosActivationSource} " +
                    $"closeMode={CloseModeLabel(winner.trigger)} remainingSeconds={remainingSeconds:F1} " +
                    $"cooldownUntil={globalCooldownUntilUtc:O}");
                uiNotices.Add(new PortfolioUiNotice(
                    "SOS_COOLDOWN_BYPASS",
                    winner.slot.SlotId,
                    $"[SOS CLOSE][COOLDOWN BYPASS] Slot {winner.slot.SlotId} | " +
                    $"Nguồn={winner.slot.SosActivationSource} | Close={CloseModeLabel(winner.trigger)} | " +
                    $"Cooldown còn {remainingSeconds:F1}s"));
            }

            // Reset both engines after a close trigger (matches TradingFlowEngine behavior).
            _openSignalEngine.Reset();
            winner.slot.CloseSignalEngine.Reset();

            return new PortfolioSnapshotResult(
                OpenTrigger: null,
                CloseTargetSlot: winner.slot,
                CloseTrigger: winner.trigger,
                UiNotices: uiNotices,
                BlockedSignals: blockedSignals);
        }

        // 4. Global cooldown vẫn chặn toàn bộ Open và close thường.
        if (globalCooldownActive)
        {
            return uiNotices.Count == 0 && blockedSignals is null
                ? PortfolioSnapshotResult.Empty
                : new PortfolioSnapshotResult(null, null, null, uiNotices, blockedSignals);
        }

        // OPEN path: only when no close is eligible and quota allows.
        if (_state.CountLiveAndPendingTotal() < _state.MaxTotalOpens)
        {
            var triggers = _openSignalEngine.ProcessSnapshot(snapshot, config);
            for (var index = 0; index < triggers.Count; index++)
            {
                var trigger = triggers[index];
                if (!trigger.Triggered || trigger.Action != GapSignalAction.Open) continue;

                // Rule A + C check (Phase 2 enable; Phase 0 cap=1 makes this trivial).
                var side = trigger.PrimarySide == GapSignalSide.Buy
                    ? TradingPositionSide.Buy
                    : TradingPositionSide.Sell;
                if (!CanOpenNewSlot(side, out var blockReason))
                {
                    LogOpenSkipThrottled(side, blockReason, effectiveNow);
                    // blockReason là chuỗi tự do; caller chuẩn hoá thành token, không map ở đây
                    // để tránh nhân đôi logic với ResolveCoordinatorBlockReasonCode.
                    (blockedSignals ??= new(2)).Add(
                        new PortfolioBlockedSignal(trigger, blockReason, null));
                    continue;
                }

                // CHỈ LOGGING: chỉ đúng một Open được trả về mỗi snapshot. Với ngưỡng Open ÂM,
                // engine có thể phát cả OpenByGapBuy lẫn OpenByGapSell cùng tick (bất khả thi khi
                // ngưỡng dương vì GapSell >= GapBuy). Ghi nhận trigger bị bỏ để không mất dấu vết;
                // KHÔNG đổi hành vi chọn — trigger đầu tiên được phép vẫn thắng như trước.
                RecordDroppedOpenTriggers(triggers, index, trigger, ref blockedSignals);

                return new PortfolioSnapshotResult(
                    OpenTrigger: trigger,
                    CloseTargetSlot: null,
                    CloseTrigger: null,
                    UiNotices: uiNotices,
                    BlockedSignals: blockedSignals);
            }
        }

        return uiNotices.Count == 0 && blockedSignals is null
            ? PortfolioSnapshotResult.Empty
            : new PortfolioSnapshotResult(null, null, null, uiNotices, blockedSignals);
    }

    // ===== Slot lifecycle =====
    public PositionSlot? AllocatePendingOpenSlot(string pairId, GapSignalTriggerResult trigger)
        => AllocatePendingOpenSlotWithReason(pairId, trigger).Slot;

    public SlotAllocationResult AllocatePendingOpenSlotWithReason(string pairId, GapSignalTriggerResult trigger)
    {
        var side = trigger.PrimarySide == GapSignalSide.Buy
            ? TradingPositionSide.Buy
            : TradingPositionSide.Sell;
        var mode = trigger.TriggerType == GapSignalTriggerType.OpenByGapBuy
            ? TradingOpenMode.GapBuy
            : TradingOpenMode.GapSell;

        // Phase 3 §3.6 Risk 4: serialize quota check + allocate atomically to prevent
        // 2 callers cùng pass CanOpenNewSlot rồi cả 2 allocate vượt quota.
        lock (_allocateLock)
        {
            if (!CanOpenNewSlot(side, out var blockReason))
            {
                _logger?.Log($"[SLOT][SKIP] Allocate failed: {blockReason}");
                return new SlotAllocationResult(null, blockReason);
            }

            var slot = _state.AllocateNewSlot(pairId, _closeSignalEngineFactory);
            var holdingSeconds = NextSecondsInRange(_lastSeenStartTimeHold, _lastSeenEndTimeHold);
            slot.MarkOpenTriggered(side, mode, trigger.TriggeredAtUtc, holdingSeconds);

            // Reset shared open engine to prevent residual window state.
            _openSignalEngine.Reset();

            return new SlotAllocationResult(slot, string.Empty);
        }
    }

    public TradeActionGateResult TryAcquireTradeAction(
        DateTime requestedAtUtc,
        string action,
        string source,
        TradeActionOrigin origin = TradeActionOrigin.Auto,
        TradingPositionSide side = TradingPositionSide.None,
        string? pairId = null)
    {
        var requestedUtc = requestedAtUtc.Kind switch
        {
            DateTimeKind.Utc => requestedAtUtc,
            DateTimeKind.Local => requestedAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(requestedAtUtc, DateTimeKind.Utc)
        };

        lock (_tradeActionGateLock)
        {
            if (origin != TradeActionOrigin.Auto)
            {
                _logger?.Log(
                    $"[TRADE_GATE][ACQUIRED] action={action} source={source} origin={origin} " +
                    "autoCooldown=bypassed autoCooldownMutation=none");
                return new TradeActionGateResult(
                    Acquired: true,
                    LockUntilUtc: _state.GlobalActionLockUntilUtc,
                    Remaining: TimeSpan.Zero,
                    CooldownSeconds: 0,
                    Reason: "NON_AUTO_ACQUIRED");
            }

            if (_nonAutoCloseOperations.Count > 0)
            {
                _logger?.Log(
                    $"[TRADE_GATE][BLOCKED] action={action} source={source} origin={origin} " +
                    $"reason=NON_AUTO_CLOSE_IN_FLIGHT count={_nonAutoCloseOperations.Count}");
                return new TradeActionGateResult(
                    Acquired: false,
                    LockUntilUtc: _state.GlobalActionLockUntilUtc,
                    Remaining: TimeSpan.Zero,
                    CooldownSeconds: 0,
                    Reason: "NON_AUTO_CLOSE_IN_FLIGHT");
            }

            var normalizedAction = (action ?? string.Empty).Trim().ToUpperInvariant();
            var targetSlot = string.IsNullOrWhiteSpace(pairId)
                ? null
                : _state.GetSlotByPairId(pairId);
            var sosCloseBypassesGlobalCooldown = normalizedAction == "CLOSE"
                && targetSlot is { IsSosActive: true };
            var bypassedGlobalCooldownRemaining = TimeSpan.Zero;

            if (_state.GlobalActionLockUntilUtc is { } lockUntilUtc
                && requestedUtc < lockUntilUtc)
            {
                bypassedGlobalCooldownRemaining = lockUntilUtc - requestedUtc;
                if (!sosCloseBypassesGlobalCooldown)
                {
                    _logger?.Log(
                        $"[TRADE_GATE][BLOCKED] action={action} source={source} " +
                        $"remainingMs={bypassedGlobalCooldownRemaining.TotalMilliseconds:F0} lockUntil={lockUntilUtc:O}");
                    return new TradeActionGateResult(
                        Acquired: false,
                        LockUntilUtc: lockUntilUtc,
                        Remaining: bypassedGlobalCooldownRemaining,
                        CooldownSeconds: 0,
                        Reason: "GLOBAL_ACTION_COOLDOWN");
                }
            }

            var requestedType = normalizedAction switch
            {
                "OPEN" => AutoTradeActionType.Open,
                "CLOSE" => AutoTradeActionType.Close,
                _ => AutoTradeActionType.None
            };

            if (requestedType == AutoTradeActionType.None || side == TradingPositionSide.None)
            {
                return new TradeActionGateResult(false, null, TimeSpan.Zero, 0, "AUTO_ACTION_CONTEXT_INVALID");
            }

            var transition = EvaluateAutoTransition(requestedUtc, requestedType, side, pairId);
            if (!transition.Acquired)
            {
                Interlocked.Increment(ref _cooldownSkipCount);
                _logger?.Log(
                    $"[TRADE_GATE][BLOCKED] action={action} side={side} source={source} " +
                    $"reason={transition.Reason} remainingMs={transition.Remaining.TotalMilliseconds:F0}");
                return transition;
            }

            // Sinh đúng một lần tại mỗi Auto dispatch. Giá trị này chỉ được dùng nếu
            // transition kế tiếp là Open→Open cùng chiều hoặc Close→Close.
            var randomSec = NextSecondsInRange(
                _state.RdStartSameActionLockSeconds,
                _state.RdEndSameActionLockSeconds);
            _state.LastAutoDispatchType = requestedType;
            _state.LastAutoDispatchSide = side;
            _state.LastAutoDispatchAtUtc = requestedUtc;
            _state.LastAutoRandomIntervalSeconds = randomSec;
            if (requestedType == AutoTradeActionType.Close)
            {
                _state.LastSelectedPostCloseLockSeconds = NextSecondsInRange(
                    _state.RdStartPostCloseLockSeconds,
                    _state.RdEndPostCloseLockSeconds);
                if (!string.IsNullOrWhiteSpace(pairId))
                {
                    _state.GetSlotByPairId(pairId)?.SetSelectedPostCloseLockSeconds(
                        _state.LastSelectedPostCloseLockSeconds);
                }
            }

            if (sosCloseBypassesGlobalCooldown && bypassedGlobalCooldownRemaining > TimeSpan.Zero)
            {
                _logger?.Log(
                    $"[TRADE_GATE][SOS_RESET] pairId={pairId} slot={targetSlot!.SlotId} side={side} " +
                    $"source={targetSlot.SosActivationSource} " +
                    $"bypassedGlobalRemainingMs={bypassedGlobalCooldownRemaining.TotalMilliseconds:F0} " +
                    $"sameActionSeconds={randomSec} postCloseSeconds={_state.LastSelectedPostCloseLockSeconds} " +
                    $"dispatchAt={requestedUtc:O}");
            }

            _logger?.Log(
                $"[TRADE_GATE][ACQUIRED] action={action} side={side} source={source} " +
                $"transitionFrom={transition.Reason} nextSameTypeRandomSec={randomSec}");

            return new TradeActionGateResult(
                Acquired: true,
                LockUntilUtc: null,
                Remaining: TimeSpan.Zero,
                CooldownSeconds: randomSec,
                Reason: "AUTO_TRANSITION_ACQUIRED");
        }
    }

    private TradeActionGateResult EvaluateAutoTransition(
        DateTime requestedUtc,
        AutoTradeActionType requestedType,
        TradingPositionSide requestedSide,
        string? pairId)
    {
        if (requestedType == AutoTradeActionType.Close)
        {
            var slot = string.IsNullOrWhiteSpace(pairId) ? null : _state.GetSlotByPairId(pairId);
            if (slot is null || slot.Side != requestedSide || !slot.OpenConfirmedAtUtc.HasValue)
            {
                return new TradeActionGateResult(false, null, TimeSpan.Zero, 0, "AUTO_CLOSE_SLOT_CONTEXT_INVALID");
            }

            var perSlotLockUntil = slot.OpenConfirmedAtUtc.Value.AddSeconds(slot.SelectedPostOpenLockSeconds);
            if (_state.IsPostOpenLockConfigured && requestedUtc < perSlotLockUntil)
            {
                return BlockedTransition(perSlotLockUntil, requestedUtc, "PER_SLOT_POST_OPEN_LOCK");
            }
        }

        if (!_state.LastAutoDispatchAtUtc.HasValue || _state.LastAutoDispatchType == AutoTradeActionType.None)
        {
            return AcquiredTransition("NO_PREVIOUS_AUTO_ACTION");
        }

        DateTime? lockUntil = null;
        var reason = "TRANSITION_NO_DELAY";
        if (_state.LastAutoDispatchType == AutoTradeActionType.Open
            && requestedType == AutoTradeActionType.Open
            && _state.LastAutoDispatchSide == requestedSide)
        {
            lockUntil = _state.LastAutoDispatchAtUtc.Value.AddSeconds(_state.LastAutoRandomIntervalSeconds);
            reason = "SAME_SIDE_OPEN_RANDOM_LOCK";
        }
        else if (_state.LastAutoDispatchType == AutoTradeActionType.Open
                 && requestedType == AutoTradeActionType.Close)
        {
            lockUntil = _state.LastAutoDispatchAtUtc.Value.AddSeconds(_state.LastAutoRandomIntervalSeconds);
            reason = "OPEN_TO_CLOSE_RANDOM_LOCK";
        }
        else if (_state.LastAutoDispatchType == AutoTradeActionType.Close
                 && requestedType == AutoTradeActionType.Close)
        {
            lockUntil = _state.LastAutoDispatchAtUtc.Value.AddSeconds(_state.LastAutoRandomIntervalSeconds);
            reason = "CLOSE_TO_CLOSE_RANDOM_LOCK";
        }
        else if (_state.LastAutoDispatchType == AutoTradeActionType.Close
                 && requestedType == AutoTradeActionType.Open)
        {
            lockUntil = _state.LastAutoDispatchAtUtc.Value.AddSeconds(_state.LastSelectedPostCloseLockSeconds);
            reason = "POST_CLOSE_OPEN_LOCK";
        }

        return lockUntil.HasValue && requestedUtc < lockUntil.Value
            ? BlockedTransition(lockUntil.Value, requestedUtc, reason)
            : AcquiredTransition(reason);
    }

    private static TradeActionGateResult BlockedTransition(DateTime lockUntil, DateTime requestedUtc, string reason)
        => new(false, lockUntil, lockUntil - requestedUtc,
            Math.Max(0, (int)Math.Ceiling((lockUntil - requestedUtc).TotalSeconds)), reason);

    private static TradeActionGateResult AcquiredTransition(string reason)
        => new(true, null, TimeSpan.Zero, 0, reason);

    public void BeginNonAutoCloseOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return;
        lock (_tradeActionGateLock)
        {
            if (_nonAutoCloseOperations.Add(operationId))
            {
                _logger?.Log(
                    $"[TRADE_GATE][NON_AUTO_BARRIER][BEGIN] operationId={operationId} count={_nonAutoCloseOperations.Count}");
            }
        }
    }

    public void EndNonAutoCloseOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return;
        lock (_tradeActionGateLock)
        {
            if (_nonAutoCloseOperations.Remove(operationId))
            {
                _logger?.Log(
                    $"[TRADE_GATE][NON_AUTO_BARRIER][END] operationId={operationId} count={_nonAutoCloseOperations.Count}");
            }
        }
    }

    public void MarkSlotOpenConfirmed(string pairId, ulong ticketA, ulong ticketB, DateTime confirmedAtUtc)
    {
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null || slot.Status != PositionSlotStatus.PendingOpen) return;

        var selectedPostOpenLockSeconds = NextSecondsInRange(
            _state.RdStartPostOpenLockSeconds,
            _state.RdEndPostOpenLockSeconds);
        slot.MarkOpenConfirmed(ticketA, ticketB, confirmedAtUtc, selectedPostOpenLockSeconds);
        Interlocked.Increment(ref _totalOpensAllTime);
        AdvanceRandomQuotaAfterConfirmedOpen(pairId);

        // Phase 8: cooldown ĐÃ được set tại dispatch (AllocatePendingOpenSlot).
        // Tại confirm chỉ update LastOpenConfirmed* cho Rule C (opposite-side lock 300s
        // tính từ confirm time, theo CLAUDE.md §2 Rule C).
        _state.LastOpenConfirmedAtUtc = confirmedAtUtc;
        _state.LastOpenConfirmedSide = slot.Side;

        var slotCloseAllowedAt = confirmedAtUtc.AddSeconds(selectedPostOpenLockSeconds);
        _logger?.Log(
            $"[SLOT][OPEN_CONFIRMED] slot={slot.SlotId} side={slot.Side} " +
            $"ticketA={ticketA} ticketB={ticketB} " +
            $"autoCloseAllowedAt={slotCloseAllowedAt:O}");

        var oppositeLockUntilUtc = confirmedAtUtc.AddSeconds(_state.OppositeSideLockSeconds);
        var oppositeSide = slot.Side == TradingPositionSide.Buy ? TradingPositionSide.Sell : TradingPositionSide.Buy;
        _logger?.Log(
            $"[SLOT][WAITING][OPPOSITE_LOCK] Block OPEN {oppositeSide} trong {_state.OppositeSideLockSeconds}s " +
            $"(đến {oppositeLockUntilUtc:HH:mm:ss} UTC) — Rule C: sau OPEN {slot.Side}, CHỈ chặn chiều ngược");
    }

    public void MarkSlotCloseTriggered(string pairId, DateTime triggeredAtUtc)
    {
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null) return;

        // Slot có thể đã được ProcessSnapshot chuyển sang PendingClose.
        if (slot.Status != PositionSlotStatus.PendingClose)
        {
            slot.TryMarkCloseTriggered(triggeredAtUtc, CloseExecutionOwner.Auto);
        }
        EnsurePostCloseLockSelected(triggeredAtUtc);
        slot.SetSelectedPostCloseLockSeconds(_state.LastSelectedPostCloseLockSeconds);
    }

    public bool TryClaimSlotClose(string pairId, CloseExecutionOwner owner, DateTime triggeredAtUtc)
    {
        lock (_allocateLock)
        {
            var slot = _state.GetSlotByPairId(pairId);
            if (slot is null || slot.Status != PositionSlotStatus.Live)
            {
                return false;
            }

            var claimed = slot.TryMarkCloseTriggered(triggeredAtUtc, owner);
            if (claimed && owner == CloseExecutionOwner.Auto)
            {
                EnsurePostCloseLockSelected(triggeredAtUtc);
                slot.SetSelectedPostCloseLockSeconds(_state.LastSelectedPostCloseLockSeconds);
            }
            if (claimed && owner is CloseExecutionOwner.Manual or CloseExecutionOwner.Recovery)
            {
                BeginNonAutoCloseOperation(pairId);
            }
            return claimed;
        }
    }

    public void MarkSlotCloseConfirmed(string pairId, DateTime confirmedAtUtc)
    {
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null) return;

        var closeOwner = slot.CloseOwner;
        slot.MarkCloseConfirmed(confirmedAtUtc);
        Interlocked.Increment(ref _totalClosesAllTime);
        _lastTpCheckLogAtUtc.Remove(slot.SlotId);

        // Rule C: only strategic auto close anchors the auto re-entry/action lock.
        // Manual/recovery must not read or mutate the auto cooldown timer.
        if (closeOwner == CloseExecutionOwner.Auto)
        {
            if (slot.SelectedPostCloseLockSeconds > 0)
            {
                _state.LastSelectedPostCloseLockSeconds = slot.SelectedPostCloseLockSeconds;
            }
            _state.LastCloseConfirmedAtUtc = confirmedAtUtc;
        }

        _logger?.Log(
            $"[SLOT][CLOSE_CONFIRMED] slot={slot.SlotId} side={slot.Side} " +
            $"profit={slot.LastProfitSnapshot:F2} closeReason={slot.LastCloseReason ?? CloseSignalReason.Gap} " +
            $"closeMode={ConfirmedCloseModeLabel(slot, closeOwner)} " +
            $"confirmGapPts={slot.LastCloseConfirmGapPts?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} " +
            $"closeGapPts={slot.LastCloseGapPts?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} " +
            $"holdMs={slot.LastCloseHoldMs?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} " +
            $"owner={closeOwner}");
        if (closeOwner == CloseExecutionOwner.Auto)
        {
            var postCloseLockSeconds = _state.LastSelectedPostCloseLockSeconds;
            var postCloseLockUntilUtc = confirmedAtUtc.AddSeconds(postCloseLockSeconds);
            _logger?.Log(
                $"[SLOT][WAITING][POST_CLOSE_LOCK] Block AUTO action trong {postCloseLockSeconds}s " +
                $"(đến {postCloseLockUntilUtc:HH:mm:ss} UTC) — slot={slot.SlotId} side={slot.Side}");
        }
    }

    public void CloseSlotManually(string pairId, DateTime confirmedAtUtc)
    {
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null)
        {
            EndNonAutoCloseOperation(pairId);
            return;
        }

        if (slot.Status != PositionSlotStatus.Closed)
        {
            MarkSlotCloseConfirmed(pairId, confirmedAtUtc);
        }

        var closed = _state.GetSlotByPairId(pairId);
        if (closed is { Status: PositionSlotStatus.Closed })
        {
            _state.RemoveSlot(closed);
            _logger?.Log($"[SLOT][MANUAL_CLOSE] pairId={pairId} confirmed+removed");
        }
        EndNonAutoCloseOperation(pairId);
    }

    public PositionSlot RegisterSyncedSlot(
        string pairId,
        TradingPositionSide side,
        TradingOpenMode openMode,
        ulong? ticketA,
        ulong? ticketB,
        DateTime openConfirmedAtUtc,
        int holdingSeconds)
    {
        var existing = _state.GetSlotByPairId(pairId);
        if (existing is not null)
        {
            existing.MarkSynced(side, openMode, ticketA, ticketB, openConfirmedAtUtc, holdingSeconds);
            existing.SetSelectedPostOpenLockSeconds(NextSecondsInRange(
                _state.RdStartPostOpenLockSeconds,
                _state.RdEndPostOpenLockSeconds));
            return existing;
        }

        var slot = _state.AllocateNewSlot(pairId, _closeSignalEngineFactory);
        slot.MarkSynced(side, openMode, ticketA, ticketB, openConfirmedAtUtc, holdingSeconds);
        slot.SetSelectedPostOpenLockSeconds(NextSecondsInRange(
            _state.RdStartPostOpenLockSeconds,
            _state.RdEndPostOpenLockSeconds));
        return slot;
    }

    public void UpdateProfit(ulong ticket, double profit)
    {
        var slot = _state.GetSlotByTicket(ticket);
        if (slot is null) return;
        slot.UpdateProfit(ticket, profit);
    }

    private static string CloseModeLabel(GapSignalTriggerResult trigger)
        => trigger.CloseReason == CloseSignalReason.Tp
            ? "TP"
            : trigger.CloseGapMode == CloseGapMode.Sos ? "SOS" : "NORMAL";

    private static string ConfirmedCloseModeLabel(PositionSlot slot, CloseExecutionOwner owner)
    {
        if (owner != CloseExecutionOwner.Auto)
        {
            return owner.ToString().ToUpperInvariant();
        }

        return slot.LastCloseReason == CloseSignalReason.Tp
            ? "TP"
            : slot.LastCloseGapMode == CloseGapMode.Sos ? "SOS" : "NORMAL";
    }

    private static string GapDisplay(GapSignalTriggerResult trigger)
    {
        var closesByGapSell = trigger.TriggerType == GapSignalTriggerType.CloseByGapSell;
        var gapName = closesByGapSell ? "GapSell" : "GapBuy";
        var gapValue = closesByGapSell ? trigger.LastSellGap : trigger.LastBuyGap;
        return $"{gapName}={gapValue?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}";
    }

    private static string GapWindowDisplay(GapSignalTriggerResult trigger)
    {
        var closesByGapSell = trigger.TriggerType == GapSignalTriggerType.CloseByGapSell;
        var gapName = closesByGapSell ? "GapSell" : "GapBuy";
        var gaps = closesByGapSell ? trigger.SellGaps : trigger.BuyGaps;
        var values = gaps.Count > 0
            ? string.Join("|", gaps.Select(value => value.ToString(CultureInfo.InvariantCulture)))
            : "unavailable";
        return $"{gapName}=({values})";
    }

    private static string BuildMinProfitUiNotice(
        PositionSlot slot,
        GapSignalTriggerResult trigger,
        double? absoluteAMovePts,
        double minProfitToClose,
        double ageSeconds,
        int maxLifeTimeSeconds)
    {
        if (trigger.CloseReason != CloseSignalReason.Tp)
        {
            return $"[CHẶN CLOSE][{CloseModeLabel(trigger)}] Slot {slot.SlotId} | {GapWindowDisplay(trigger)} | " +
                   $"abs(A)={absoluteAMovePts?.ToString("0.##", CultureInfo.InvariantCulture) ?? "N/A"}pt < " +
                   $"min_profit_to_close={minProfitToClose.ToString("0.##", CultureInfo.InvariantCulture)}pt";
        }

        var tpCycle = trigger.CloseTpProfits is { Count: > 0 }
            ? string.Join(", ", trigger.CloseTpProfits.Select(
                value => value.ToString("0.00", CultureInfo.InvariantCulture)))
            : "N/A";
        return $"[CHẶN CLOSE][TP][MIN PROFIT] Slot {slot.SlotId} | " +
               $"abs(A)={absoluteAMovePts?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A"}pt < " +
               $"MinProfit={minProfitToClose.ToString("0.00", CultureInfo.InvariantCulture)}pt | " +
               $"Tuổi lệnh={ageSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s/{maxLifeTimeSeconds}s | " +
               $"Chu kỳ TP=[{tpCycle}]pt";
    }

    private static string CloseGapSummary(GapSignalTriggerResult trigger)
        => $"trigger={trigger.TriggerType} trackedGap={GapDisplay(trigger)} " +
           $"confirmGapPts={trigger.EffectiveCloseConfirmGapPts?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} " +
           $"closeGapPts={trigger.EffectiveCloseGapPts?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} " +
           $"holdMs={trigger.EffectiveCloseHoldMs?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}";

    private void LogTpCheck(PositionSlot slot, GapSignalConfirmationConfig config, DateTime effectiveNow)
    {
        if (config.CloseTpProfit <= 0d)
        {
            return;
        }

        // Throttle heartbeat: mỗi slot tối đa 1 dòng / TpCheckLogMinIntervalSeconds. KHÔNG trigger theo
        // band-change vì profit dao động quanh ngưỡng confirm/TP gây spam. Close thật có [SLOT][CLOSE_CONFIRMED].
        if (_lastTpCheckLogAtUtc.TryGetValue(slot.SlotId, out var lastLogAt)
            && (effectiveNow - lastLogAt) < TimeSpan.FromSeconds(TpCheckLogMinIntervalSeconds))
        {
            return;
        }
        _lastTpCheckLogAtUtc[slot.SlotId] = effectiveNow;

        var profitValue = slot.HasCompleteProfitSnapshot && slot.LastProfitSnapshot.HasValue
            ? slot.LastProfitSnapshot
            : (double?)null;
        var band = TpCheckLogBand.Resolve(profitValue, config.CloseConfirmTpProfit, config.CloseTpProfit);

        var profitText = profitValue.HasValue
            ? profitValue.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : "incomplete";

        _logger?.Log(
            $"[SLOT][TP_CHECK] slot={slot.SlotId} band={band} profit={profitText} " +
            $"confirm={Math.Abs(config.CloseConfirmTpProfit).ToString("0.00", CultureInfo.InvariantCulture)} " +
            $"tp={Math.Abs(config.CloseTpProfit).ToString("0.00", CultureInfo.InvariantCulture)} " +
            $"cycleSize={Math.Max(1, config.SignalCycleSize)} confirmationMode=FIXED_SIZE");
    }

    // Throttle [SLOT][SKIP] Open blocked: log khi (side|reason) đổi HOẶC quá interval — tránh spam mỗi tick khi quota full / opposite-lock.
    private void LogOpenSkipThrottled(TradingPositionSide side, string blockReason, DateTime effectiveNow)
    {
        var signature = $"{side}|{blockReason}";
        var changed = !string.Equals(_lastOpenSkipSignature, signature, StringComparison.Ordinal);
        var intervalElapsed = (effectiveNow - _lastOpenSkipLogAtUtc) >= TimeSpan.FromSeconds(OpenSkipLogMinIntervalSeconds);
        if (!changed && !intervalElapsed)
        {
            return;
        }

        _lastOpenSkipSignature = signature;
        _lastOpenSkipLogAtUtc = effectiveNow;
        _logger?.Log($"[SLOT][SKIP] Open {side} blocked: {blockReason}");
    }

    /// <summary>
    /// CHỈ LOGGING. Ghi nhận các Open trigger đứng sau <paramref name="selectedIndex"/> trong cùng
    /// một snapshot — chúng bị bỏ vì mỗi snapshot chỉ trả về đúng một Open. Chỉ xảy ra khi ngưỡng
    /// Open ÂM cho phép cả hai chiều cùng thoả (cần <c>2 * |confirm_gap_pts| >= spread</c>).
    /// KHÔNG tham gia bất kỳ quyết định giao dịch nào.
    /// </summary>
    private void RecordDroppedOpenTriggers(
        IReadOnlyList<GapSignalTriggerResult> triggers,
        int selectedIndex,
        GapSignalTriggerResult selected,
        ref List<PortfolioBlockedSignal>? blockedSignals)
    {
        for (var i = selectedIndex + 1; i < triggers.Count; i++)
        {
            var dropped = triggers[i];
            if (!dropped.Triggered || dropped.Action != GapSignalAction.Open) continue;

            var reason =
                $"DUAL_SIDE_TRIGGER_DROPPED (side={dropped.PrimarySide}, " +
                $"trigger {selected.PrimarySide} cùng tick đã được chọn)";
            (blockedSignals ??= new(1)).Add(new PortfolioBlockedSignal(dropped, reason, null));

            // Không dùng LogOpenSkipThrottled: sự kiện này hiếm (chỉ khi hai chiều cùng trigger),
            // throttle sẽ nuốt mất đúng thứ cần quan sát.
            _logger?.Log(
                $"[SLOT][SKIP] Open {dropped.PrimarySide} dropped: DUAL_SIDE_TRIGGER_DROPPED " +
                $"(trigger {selected.PrimarySide} cùng tick đã được chọn)");
        }
    }

    // ===== Rule checks (Phase 2) =====
    public bool CanOpenNewSlot(TradingPositionSide side, out string blockReason)
    {
        if (ScheduleSleepingEvaluator.IsOpenBlocked(_scheduleSleepingJson, DateTime.Now))
        {
            blockReason = "SCHEDULE_SLEEPING (OPEN blocked by device local time)";
            Interlocked.Increment(ref _cooldownSkipCount);
            return false;
        }

        var totalNow = _state.CountLiveAndPendingTotal();
        if (totalNow >= _state.MaxTotalOpens)
        {
            blockReason = $"QUOTA_TOTAL_FULL ({totalNow}/{_state.MaxTotalOpens}) " +
                $"description=\"Tổng số lệnh hiện tại đã đạt giới hạn cố định {_state.MaxTotalOpens}; không mở thêm lệnh mới\"";
            Interlocked.Increment(ref _quotaSkipCount);
            return false;
        }

        if (side == TradingPositionSide.Buy)
        {
            var buyNow = _state.CountLiveAndPendingBuy();
            var maxBuy = _state.IsRandomQuotaEnabled ? _state.EffectiveMaxBuyOpens : _state.MaxBuyOpens;
            if (buyNow >= maxBuy)
            {
                blockReason = $"QUOTA_BUY_FULL ({buyNow}/{maxBuy}) " +
                    $"description=\"Số lệnh Buy hiện tại đã đạt quota Buy ngẫu nhiên {maxBuy}; không mở thêm Buy\"";
                Interlocked.Increment(ref _quotaSkipCount);
                return false;
            }
        }
        else if (side == TradingPositionSide.Sell)
        {
            var sellNow = _state.CountLiveAndPendingSell();
            var maxSell = _state.IsRandomQuotaEnabled ? _state.EffectiveMaxSellOpens : _state.MaxSellOpens;
            if (sellNow >= maxSell)
            {
                blockReason = $"QUOTA_SELL_FULL ({sellNow}/{maxSell}) " +
                    $"description=\"Số lệnh Sell hiện tại đã đạt quota Sell ngẫu nhiên {maxSell}; không mở thêm Sell\"";
                Interlocked.Increment(ref _quotaSkipCount);
                return false;
            }
        }

        // Rule C — opposite-side lock 300s.
        if (_state.LastOpenConfirmedAtUtc.HasValue
            && _state.LastOpenConfirmedSide != TradingPositionSide.None
            && _state.LastOpenConfirmedSide != side)
        {
            var elapsedSec = (DateTime.UtcNow - _state.LastOpenConfirmedAtUtc.Value).TotalSeconds;
            if (elapsedSec < _state.OppositeSideLockSeconds)
            {
                var remaining = _state.OppositeSideLockSeconds - (int)elapsedSec;
                blockReason = $"OPPOSITE_SIDE_LOCK (remaining {remaining}s, CHỈ chặn opposite, last={_state.LastOpenConfirmedSide})";
                Interlocked.Increment(ref _oppositeLockSkipCount);
                return false;
            }
        }

        // Rule C (post-close) — sau CLOSE confirm, khoá MỌI open (cả 2 chiều) trong
        // Giá trị post-close đã random. Re-entry cooldown: vừa đóng thì phải chờ hết window
        // mới được vào lệnh mới. KHÔNG khoá CLOSE (CanCloseNow không check anchor này).
        if (_state.LastCloseConfirmedAtUtc.HasValue)
        {
            var elapsedSec = (DateTime.UtcNow - _state.LastCloseConfirmedAtUtc.Value).TotalSeconds;
            if (elapsedSec < _state.LastSelectedPostCloseLockSeconds)
            {
                var remaining = _state.LastSelectedPostCloseLockSeconds - (int)elapsedSec;
                blockReason = $"POST_CLOSE_LOCK (remaining {remaining}s, chặn CẢ 2 CHIỀU sau CLOSE lúc {_state.LastCloseConfirmedAtUtc.Value:HH:mm:ss}UTC)";
                Interlocked.Increment(ref _oppositeLockSkipCount);
                return false;
            }
        }

        blockReason = string.Empty;
        return true;
    }

    public bool CanCloseNow(out string blockReason)
    {
        if (_state.GlobalActionLockUntilUtc.HasValue)
        {
            var remaining = _state.GlobalActionLockUntilUtc.Value - DateTime.UtcNow;
            if (remaining.TotalSeconds > 0)
            {
                blockReason = $"GLOBAL_COOLDOWN (remaining {remaining.TotalSeconds:F1}s)";
                Interlocked.Increment(ref _cooldownSkipCount);
                return false;
            }
        }
        blockReason = string.Empty;
        return true;
    }

    public PortfolioMetrics GetMetrics()
    {
        return new PortfolioMetrics(
            CurrentLiveSlots: LiveCount,
            CurrentLiveBuy: LiveBuyCount,
            CurrentLiveSell: LiveSellCount,
            CurrentPendingOpen: PendingOpenSlots.Count,
            CurrentPendingClose: PendingCloseSlots.Count,
            TotalOpensAllTime: Interlocked.Read(ref _totalOpensAllTime),
            TotalClosesAllTime: Interlocked.Read(ref _totalClosesAllTime),
            QuotaSkipCount: Interlocked.Read(ref _quotaSkipCount),
            OppositeLockSkipCount: Interlocked.Read(ref _oppositeLockSkipCount),
            CooldownSkipCount: Interlocked.Read(ref _cooldownSkipCount));
    }

    // Compatibility overload for callers that rely on the historical min=1 behavior.
    public void UpdateQuotaConfig(int maxTotal, int maxBuy, int maxSell)
        => UpdateQuotaConfig(maxTotal, 1, maxBuy, 1, maxSell);

    public void UpdateQuotaConfig(int maxTotal, int minBuy, int maxBuy, int minSell, int maxSell)
    {
        _state.MaxTotalOpens = Math.Max(1, maxTotal);
        _state.MaxBuyOpens = Math.Max(1, maxBuy);
        _state.MaxSellOpens = Math.Max(1, maxSell);
        _state.MinBuyOpens = Math.Clamp(minBuy, 1, _state.MaxBuyOpens);
        _state.MinSellOpens = Math.Clamp(minSell, 1, _state.MaxSellOpens);
        if (_state.IsRandomQuotaEnabled)
        {
            _state.EffectiveMaxBuyOpens = Math.Clamp(
                _state.EffectiveMaxBuyOpens, _state.MinBuyOpens, _state.MaxBuyOpens);
            _state.EffectiveMaxSellOpens = Math.Clamp(
                _state.EffectiveMaxSellOpens, _state.MinSellOpens, _state.MaxSellOpens);
        }
    }

    public void EnableRandomQuota()
    {
        if (_state.IsRandomQuotaEnabled) return;
        _state.IsRandomQuotaEnabled = true;
        StartNewRandomQuotaCycle("INITIALIZED");
    }

    public void RerollRandomQuota()
    {
        if (!_state.IsRandomQuotaEnabled)
        {
            _state.IsRandomQuotaEnabled = true;
        }

        StartNewRandomQuotaCycle("MANUAL_REROLL");
    }

    public void RestoreRandomQuotaState(RandomQuotaState state)
    {
        if (!state.IsEnabled) return;

        _state.IsRandomQuotaEnabled = true;
        _state.EffectiveMaxBuyOpens = Math.Clamp(
            state.EffectiveMaxBuy, _state.MinBuyOpens, _state.MaxBuyOpens);
        _state.EffectiveMaxSellOpens = Math.Clamp(
            state.EffectiveMaxSell, _state.MinSellOpens, _state.MaxSellOpens);
        _state.QuotaRandomAfterOpens = Math.Clamp(state.RandomAfterOpens, 2, 5);
        _state.QuotaOpenCountSinceRandom = Math.Clamp(
            state.OpenCountSinceRandom, 0, _state.QuotaRandomAfterOpens - 1);
        _state.QuotaCycleNumber = Math.Max(1, state.CycleNumber);
        _state.PreviousMaxBuyOpens = NormalizePreviousQuota(
            state.PreviousMaxBuy, _state.MinBuyOpens, _state.MaxBuyOpens);
        _state.PreviousMaxSellOpens = NormalizePreviousQuota(
            state.PreviousMaxSell, _state.MinSellOpens, _state.MaxSellOpens);
        LogRandomQuota("RESTORED", "APP_RESTART");
    }

    public void UpdateCooldownConfig(int minSec, int maxSec)
    {
        var normalizedMin = Math.Max(0, Math.Min(minSec, maxSec));
        var normalizedMax = Math.Max(0, Math.Max(minSec, maxSec));
        _state.GlobalCooldownMinSec = normalizedMin;
        _state.GlobalCooldownMaxSec = normalizedMax;
    }

    public void UpdateMaxLifeTimeConfig(int maxLifeTimeSec)
    {
        _state.MaxLifeTimeBySecond = Math.Max(0, maxLifeTimeSec);
    }

    public void UpdateMinProfitToCloseConfig(double minProfitToClose)
    {
        _state.MinProfitToClose = Math.Max(0d, minProfitToClose);
    }

    public void UpdateOppositeSideLockConfig(int seconds)
    {
        // <= 0 = tắt lock nguy hiểm → giữ default thay vì disable.
        _state.OppositeSideLockSeconds = seconds > 0 ? seconds : DefaultOppositeSideLockSeconds;
    }

    public void UpdatePostCloseLockConfig(int startSeconds, int endSeconds)
    {
        var start = startSeconds > 0 ? startSeconds : DefaultPostCloseLockSeconds;
        var end = endSeconds > 0 ? endSeconds : DefaultPostCloseLockSeconds;
        _state.RdStartPostCloseLockSeconds = Math.Min(start, end);
        _state.RdEndPostCloseLockSeconds = Math.Max(start, end);
        _state.IsPostCloseLockConfigured = true;
    }

    public void UpdatePostOpenLockConfig(int startSeconds, int endSeconds)
    {
        var start = Math.Max(0, startSeconds);
        var end = Math.Max(0, endSeconds);
        _state.RdStartPostOpenLockSeconds = Math.Min(start, end);
        _state.RdEndPostOpenLockSeconds = Math.Max(start, end);
        _state.IsPostOpenLockConfigured = true;
    }

    public void UpdatePostCloseLockConfig(int seconds) => UpdatePostCloseLockConfig(seconds, seconds);

    public void UpdatePostOpenLockConfig(int seconds) => UpdatePostOpenLockConfig(seconds, seconds);

    public void UpdateSameActionLockConfig(int startSeconds, int endSeconds)
    {
        var start = startSeconds > 0 ? startSeconds : 3;
        var end = endSeconds > 0 ? endSeconds : 10;
        _state.RdStartSameActionLockSeconds = Math.Min(start, end);
        _state.RdEndSameActionLockSeconds = Math.Max(start, end);
    }

    private void EnsurePostCloseLockSelected(DateTime triggeredAtUtc)
    {
        if (_state.LastAutoDispatchType == AutoTradeActionType.Close
            && _state.LastAutoDispatchAtUtc == triggeredAtUtc)
        {
            return;
        }

        _state.LastSelectedPostCloseLockSeconds = NextSecondsInRange(
            _state.RdStartPostCloseLockSeconds,
            _state.RdEndPostCloseLockSeconds);
    }

    public void UpdateScheduleSleepingConfig(string? scheduleSleepingJson)
    {
        _scheduleSleepingJson = scheduleSleepingJson ?? string.Empty;
    }

    // ===== Rollback =====
    public void AbortPendingOpen(string pairId)
    {
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null) return;

        if (slot.Status == PositionSlotStatus.PendingOpen)
        {
            _state.RemoveSlot(slot);
            _logger?.Log($"[SLOT][ABORT] PendingOpen removed: pairId={pairId} slot={slot.SlotId}");
        }
    }

    public void AbortPendingClose(string pairId)
    {
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null)
        {
            EndNonAutoCloseOperation(pairId);
            return;
        }

        var closeOwner = slot.CloseOwner;
        slot.ClearCloseExecutionPending();
        if (closeOwner is CloseExecutionOwner.Manual or CloseExecutionOwner.Recovery)
        {
            EndNonAutoCloseOperation(pairId);
        }
        if (slot.Status == PositionSlotStatus.PendingClose)
        {
            slot.Status = PositionSlotStatus.Live;
            _logger?.Log($"[SLOT][ABORT] PendingClose reverted to Live: pairId={pairId} slot={slot.SlotId}");
        }
    }

    public void Reset()
    {
        _state.Clear();
        _openSignalEngine.Reset();
        _lastMinProfitStatusBySlot.Clear();
        _wasBlockedByCooldownLastTick = false;
        lock (_tradeActionGateLock)
        {
            _nonAutoCloseOperations.Clear();
        }
    }

    public void ClearAllSlots()
    {
        _state.Clear();
        _lastMinProfitStatusBySlot.Clear();
        lock (_tradeActionGateLock)
        {
            _nonAutoCloseOperations.Clear();
        }
    }

    /// <summary>
    /// Phase 5: rebuild Live slots từ persisted snapshot (DB JSON). Caller (ViewModel)
    /// đã verify tickets exist trong MMF trước khi pass vào — orphans handled separately.
    /// Sau recovery, kick global cooldown để tránh spam ngay sau restart.
    /// </summary>
    public void RecoverSlotsFromPersisted(IEnumerable<RecoveredSlotData> slots)
    {
        _state.Clear();

        var maxSlotId = 0;
        DateTime? latestOpenAt = null;
        var latestOpenSide = TradingPositionSide.None;

        foreach (var data in slots)
        {
            var slot = new PositionSlot(data.SlotId, data.PairId, _closeSignalEngineFactory.Create());
            slot.MarkSynced(
                side: data.Side,
                mode: data.OpenMode,
                ticketA: data.TicketA,
                ticketB: data.TicketB,
                openConfirmedAtUtc: data.OpenConfirmedAtUtc,
                holdingSeconds: data.HoldingSeconds);
            slot.SetHwndProfile(data.HwndProfileIndex, new ManualHwndColumnConfig(
                data.ChartHwndA, data.TradeHwndA, data.ChartHwndB, data.TradeHwndB));
            slot.SetSelectedPostOpenLockSeconds(NextSecondsInRange(
                _state.RdStartPostOpenLockSeconds,
                _state.RdEndPostOpenLockSeconds));
            _state.AddRecoveredSlot(slot);

            maxSlotId = Math.Max(maxSlotId, data.SlotId);

            if (!latestOpenAt.HasValue || data.OpenConfirmedAtUtc > latestOpenAt.Value)
            {
                latestOpenAt = data.OpenConfirmedAtUtc;
                latestOpenSide = data.Side;
            }

            _logger?.Log(
                $"[SLOT][RECOVERY] Restored slot {slot.SlotId}: side={slot.Side} mode={slot.OpenMode} " +
                $"ticketA={slot.TicketA} ticketB={slot.TicketB} openConfirmedAt={data.OpenConfirmedAtUtc:O} " +
                $"holding={data.HoldingSeconds}s");
        }

        _state.SetNextSlotId(maxSlotId + 1);

        // Restore Rule C state from latest open in persisted set.
        if (latestOpenAt.HasValue)
        {
            _state.LastOpenConfirmedAtUtc = latestOpenAt;
            _state.LastOpenConfirmedSide = latestOpenSide;
            _logger?.Log($"[SLOT][RECOVERY] Restored last-open: side={latestOpenSide} at {latestOpenAt:O}");
        }

        // Always kick a cooldown after restart to avoid trigger spam on first ticks.
        var startupCooldownSec = NextSecondsInRange(_state.GlobalCooldownMinSec, _state.GlobalCooldownMaxSec);
        if (startupCooldownSec > 0)
        {
            _state.GlobalActionLockUntilUtc = DateTime.UtcNow.AddSeconds(startupCooldownSec);
        }

        _logger?.Log(
            $"[SLOT][RECOVERY] Done: restored={_state.Slots.Count} slots, " +
            $"appStartCooldown={startupCooldownSec}s");

        if (startupCooldownSec > 0)
        {
            _logger?.Log(
                $"[SLOT][WAITING] Block AUTO open/close trong {startupCooldownSec}s " +
                $"(đến {_state.GlobalActionLockUntilUtc:HH:mm:ss} UTC) — reason=app restart cooldown");
        }
    }

    // ===== Helpers =====
    private void AdvanceRandomQuotaAfterConfirmedOpen(string pairId)
    {
        if (!_state.IsRandomQuotaEnabled) return;

        _state.QuotaOpenCountSinceRandom++;
        _logger?.Log(
            $"[QUOTA_RANDOM][PROGRESS] cycle={_state.QuotaCycleNumber} pairId={pairId} " +
            $"description=\"Pair đã mở thành công đủ hai chân A/B; tăng tiến độ chu kỳ quota\" " +
            $"count={_state.QuotaOpenCountSinceRandom}/{_state.QuotaRandomAfterOpens} " +
            $"effectiveBuy={_state.EffectiveMaxBuyOpens} effectiveSell={_state.EffectiveMaxSellOpens} " +
            $"maxTotal={_state.MaxTotalOpens}");

        if (_state.QuotaOpenCountSinceRandom >= _state.QuotaRandomAfterOpens)
        {
            StartNewRandomQuotaCycle("OPEN_THRESHOLD_REACHED");
        }
    }

    private void StartNewRandomQuotaCycle(string reason)
    {
        if (_state.QuotaCycleNumber > 0)
        {
            _state.PreviousMaxBuyOpens = _state.EffectiveMaxBuyOpens;
            _state.PreviousMaxSellOpens = _state.EffectiveMaxSellOpens;
        }
        _state.QuotaCycleNumber++;
        _state.EffectiveMaxBuyOpens = _random.Next(_state.MinBuyOpens, _state.MaxBuyOpens + 1);
        _state.EffectiveMaxSellOpens = _random.Next(_state.MinSellOpens, _state.MaxSellOpens + 1);
        _state.QuotaRandomAfterOpens = _random.Next(2, 6);
        _state.QuotaOpenCountSinceRandom = 0;
        LogRandomQuota("NEW_CYCLE", reason);
    }

    private void LogRandomQuota(string action, string reason)
    {
        var description = (action, reason) switch
        {
            ("NEW_CYCLE", "INITIALIZED") =>
                "Khởi tạo chu kỳ quota ngẫu nhiên đầu tiên",
            ("NEW_CYCLE", "OPEN_THRESHOLD_REACHED") =>
                "Đã đủ số pair Open thành công của chu kỳ trước; bắt đầu chu kỳ quota mới",
            ("NEW_CYCLE", "MANUAL_REROLL") =>
                "Người dùng chủ động random lại quota",
            ("RESTORED", "APP_RESTART") =>
                "Khôi phục quota và tiến độ chu kỳ cũ sau khi ứng dụng khởi động lại",
            _ => "Cập nhật trạng thái quota ngẫu nhiên"
        };
        _logger?.Log(
            $"[QUOTA_RANDOM][{action}] cycle={_state.QuotaCycleNumber} reason={reason} " +
            $"description=\"{description}\" " +
            $"previousBuy={_state.PreviousMaxBuyOpens?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} " +
            $"previousSell={_state.PreviousMaxSellOpens?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} " +
            $"effectiveBuy={_state.EffectiveMaxBuyOpens} effectiveSell={_state.EffectiveMaxSellOpens} " +
            $"rangeBuy={_state.MinBuyOpens}..{_state.MaxBuyOpens} " +
            $"rangeSell={_state.MinSellOpens}..{_state.MaxSellOpens} " +
            $"maxTotal={_state.MaxTotalOpens} count={_state.QuotaOpenCountSinceRandom}/" +
            $"{_state.QuotaRandomAfterOpens}");
    }

    private static int? NormalizePreviousQuota(int? value, int configuredMin, int configuredMax)
        => value.HasValue ? Math.Clamp(value.Value, configuredMin, configuredMax) : null;

    private static double GetSlotAgeSeconds(PositionSlot slot, DateTime nowUtc)
        => slot.OpenConfirmedAtUtc.HasValue
            ? Math.Max(0d, (nowUtc - slot.OpenConfirmedAtUtc.Value).TotalSeconds)
            : 0d;

    private static string SosReasonVietnamese(string source) => source switch
    {
        "A_OPEN_DISTANCE" => "Khoảng cách tuyệt đối giữa giá hiện tại và giá mở chân A đã đạt ngưỡng kích hoạt SOS",
        "TIME" => "Thời gian mở lệnh đã đạt ngưỡng kích hoạt SOS",
        "A_OPEN_DISTANCE_AND_TIME" => "Khoảng cách giá chân A và thời gian mở lệnh đều đã đạt ngưỡng kích hoạt SOS",
        _ => "Không có điều kiện SOS"
    };

    private bool IsHoldingElapsedOrFloorReached(PositionSlot slot, DateTime nowUtc)
    {
        // Slot must have an OpenedAtUtc baseline. If null (race), allow close.
        var baseline = slot.OpenConfirmedAtUtc ?? slot.OpenedAtUtc;
        if (baseline is null) return true;

        var configuredHolding = slot.HoldingSeconds > 0
            ? slot.HoldingSeconds
            : _lastSeenStartTimeHold;
        var effectiveHolding = Math.Max(configuredHolding, slot.SelectedPostOpenLockSeconds);

        if (effectiveHolding <= 0) return true;

        return (nowUtc - baseline.Value) >= TimeSpan.FromSeconds(effectiveHolding);
    }

    private bool IsPostOpenCloseLockElapsed(PositionSlot slot, DateTime nowUtc)
    {
        if (!_state.IsPostOpenLockConfigured || slot.SelectedPostOpenLockSeconds <= 0)
        {
            return true;
        }

        return slot.OpenConfirmedAtUtc.HasValue
            && nowUtc >= slot.OpenConfirmedAtUtc.Value.AddSeconds(slot.SelectedPostOpenLockSeconds);
    }

    private void ExtendGlobalActionLock(DateTime proposedUntilUtc, string reason)
    {
        lock (_tradeActionGateLock)
        {
            if (!_state.GlobalActionLockUntilUtc.HasValue
                || proposedUntilUtc > _state.GlobalActionLockUntilUtc.Value)
            {
                _state.GlobalActionLockUntilUtc = proposedUntilUtc;
                _logger?.Log(
                    $"[TRADE_GATE][EXTEND] reason={reason} lockUntil={proposedUntilUtc:O}");
            }
        }
    }

    private static DateTime ResolveEffectiveNowUtc(DateTime snapshotTimestampUtc)
    {
        // Mirrors TradingFlowEngine.ResolveEffectiveNowUtc to preserve test parity.
        var snapshotUtc = snapshotTimestampUtc.Kind switch
        {
            DateTimeKind.Utc => snapshotTimestampUtc,
            DateTimeKind.Local => snapshotTimestampUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(snapshotTimestampUtc, DateTimeKind.Utc)
        };

        var wallUtc = DateTime.UtcNow;
        var delta = snapshotUtc - wallUtc;

        if (Math.Abs(delta.TotalMinutes) <= SnapshotWallClockTolerance.TotalMinutes)
        {
            return snapshotUtc > wallUtc ? snapshotUtc : wallUtc;
        }

        return snapshotUtc;
    }

    private int NextSecondsInRange(int minSeconds, int maxSeconds)
    {
        var min = Math.Max(0, minSeconds);
        var max = Math.Max(0, maxSeconds);
        if (min > max) (min, max) = (max, min);
        if (min == max) return min;
        return _random.Next(min, max + 1);
    }
}
