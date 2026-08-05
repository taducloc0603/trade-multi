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
///   1. Check global cooldown (Rule B framework, default off Phase 0).
///   2. OPEN path: if quota allows, ask shared _openSignalEngine for trigger.
///   3. CLOSE path: iterate Live slots, ask each slot's own CloseSignalEngine.
///      Pick winner by LastProfitSnapshot (Rule D framework, trivial at cap=1).
/// </summary>
public sealed class PortfolioCoordinator : IPortfolioCoordinator
{
    // Rule C — default fallback khi DB thiếu cột; runtime override từ DB column.
    // opposite_side_lock_seconds → lock sau OPEN (chỉ chặn chiều ngược).
    public const int DefaultOppositeSideLockSeconds = 300;
    // post_close_lock_seconds → lock sau CLOSE (chặn cả 2 chiều).
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

    public DateTime? GlobalActionLockUntilUtc => _state.GlobalActionLockUntilUtc;
    public DateTime? LastOpenConfirmedAtUtc => _state.LastOpenConfirmedAtUtc;
    public TradingPositionSide LastOpenConfirmedSide => _state.LastOpenConfirmedSide;
    public DateTime? LastCloseConfirmedAtUtc => _state.LastCloseConfirmedAtUtc;
    public int OppositeSideLockSeconds => _state.OppositeSideLockSeconds;
    public int PostCloseLockSeconds => _state.PostCloseLockSeconds;
    public int PostOpenLockSeconds => _state.PostOpenLockSeconds;
    public int GlobalCooldownMinSec => _state.GlobalCooldownMinSec;
    public int GlobalCooldownMaxSec => _state.GlobalCooldownMaxSec;
    public TradingFlowSkipDiagnostic? LastSkipDiagnostic { get; private set; }

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
        LastSkipDiagnostic = null;

        // 1. Cache hold range for fallback (CanCheckClose floor protection).
        _lastSeenStartTimeHold = Math.Max(0, config.StartTimeHold);
        _lastSeenEndTimeHold = Math.Max(_lastSeenStartTimeHold, config.EndTimeHold);

        var effectiveNow = ResolveEffectiveNowUtc(snapshot.TimestampUtc);

        // 2. Check global cooldown (Rule B). Phase 0: cooldown 0s default → never blocks.
        if (_state.GlobalActionLockUntilUtc.HasValue && effectiveNow < _state.GlobalActionLockUntilUtc.Value)
        {
            // Phase 8: throttled log — chỉ log lần ĐẦU tiên bị block (tránh spam mỗi tick).
            // Log entry này giúp điều tra "tại sao không có trade nào dispatch" trong 1 window.
            if (!_wasBlockedByCooldownLastTick)
            {
                var remaining = (_state.GlobalActionLockUntilUtc.Value - effectiveNow).TotalSeconds;
                _logger?.Log(
                    $"[SLOT][COOLDOWN][BLOCK] ProcessSnapshot bị skip — cooldown active " +
                    $"(remaining {remaining:F1}s, until {_state.GlobalActionLockUntilUtc:HH:mm:ss} UTC). " +
                    $"Open/close signals trong window này sẽ bị bỏ qua.");
                _wasBlockedByCooldownLastTick = true;
            }
            return PortfolioSnapshotResult.Empty;
        }

        // Phase 8: log khi cooldown vừa hết → giúp xác định thời điểm chính xác auto resumed.
        if (_wasBlockedByCooldownLastTick)
        {
            _logger?.Log(
                $"[SLOT][COOLDOWN][CLEAR] ProcessSnapshot resumed — cooldown đã hết " +
                $"(effectiveNow={effectiveNow:HH:mm:ss} UTC). Open/close signals sẽ được xử lý.");
            _wasBlockedByCooldownLastTick = false;
        }

