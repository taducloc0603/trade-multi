using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Application.Abstractions;

/// <summary>
/// Multi-slot portfolio orchestrator. ViewModel dùng làm entry point chính sau Phase 1.
/// Engine cũ (TradingFlowEngine) chỉ còn được wrap qua PortfolioCoordinatorAdapter cho
/// rollback safety và UI display (CurrentPhaseText scalar).
/// </summary>
public interface IPortfolioCoordinator
{
    // === State queries (Phase 0 + Phase 1 + Phase 6 UI binding) ===
    int LiveCount { get; }
    int PendingCount { get; }
    int LiveBuyCount { get; }
    int LiveSellCount { get; }
    int LiveAndPendingTotalCount { get; }
    IReadOnlyList<PositionSlot> LiveSlots { get; }
    IReadOnlyList<PositionSlot> PendingOpenSlots { get; }
    IReadOnlyList<PositionSlot> PendingCloseSlots { get; }

    // === Diagnostic info (Phase 6 UI status bar) ===
    DateTime? GlobalActionLockUntilUtc { get; }
    bool HasNonAutoCloseInFlight { get; }
    DateTime? LastOpenConfirmedAtUtc { get; }
    TradingPositionSide LastOpenConfirmedSide { get; }
    DateTime? LastCloseConfirmedAtUtc { get; }
    int OppositeSideLockSeconds { get; }
    int RdStartPostCloseLockSeconds { get; }
    int RdEndPostCloseLockSeconds { get; }
    int LastSelectedPostCloseLockSeconds { get; }
    int RdStartPostOpenLockSeconds { get; }
    int RdEndPostOpenLockSeconds { get; }
    int RdStartSameActionLockSeconds { get; }
    int RdEndSameActionLockSeconds { get; }
    int GlobalCooldownMinSec { get; }
    int GlobalCooldownMaxSec { get; }
    RandomQuotaState RandomQuotaState { get; }
    TradeActionGateResult TryAcquireTradeAction(
        DateTime requestedAtUtc,
        string action,
        string source,
        TradeActionOrigin origin = TradeActionOrigin.Auto,
        TradingPositionSide side = TradingPositionSide.None,
        string? pairId = null);
    void BeginNonAutoCloseOperation(string operationId);
    void EndNonAutoCloseOperation(string operationId);

    // === Phase 7 metrics (monitoring) ===
    PortfolioMetrics GetMetrics();

