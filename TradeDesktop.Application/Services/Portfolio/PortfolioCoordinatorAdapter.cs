using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Adapter implements ITradingFlowEngine cũ bằng cách map scalar API sang
/// PortfolioCoordinator multi-slot state. Mục đích:
/// 1) Test contract — PortfolioCoordinatorAdapterTests mirror TradingFlowEngineTests 100%.
/// 2) Rollback safety — nếu ViewModel migration (Phase 1+) lỗi, có thể revert
///    về adapter mode mà không sửa coordinator.
///
/// Phase 0 cap=1: adapter tự allocate "synthetic" slot khi ProcessSnapshot trả Open
/// trigger để map về CurrentPhase=WaitingClose* immediately. ViewModel migrated (Phase 1+)
/// sẽ gọi coordinator.ProcessSnapshot trực tiếp + allocate slot tự, không qua adapter.
/// </summary>
public sealed class PortfolioCoordinatorAdapter : ITradingFlowEngine
{
    private readonly PortfolioCoordinator _coordinator;

    // Adapter-owned scalars (không có equivalent trong slot model):
    // qualifying counters và wait-after-close state.
    private int _openQualifyingCount;
    private int _closeQualifyingCount;
    private DateTime? _adapterClosedAtUtc;
    private DateTime? _adapterClosedAtRuntimeUtc;
    private int _adapterCurrentWaitSeconds;

    public PortfolioCoordinatorAdapter(IPortfolioCoordinator coordinator)
    {
        _coordinator = (PortfolioCoordinator)coordinator;
    }

    // ===== Scalar mapping from slot state =====
    public TradingFlowPhase CurrentPhase
    {
        get
        {
            var slot = FirstNonClosedSlot();
            if (slot is null) return TradingFlowPhase.WaitingOpen;
            return slot.OpenMode == TradingOpenMode.GapBuy
                ? TradingFlowPhase.WaitingCloseFromGapBuy
                : TradingFlowPhase.WaitingCloseFromGapSell;
        }
    }

    public TradingOpenMode CurrentOpenMode => FirstNonClosedSlot()?.OpenMode ?? TradingOpenMode.None;
    public TradingPositionSide CurrentPositionSide => FirstNonClosedSlot()?.Side ?? TradingPositionSide.None;
    public DateTime? OpenedAtUtc => FirstNonClosedSlot()?.OpenedAtUtc;
    public DateTime? ClosedAtUtc => _adapterClosedAtUtc;
    public int CurrentHoldingSeconds => FirstNonClosedSlot()?.HoldingSeconds ?? 0;
    public int CurrentWaitSeconds => _adapterCurrentWaitSeconds;
    public int CurrentOpenQualifyingCount => _openQualifyingCount;
    public int CurrentCloseQualifyingCount => _closeQualifyingCount;
    public TradingFlowSkipDiagnostic? LastSkipDiagnostic => _coordinator.LastSkipDiagnostic;