        // 3. OPEN path: only if quota allows.
        if (_state.CountLiveAndPendingTotal() < _state.MaxTotalOpens)
        {
            var triggers = _openSignalEngine.ProcessSnapshot(snapshot, config);
            foreach (var trigger in triggers)
            {
                if (!trigger.Triggered || trigger.Action != GapSignalAction.Open) continue;

                // Rule A + C check (Phase 2 enable; Phase 0 cap=1 makes this trivial).
                var side = trigger.PrimarySide == GapSignalSide.Buy
                    ? TradingPositionSide.Buy
                    : TradingPositionSide.Sell;
                if (!CanOpenNewSlot(side, out var blockReason))
                {
                    LogOpenSkipThrottled(side, blockReason, effectiveNow);
                    continue;
                }

                return new PortfolioSnapshotResult(
                    OpenTrigger: trigger,
                    CloseTargetSlot: null,
                    CloseTrigger: null);
            }
        }

        // 4. CLOSE path: iterate each Live slot's own CloseSignalEngine.
        var maxLifeTimeSec = _state.MaxLifeTimeBySecond;
        var minProfit = Math.Abs(config.CloseMinProfit);
        var eligibleCloses = new List<(PositionSlot slot, GapSignalTriggerResult trigger)>();
        foreach (var slot in _state.GetLiveSlots())
        {
            if (slot.IsCloseExecutionPending) continue;
            if (!IsHoldingElapsedOrFloorReached(slot, effectiveNow)) continue;

            LogTpCheck(slot, config, effectiveNow);

            var closeTrigger = slot.CloseSignalEngine.ProcessSnapshot(
                snapshot,
                config,
                slot.OpenMode,
                slot.HasCompleteProfitSnapshot ? slot.LastProfitSnapshot : null);
            if (closeTrigger is null || !closeTrigger.Triggered || closeTrigger.Action != GapSignalAction.Close)
            {
                continue;
            }

            // Guard min-profit (close_min_profit): SÀN lợi nhuận tối thiểu để ĐÓNG theo signal.
            // Áp cho MỌI CloseReason (cả Gap-reversal LẪN TP) — chặn đóng khi profit A+B của slot
            // chưa đạt ngưỡng. Lệnh QUÁ HẠN (age > max_life_time_by_second) được MIỄN guard → vẫn
            // đóng dù chưa đủ sàn. CloseMinProfit <= 0 = tắt guard. Không tạo close mới → không đụng Rule E.
            if (minProfit > 0d)
            {
                var isOvertime = maxLifeTimeSec > 0
                    && slot.OpenConfirmedAtUtc.HasValue
                    && (effectiveNow - slot.OpenConfirmedAtUtc.Value).TotalSeconds > maxLifeTimeSec;
                var slotProfit = slot.HasCompleteProfitSnapshot ? slot.LastProfitSnapshot : null;
                if (!isOvertime && (!slotProfit.HasValue || slotProfit.Value < minProfit))
                {
                    _logger?.Log(
                        $"[CLOSE_SELECT][MINPROFIT_SKIP] slot={slot.SlotId} pairId={slot.PairId} " +
                        $"reason={closeTrigger.CloseReason} " +
                        $"profit={(slotProfit.HasValue ? slotProfit.Value.ToString("0.##") : "null")} " +
                        $"minProfit={minProfit:0.##} overtime={isOvertime} — close suppressed");
                    continue;
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
                        winner.trigger.CloseReason);
            }
            if (!claimed)
            {
                return PortfolioSnapshotResult.Empty;
            }

            // Reset both engines after a close trigger (matches TradingFlowEngine behavior).
            _openSignalEngine.Reset();
            winner.slot.CloseSignalEngine.Reset();

            return new PortfolioSnapshotResult(
                OpenTrigger: null,
                CloseTargetSlot: winner.slot,
                CloseTrigger: winner.trigger);
        }