    // === Snapshot pipeline (Phase 1 entry point) ===
    PortfolioSnapshotResult ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config);

    // === Slot lifecycle ===
    PositionSlot? AllocatePendingOpenSlot(string pairId, GapSignalTriggerResult trigger);
    SlotAllocationResult AllocatePendingOpenSlotWithReason(string pairId, GapSignalTriggerResult trigger);
    void MarkSlotOpenConfirmed(string pairId, ulong ticketA, ulong ticketB, DateTime confirmedAtUtc);
    void MarkSlotCloseTriggered(string pairId, DateTime triggeredAtUtc);
    bool TryClaimSlotClose(string pairId, CloseExecutionOwner owner, DateTime triggeredAtUtc);
    void MarkSlotCloseConfirmed(string pairId, DateTime confirmedAtUtc);

    // Manual per-pair close finalize: confirm (nếu chưa Closed) + remove slot theo pairId.
    // Coordinator-only — KHÔNG kick cooldown lại, KHÔNG đụng auto-cycle ViewModel state.
    void CloseSlotManually(string pairId, DateTime confirmedAtUtc);

    // Recovery (Phase 5) + manual flow (Phase 1)
    PositionSlot RegisterSyncedSlot(
        string pairId,
        TradingPositionSide side,
        TradingOpenMode openMode,
        ulong? ticketA,
        ulong? ticketB,
        DateTime openConfirmedAtUtc,
        int holdingSeconds);

    // === Slot queries ===
    PositionSlot? GetSlotByPairId(string pairId);
    PositionSlot? GetSlotByTicket(ulong ticket);

    // === Profit tracking (Phase 1 MMF poll; Phase 2 Rule D priority close) ===
    void UpdateProfit(ulong ticket, double profit);

    // === Rule checks (Phase 2) ===
    bool CanOpenNewSlot(TradingPositionSide side, out string blockReason);
    bool CanCloseNow(out string blockReason);

    // === Config sync from RuntimeConfigState (Phase 2) ===
    void UpdateQuotaConfig(int maxTotal, int minBuy, int maxBuy, int minSell, int maxSell);
    void EnableRandomQuota();
    void RerollRandomQuota();
    void RestoreRandomQuotaState(RandomQuotaState state);
    void UpdateCooldownConfig(int minSec, int maxSec);
    void UpdateMaxLifeTimeConfig(int maxLifeTimeSec);
    void UpdateMinProfitToCloseConfig(double minProfitToClose);
    void UpdateOppositeSideLockConfig(int seconds);
    void UpdatePostCloseLockConfig(int startSeconds, int endSeconds);
    void UpdatePostOpenLockConfig(int startSeconds, int endSeconds);
    void UpdateSameActionLockConfig(int startSeconds, int endSeconds);
    void UpdateScheduleSleepingConfig(string? scheduleSleepingJson);

    // === Rollback (open/close execution failed) ===
    void AbortPendingOpen(string pairId);
    void AbortPendingClose(string pairId);

    // === Reset & recovery ===
    void Reset();
    void ClearAllSlots();
    void RecoverSlotsFromPersisted(IEnumerable<RecoveredSlotData> slots);
}

public sealed record RandomQuotaState(
    bool IsEnabled,
    int EffectiveMaxBuy,
    int EffectiveMaxSell,
    int OpenCountSinceRandom,
    int RandomAfterOpens,
    long CycleNumber,
    int? PreviousMaxBuy = null,
    int? PreviousMaxSell = null);

public sealed record SlotAllocationResult(PositionSlot? Slot, string BlockReason)
{
    public bool Success => Slot is not null;
}

public sealed record TradeActionGateResult(
    bool Acquired,
    DateTime? LockUntilUtc,
    TimeSpan Remaining,
    int CooldownSeconds,
    string Reason);

public enum TradeActionOrigin
{
    Auto = 0,
    Manual = 1,
    Recovery = 2
}

public enum AutoTradeActionType
{
    None = 0,
    Open = 1,
    Close = 2
}


public sealed record PortfolioSnapshotResult(
    GapSignalTriggerResult? OpenTrigger,
    PositionSlot? CloseTargetSlot,
    GapSignalTriggerResult? CloseTrigger,
    IReadOnlyList<PortfolioUiNotice>? UiNotices = null)
{
    public static PortfolioSnapshotResult Empty { get; } = new(null, null, null);
}

public sealed record PortfolioUiNotice(
    string Code,
    int SlotId,
    string Message);

/// <summary>
/// Phase 7: monitoring snapshot. Caller logs periodically to track health + skip
/// reason distribution. Counters reset trên Coordinator.Reset.
/// </summary>
public sealed record PortfolioMetrics(
    int CurrentLiveSlots,
    int CurrentLiveBuy,
    int CurrentLiveSell,
    int CurrentPendingOpen,
    int CurrentPendingClose,
    long TotalOpensAllTime,
    long TotalClosesAllTime,
    long QuotaSkipCount,
    long OppositeLockSkipCount,
    long CooldownSkipCount);

public sealed record RecoveredSlotData(
    int SlotId,
    string PairId,
    TradingPositionSide Side,
    TradingOpenMode OpenMode,
    ulong TicketA,
    ulong TicketB,
    DateTime OpenConfirmedAtUtc,
    int HoldingSeconds,
    int? HwndProfileIndex = null,
    string ChartHwndA = "",
    string TradeHwndA = "",
    string ChartHwndB = "",
    string TradeHwndB = "");
