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
    DateTime? LastOpenConfirmedAtUtc { get; }
    TradingPositionSide LastOpenConfirmedSide { get; }
    TradingFlowSkipDiagnostic? LastSkipDiagnostic { get; }
    int GlobalCooldownMinSec { get; }
    int GlobalCooldownMaxSec { get; }

    // === Phase 7 metrics (monitoring) ===
    PortfolioMetrics GetMetrics();

    // === Snapshot pipeline (Phase 1 entry point) ===
    PortfolioSnapshotResult ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config);

    // === Slot lifecycle ===
    PositionSlot? AllocatePendingOpenSlot(string pairId, GapSignalTriggerResult trigger);
    void MarkSlotOpenConfirmed(string pairId, ulong ticketA, ulong ticketB, DateTime confirmedAtUtc);
    void MarkSlotCloseTriggered(string pairId, DateTime triggeredAtUtc);
    void MarkSlotCloseConfirmed(string pairId, DateTime confirmedAtUtc);

    // Phase 8: kick global cooldown trực tiếp (cho path không qua slot lifecycle:
    // external close của lệnh mồ côi, cleanup orphan, ...).
    void KickGlobalCooldown(DateTime triggeredAtUtc, string reasonSuffix);

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

    // Net P/L thật từ broker (HistorySharedRecord.Profit) lúc lệnh đóng — chỉ log/hiển thị,
    // KHÔNG tham gia quyết định TP/Rule D.
    void UpdateRealizedCloseProfit(ulong ticket, double profit);

    // === Rule checks (Phase 2) ===
    bool CanOpenNewSlot(TradingPositionSide side, out string blockReason);
    bool CanCloseNow(out string blockReason);

    // === Config sync from RuntimeConfigState (Phase 2) ===
    void UpdateQuotaConfig(int maxTotal, int maxBuy, int maxSell);
    void UpdateCooldownConfig(int minSec, int maxSec);
    void UpdateMaxLifeTimeConfig(int maxLifeTimeSec);

    // === Rollback (open/close execution failed) ===
    void AbortPendingOpen(string pairId);
    void AbortPendingClose(string pairId);

    // === Reset & recovery ===
    void Reset();
    void ClearAllSlots();
    void RecoverSlotsFromPersisted(IEnumerable<RecoveredSlotData> slots);
}

public sealed record PortfolioSnapshotResult(
    GapSignalTriggerResult? OpenTrigger,
    PositionSlot? CloseTargetSlot,
    GapSignalTriggerResult? CloseTrigger)
{
    public static PortfolioSnapshotResult Empty { get; } = new(null, null, null);
}

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
    int HoldingSeconds);