    // ===== ProcessSnapshot (legacy single-trigger API) =====
    public GapSignalTriggerResult? ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config)
    {
        // Wait-gate: if we're in waiting-open period after a close, gate by adapter's
        // wait seconds against wall clock (mirrors TradingFlowEngine.CanCheckOpen).
        if (FirstNonClosedSlot() is null && _adapterClosedAtUtc.HasValue && _adapterCurrentWaitSeconds > 0)
        {
            var effectiveNow = ResolveEffectiveNow(snapshot.TimestampUtc);
            var baseline = _adapterClosedAtRuntimeUtc ?? _adapterClosedAtUtc.Value;
            var elapsed = effectiveNow - baseline;
            if (elapsed < TimeSpan.FromSeconds(_adapterCurrentWaitSeconds))
            {
                return null;
            }
        }

        var result = _coordinator.ProcessSnapshot(snapshot, config);

        if (result.OpenTrigger is not null)
        {
            // Auto-allocate synthetic slot so adapter scalar mapping reflects WaitingClose phase.
            var pairId = $"ADAPTER-{Environment.TickCount64}-{Guid.NewGuid():N}";
            var slot = _coordinator.AllocatePendingOpenSlot(pairId, result.OpenTrigger);
            // Slot may be null only if rule check fails — but ProcessSnapshot already checked it.
            // Defensive: if null, just return trigger anyway.
            _ = slot;

            _adapterClosedAtUtc = null;
            _adapterClosedAtRuntimeUtc = null;
            _adapterCurrentWaitSeconds = 0;
            return result.OpenTrigger;
        }

        if (result.CloseTrigger is not null)
        {
            // Coordinator already marked slot PendingClose internally in ProcessSnapshot.
            // Adapter just returns the trigger; ViewModel will call BeginWaitAfterClose later.
            _adapterClosedAtUtc = null;
            return result.CloseTrigger;
        }

        return null;
    }

    public void BeginWaitAfterClose(
        DateTime closeCompletedAtUtc,
        int startWaitSeconds,
        int endWaitSeconds)
    {
        // Find any PendingClose slot. If none, also accept Live slot
        // (resilience for race where AbortPendingCloseExecution cleared the flag).
        var slot = _coordinator.PendingCloseSlots.FirstOrDefault()
                   ?? _coordinator.LiveSlots.FirstOrDefault();
        if (slot is null)
        {
            return;
        }

        // Mark close confirmed in coordinator (also kicks cooldown if configured).
        _coordinator.MarkSlotCloseConfirmed(slot.PairId, closeCompletedAtUtc);

        // Remove the slot so adapter's CurrentPhase maps back to WaitingOpen.
        _coordinator.State.RemoveSlot(slot);

        // Compute adapter wait seconds (random Uniform).
        _adapterClosedAtUtc = closeCompletedAtUtc;
        _adapterClosedAtRuntimeUtc = DateTime.UtcNow;
        _adapterCurrentWaitSeconds = NextSecondsInRange(startWaitSeconds, endWaitSeconds);

        ResetQualifyingCounters();
    }

    public void AbortPendingCloseExecution()
    {
        var slot = _coordinator.PendingCloseSlots.FirstOrDefault();
        if (slot is null) return;

        _coordinator.AbortPendingClose(slot.PairId);
        _adapterClosedAtUtc = null;
        _adapterClosedAtRuntimeUtc = null;
        _adapterCurrentWaitSeconds = 0;
    }

    public void AbortPendingOpenExecution()
    {
        var slot = FirstNonClosedSlot();
        if (slot is null) return;

        if (slot.Status == PositionSlotStatus.PendingOpen)
        {
            _coordinator.AbortPendingOpen(slot.PairId);
        }
        else
        {
            // Slot is Live or PendingClose — remove anyway (mirrors engine cũ's reset behavior).
            _coordinator.State.RemoveSlot(slot);
        }

        _adapterClosedAtUtc = null;
        _adapterClosedAtRuntimeUtc = null;
        _adapterCurrentWaitSeconds = 0;
        // Keep qualifying counters (mirrors engine cũ §AbortPendingOpenExecution).
    }

    public bool TryConsumeQualifyingForOpen(int requiredN)
    {
        var effectiveN = Math.Max(1, requiredN);
        _openQualifyingCount++;
        if (_openQualifyingCount >= effectiveN)
        {
            _openQualifyingCount = 0;
            return true;
        }
        return false;
    }

    public bool TryConsumeQualifyingForClose(int requiredN)
    {
        var effectiveN = Math.Max(1, requiredN);
        _closeQualifyingCount++;
        if (_closeQualifyingCount >= effectiveN)
        {
            _closeQualifyingCount = 0;
            return true;
        }
        return false;
    }

    public void ResetQualifyingCounters()
    {
        _openQualifyingCount = 0;
        _closeQualifyingCount = 0;
    }

    public void ForceWaitingClose(TradingPositionSide positionSide)
    {
        if (positionSide == TradingPositionSide.None) return;

        // Clear close-related state (mirrors engine cũ ForceWaitingClose).
        _adapterClosedAtUtc = null;
        _adapterClosedAtRuntimeUtc = null;
        _adapterCurrentWaitSeconds = 0;

        var mode = positionSide == TradingPositionSide.Buy
            ? TradingOpenMode.GapBuy
            : TradingOpenMode.GapSell;

        // Find existing non-closed slot or create synced one.
        var existing = FirstNonClosedSlot();
        if (existing is not null)
        {
            // Update existing slot's side/mode (mirrors engine cũ overwriting state).
            existing.MarkSynced(
                side: positionSide,
                mode: mode,
                ticketA: existing.TicketA,
                ticketB: existing.TicketB,
                openConfirmedAtUtc: existing.OpenConfirmedAtUtc ?? existing.OpenedAtUtc ?? DateTime.UtcNow,
                holdingSeconds: existing.HoldingSeconds > 0 ? existing.HoldingSeconds : FallbackHoldingSeconds());
            return;
        }

        var holdingSeconds = FallbackHoldingSeconds();
        _coordinator.RegisterSyncedSlot(
            pairId: $"FORCE-{Environment.TickCount64}-{Guid.NewGuid():N}",
            side: positionSide,
            openMode: mode,
            ticketA: null,
            ticketB: null,
            openConfirmedAtUtc: DateTime.UtcNow,
            holdingSeconds: holdingSeconds);
    }

    public void ForceWaitingOpen()
    {
        _coordinator.ClearAllSlots();
        _adapterClosedAtUtc = null;
        _adapterClosedAtRuntimeUtc = null;
        _adapterCurrentWaitSeconds = 0;
        ResetQualifyingCounters();
    }

    public void Reset()
    {
        ForceWaitingOpen();
        _coordinator.Reset();
    }

    // ===== Helpers =====
    private PositionSlot? FirstNonClosedSlot()
    {
        // Order: Live → PendingClose → PendingOpen (most "active" first).
        return _coordinator.LiveSlots.FirstOrDefault()
            ?? _coordinator.PendingCloseSlots.FirstOrDefault()
            ?? _coordinator.PendingOpenSlots.FirstOrDefault();
    }

    private int FallbackHoldingSeconds()
    {
        // Mirror TradingFlowEngine.ForceWaitingClose: nếu chưa có HoldingSeconds, random
        // lại từ config hold range gần nhất mà coordinator đã thấy qua ProcessSnapshot.
        return _coordinator.LastSeenStartTimeHold > 0
            ? NextSecondsInRange(_coordinator.LastSeenStartTimeHold, _coordinator.LastSeenEndTimeHold)
            : 0;
    }

    private static readonly TimeSpan SnapshotWallClockTolerance = TimeSpan.FromMinutes(5);
    private static DateTime ResolveEffectiveNow(DateTime snapshotTimestampUtc)
    {
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

    // Adapter uses its own deterministic Random (not coordinator's) so test parity holds
    // even when tests run coordinator + adapter separately.
    private readonly Random _adapterRandom = new();
    private int NextSecondsInRange(int minSeconds, int maxSeconds)
    {
        var min = Math.Max(0, minSeconds);
        var max = Math.Max(0, maxSeconds);
        if (min > max) (min, max) = (max, min);
        if (min == max) return min;
        return _adapterRandom.Next(min, max + 1);
    }
}