        return PortfolioSnapshotResult.Empty;
    }

    // ===== Slot lifecycle =====
    public PositionSlot? AllocatePendingOpenSlot(string pairId, GapSignalTriggerResult trigger)
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
                return null;
            }

            var slot = _state.AllocateNewSlot(pairId, _closeSignalEngineFactory);
            var holdingSeconds = NextSecondsInRange(_lastSeenStartTimeHold, _lastSeenEndTimeHold);
            slot.MarkOpenTriggered(side, mode, trigger.TriggeredAtUtc, holdingSeconds);

            // Reset shared open engine to prevent residual window state.
            _openSignalEngine.Reset();

            return slot;
        }
    }

    public TradeActionGateResult TryAcquireTradeAction(
        DateTime requestedAtUtc,
        string action,
        string source)
    {
        var requestedUtc = requestedAtUtc.Kind switch
        {
            DateTimeKind.Utc => requestedAtUtc,
            DateTimeKind.Local => requestedAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(requestedAtUtc, DateTimeKind.Utc)
        };

        lock (_tradeActionGateLock)
        {
            if (_state.GlobalActionLockUntilUtc is { } lockUntilUtc
                && requestedUtc < lockUntilUtc)
            {
                var remaining = lockUntilUtc - requestedUtc;
                _logger?.Log(
                    $"[TRADE_GATE][BLOCKED] action={action} source={source} " +
                    $"remainingMs={remaining.TotalMilliseconds:F0} lockUntil={lockUntilUtc:O}");
                return new TradeActionGateResult(
                    Acquired: false,
                    LockUntilUtc: lockUntilUtc,
                    Remaining: remaining,
                    CooldownSeconds: 0,
                    Reason: "GLOBAL_ACTION_COOLDOWN");
            }

            // Global portfolio lock by action type. Once configured, these fixed DB values
            // replace the legacy start_wait_time/end_wait_time random cooldown:
            // OPEN  -> blocks every following OPEN/CLOSE for post_open_lock_seconds.
            // CLOSE -> blocks every following OPEN/CLOSE for post_close_lock_seconds.
            var normalizedAction = (action ?? string.Empty).Trim().ToUpperInvariant();
            var cooldownSec = normalizedAction switch
            {
                "OPEN" when _state.IsPostOpenLockConfigured => _state.PostOpenLockSeconds,
                "CLOSE" when _state.IsPostCloseLockConfigured => _state.PostCloseLockSeconds,
                _ => NextSecondsInRange(_state.GlobalCooldownMinSec, _state.GlobalCooldownMaxSec)
            };
            var newLockUntilUtc = requestedUtc.AddSeconds(cooldownSec);
            _state.GlobalActionLockUntilUtc = cooldownSec > 0
                ? newLockUntilUtc
                : null;

            _logger?.Log(
                $"[TRADE_GATE][ACQUIRED] action={action} source={source} " +
                $"cooldownSec={cooldownSec} lockUntil={(cooldownSec > 0 ? newLockUntilUtc.ToString("O") : "none")}");

            return new TradeActionGateResult(
                Acquired: true,
                LockUntilUtc: _state.GlobalActionLockUntilUtc,
                Remaining: cooldownSec > 0 ? TimeSpan.FromSeconds(cooldownSec) : TimeSpan.Zero,
                CooldownSeconds: cooldownSec,
                Reason: cooldownSec > 0 ? "ACQUIRED" : "ACQUIRED_COOLDOWN_DISABLED");
        }
    }

    public void MarkSlotOpenConfirmed(string pairId, ulong ticketA, ulong ticketB, DateTime confirmedAtUtc)
    {
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null) return;

        slot.MarkOpenConfirmed(ticketA, ticketB, confirmedAtUtc);
        Interlocked.Increment(ref _totalOpensAllTime);

        // Phase 8: cooldown ĐÃ được set tại dispatch (AllocatePendingOpenSlot).
        // Tại confirm chỉ update LastOpenConfirmed* cho Rule C (opposite-side lock 300s
        // tính từ confirm time, theo CLAUDE.md §2 Rule C).
        _state.LastOpenConfirmedAtUtc = confirmedAtUtc;
        _state.LastOpenConfirmedSide = slot.Side;

        if (_state.IsPostOpenLockConfigured && _state.PostOpenLockSeconds > 0)
        {
            ExtendGlobalActionLock(
                confirmedAtUtc.AddSeconds(_state.PostOpenLockSeconds),
                $"open-confirm slot={slot.SlotId}");
        }

        // Log include lock state để dễ correlate confirm time với cooldown window.
        var lockSummary = _state.GlobalActionLockUntilUtc.HasValue
            ? $"lockUntil={_state.GlobalActionLockUntilUtc:HH:mm:ss}UTC"
            : "lockNone";
        _logger?.Log(
            $"[SLOT][OPEN_CONFIRMED] slot={slot.SlotId} side={slot.Side} " +
            $"ticketA={ticketA} ticketB={ticketB} {lockSummary}");

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

            return slot.TryMarkCloseTriggered(triggeredAtUtc, owner);
        }
    }

    public void MarkSlotCloseConfirmed(string pairId, DateTime confirmedAtUtc)
    {
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null) return;

        slot.MarkCloseConfirmed(confirmedAtUtc);
        Interlocked.Increment(ref _totalClosesAllTime);
        _lastTpCheckLogAtUtc.Remove(slot.SlotId);

        // Rule C (post-close) — anchor khoá MỌI open (cả 2 chiều) trong OppositeSideLockSeconds
        // giây tính từ close confirm. Phủ auto/manual/external (mọi path đều qua đây).
        _state.LastCloseConfirmedAtUtc = confirmedAtUtc;

        if (_state.IsPostCloseLockConfigured && _state.PostCloseLockSeconds > 0)
        {
            ExtendGlobalActionLock(
                confirmedAtUtc.AddSeconds(_state.PostCloseLockSeconds),
                $"close-confirm slot={slot.SlotId}");
        }

        // Phase 8: cooldown ĐÃ được set tại close dispatch (MarkSlotCloseTriggered).
        // Tại confirm chỉ log + update slot status, không reset cooldown.
        var lockSummary = _state.GlobalActionLockUntilUtc.HasValue
            ? $"lockUntil={_state.GlobalActionLockUntilUtc:HH:mm:ss}UTC"
            : "lockNone";
        var postCloseLockUntilUtc = confirmedAtUtc.AddSeconds(_state.PostCloseLockSeconds);
        _logger?.Log(
            $"[SLOT][CLOSE_CONFIRMED] slot={slot.SlotId} side={slot.Side} " +
            $"profit={slot.LastProfitSnapshot:F2} closeReason={slot.LastCloseReason ?? CloseSignalReason.Gap} {lockSummary}");
        _logger?.Log(
            $"[SLOT][WAITING][POST_CLOSE_LOCK] Block ALL OPEN trong {_state.PostCloseLockSeconds}s " +
            $"(đến {postCloseLockUntilUtc:HH:mm:ss} UTC) — Rule C: sau CLOSE slot={slot.SlotId} side={slot.Side}, chặn CẢ 2 CHIỀU (re-entry lock)");
    }

    public void CloseSlotManually(string pairId, DateTime confirmedAtUtc)
    {
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null) return;

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
            return existing;
        }

        var slot = _state.AllocateNewSlot(pairId, _closeSignalEngineFactory);
        slot.MarkSynced(side, openMode, ticketA, ticketB, openConfirmedAtUtc, holdingSeconds);
        return slot;
    }

    public void UpdateProfit(ulong ticket, double profit)
    {
        var slot = _state.GetSlotByTicket(ticket);
        if (slot is null) return;
        slot.UpdateProfit(ticket, profit);
    }

    public CloseDispatchGuardResult CheckCloseDispatch(
        string pairId,
        DateTime nowUtc,
        double closeMinProfit)
    {
        var minProfit = Math.Abs(closeMinProfit);
        var slot = _state.GetSlotByPairId(pairId);
        if (slot is null)
        {
            return new CloseDispatchGuardResult(false, null, minProfit, false, "SLOT_NOT_FOUND");
        }

        var effectiveNow = ResolveEffectiveNowUtc(nowUtc);
        var isOvertime = _state.MaxLifeTimeBySecond > 0
            && slot.OpenConfirmedAtUtc.HasValue
            && (effectiveNow - slot.OpenConfirmedAtUtc.Value).TotalSeconds > _state.MaxLifeTimeBySecond;
        var latestProfit = slot.HasCompleteProfitSnapshot ? slot.LastProfitSnapshot : null;

        if (minProfit <= 0d)
        {
            return new CloseDispatchGuardResult(true, latestProfit, minProfit, isOvertime, "MIN_PROFIT_DISABLED");
        }

        if (isOvertime)
        {
            return new CloseDispatchGuardResult(true, latestProfit, minProfit, true, "OVERTIME_BYPASS");
        }

        if (!latestProfit.HasValue)
        {
            return new CloseDispatchGuardResult(false, null, minProfit, false, "INCOMPLETE_PROFIT");
        }

        return latestProfit.Value >= minProfit
            ? new CloseDispatchGuardResult(true, latestProfit, minProfit, false, "MIN_PROFIT_MET")
            : new CloseDispatchGuardResult(false, latestProfit, minProfit, false, "BELOW_MIN_PROFIT");
    }

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
            $"holdMs={Math.Max(0, config.CloseHoldConfirmMs)} " +
            $"maxTick={Math.Max(0, config.CloseMaxTimesTick)}");
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
            blockReason = $"QUOTA_TOTAL_FULL ({totalNow}/{_state.MaxTotalOpens})";
            Interlocked.Increment(ref _quotaSkipCount);
            return false;
        }

        if (side == TradingPositionSide.Buy)
        {
            var buyNow = _state.CountLiveAndPendingBuy();
            if (buyNow >= _state.MaxBuyOpens)
            {
                blockReason = $"QUOTA_BUY_FULL ({buyNow}/{_state.MaxBuyOpens})";
                Interlocked.Increment(ref _quotaSkipCount);
                return false;
            }
        }
        else if (side == TradingPositionSide.Sell)
        {
            var sellNow = _state.CountLiveAndPendingSell();
            if (sellNow >= _state.MaxSellOpens)
            {
                blockReason = $"QUOTA_SELL_FULL ({sellNow}/{_state.MaxSellOpens})";
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
        // PostCloseLockSeconds giây. Re-entry cooldown: vừa đóng thì phải chờ hết window
        // mới được vào lệnh mới. KHÔNG khoá CLOSE (CanCloseNow không check anchor này).
        if (_state.LastCloseConfirmedAtUtc.HasValue)
        {
            var elapsedSec = (DateTime.UtcNow - _state.LastCloseConfirmedAtUtc.Value).TotalSeconds;
            if (elapsedSec < _state.PostCloseLockSeconds)
            {
                var remaining = _state.PostCloseLockSeconds - (int)elapsedSec;
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

    public void UpdateQuotaConfig(int maxTotal, int maxBuy, int maxSell)
    {
        _state.MaxTotalOpens = Math.Max(1, maxTotal);
        _state.MaxBuyOpens = Math.Max(1, maxBuy);
        _state.MaxSellOpens = Math.Max(1, maxSell);
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

    public void UpdateOppositeSideLockConfig(int seconds)
    {
        // <= 0 = tắt lock nguy hiểm → giữ default thay vì disable.
        _state.OppositeSideLockSeconds = seconds > 0 ? seconds : DefaultOppositeSideLockSeconds;
    }

    public void UpdatePostCloseLockConfig(int seconds)
    {
        // <= 0 = tắt lock nguy hiểm → giữ default thay vì disable.
        _state.PostCloseLockSeconds = seconds > 0 ? seconds : DefaultPostCloseLockSeconds;
        _state.IsPostCloseLockConfigured = true;
    }

    public void UpdatePostOpenLockConfig(int seconds)
    {
        _state.PostOpenLockSeconds = Math.Max(0, seconds);
        _state.IsPostOpenLockConfigured = true;
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
        if (slot is null) return;

        slot.ClearCloseExecutionPending();
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
        _wasBlockedByCooldownLastTick = false;
    }

    public void ClearAllSlots()
    {
        _state.Clear();
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
                $"[SLOT][WAITING] Block ALL open/close trong {startupCooldownSec}s " +
                $"(đến {_state.GlobalActionLockUntilUtc:HH:mm:ss} UTC) — reason=app restart cooldown");
        }
    }

    // ===== Helpers =====
    private bool IsHoldingElapsedOrFloorReached(PositionSlot slot, DateTime nowUtc)
    {
        // Slot must have an OpenedAtUtc baseline. If null (race), allow close.
        var baseline = slot.OpenConfirmedAtUtc ?? slot.OpenedAtUtc;
        if (baseline is null) return true;

        var configuredHolding = slot.HoldingSeconds > 0
            ? slot.HoldingSeconds
            : _lastSeenStartTimeHold;
        var effectiveHolding = Math.Max(configuredHolding, _state.PostOpenLockSeconds);

        if (effectiveHolding <= 0) return true;

        return (nowUtc - baseline.Value) >= TimeSpan.FromSeconds(effectiveHolding);
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
