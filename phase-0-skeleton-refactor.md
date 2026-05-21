# Phase 0 — Skeleton refactor (no-op for user)

Mục tiêu: tạo abstraction multi-slot mới nhưng behavior y hệt single-slot hiện tại. Test cũ phải pass nguyên 100%.

---

## 0.1 Files cần tạo mới

### `TradeDesktop.Application/Services/Portfolio/PositionSlot.cs`
Class mutable đại diện 1 lệnh độc lập.

**Fields cần có:**
- `SlotId : int` (init-only, monotonic tăng theo open order)
- `PairId : string` (init-only, format `"AUTO-{SlotId:D4}-{rawMs}"`)
- `Side : TradingPositionSide` (set khi open trigger)
- `OpenMode : TradingOpenMode` (set khi open trigger)
- `Status : PositionSlotStatus` enum: `PendingOpen, Live, PendingClose, Closed`
- `TicketA, TicketB : ulong?` (null khi pending)
- `OpenedAtUtc : DateTime?` (lúc engine trigger open)
- `OpenConfirmedAtUtc : DateTime?` (lúc MMF confirm cả 2 leg)
- `ClosedAtUtc : DateTime?`
- `CloseConfirmedAtUtc : DateTime?`
- `HoldingSeconds : int`
- `IsCloseExecutionPending : bool`
- `LastProfitSnapshot : double?` (cập nhật từ MMF poll)
- `CloseSignalEngine : CloseSignalEngine` (instance riêng, init trong constructor)

**Methods:**
- Constructor nhận `SlotId, PairId`.
- `MarkOpenTriggered(side, mode, triggerAtUtc, holdingSeconds)`.
- `MarkOpenConfirmed(ticketA, ticketB, confirmedAtUtc)`.
- `MarkCloseTriggered(triggerAtUtc)` set `IsCloseExecutionPending=true`.
- `MarkCloseConfirmed(closedAtUtc)` set `Status=Closed`.
- `IsHoldingTimeElapsed(now)` check `now - OpenConfirmedAtUtc >= HoldingSeconds`.

### `TradeDesktop.Application/Services/Portfolio/PositionSlotStatus.cs`
Enum: `PendingOpen, Live, PendingClose, Closed`.

### `TradeDesktop.Application/Services/Portfolio/PortfolioState.cs`
Container quản lý collection slots + global counters.

**Fields:**
- `Slots : List<PositionSlot>` (kể cả Closed, giữ cho audit Phase sau)
- `MaxTotalOpens : int` (init 1 cho Phase 0)
- `MaxBuyOpens : int` (init 1)
- `MaxSellOpens : int` (init 1)
- `GlobalActionLockUntilUtc : DateTime?`
- `GlobalCooldownMinSec : int` (init 0 cho Phase 0)
- `GlobalCooldownMaxSec : int` (init 0)
- `LastOpenConfirmedAtUtc : DateTime?`
- `LastOpenConfirmedSide : TradingPositionSide`
- `_nextSlotId : int` private counter

**Methods (read-only counters):**
- `CountLiveAndPendingBuy() : int` — slots có Side=Buy && Status ∈ {PendingOpen, Live, PendingClose}
- `CountLiveAndPendingSell() : int` — tương tự
- `CountLiveAndPendingTotal() : int`
- `GetLiveSlots() : IEnumerable<PositionSlot>` — Status=Live
- `GetSlotByPairId(pairId) : PositionSlot?`
- `GetSlotByTicket(ticket) : PositionSlot?`

**Methods (mutation):**
- `AllocateNewSlot(pairId) : PositionSlot` — increment `_nextSlotId`, add vào list, return.
- `RemoveClosed()` — cleanup periodic (gọi từ poll).

### `TradeDesktop.Application/Abstractions/IPortfolioCoordinator.cs`
Interface chính cho ViewModel sử dụng.

```csharp
public interface IPortfolioCoordinator
{
    // Trạng thái tổng quan
    int LiveCount { get; }
    int PendingCount { get; }
    int LiveBuyCount { get; }
    int LiveSellCount { get; }
    IReadOnlyList<PositionSlot> LiveSlots { get; }

    // Snapshot pipeline (mỗi tick 50ms)
    PortfolioSnapshotResult ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config);

    // Slot lifecycle (gọi từ ViewModel khi MMF confirm)
    PositionSlot? AllocatePendingOpenSlot(string pairId, GapSignalTriggerResult trigger);
    void MarkSlotOpenConfirmed(string pairId, ulong ticketA, ulong ticketB, DateTime confirmedAtUtc);
    void MarkSlotCloseTriggered(string pairId, DateTime triggeredAtUtc);
    void MarkSlotCloseConfirmed(string pairId, DateTime confirmedAtUtc);

    // Profit tracking (gọi từ MMF poll)
    void UpdateProfit(ulong ticket, double profit);

    // Rollback (open execution failed, close execution failed)
    void AbortPendingOpen(string pairId);
    void AbortPendingClose(string pairId);

    // Recovery & reset
    void Reset();
    void RecoverSlotsFromPersisted(IEnumerable<RecoveredSlotData> slots);
}

public sealed record PortfolioSnapshotResult(
    GapSignalTriggerResult? OpenTrigger,        // null nếu không có open
    PositionSlot? CloseTargetSlot,              // null nếu không có close
    GapSignalTriggerResult? CloseTrigger);       // trigger ứng với CloseTargetSlot

public sealed record RecoveredSlotData(
    int SlotId,
    string PairId,
    TradingPositionSide Side,
    TradingOpenMode OpenMode,
    ulong TicketA,
    ulong TicketB,
    DateTime OpenConfirmedAtUtc,
    int HoldingSeconds);
```

### `TradeDesktop.Application/Services/Portfolio/PortfolioCoordinator.cs`
Implementation chính. Phase 0 chỉ wrap 1 slot.

**Internal state:**
- `_portfolioState : PortfolioState` (private)
- `_openSignalEngine : IGapSignalConfirmationEngine` (shared, inject qua DI)
- `_random : Random`
- `_lastSeenStartTimeHold, _lastSeenEndTimeHold : int` (giống TradingFlowEngine cũ)

**ProcessSnapshot logic (Phase 0 — cap=1):**

```
1. Cache _lastSeen* từ config.
2. Resolve effectiveNow từ snapshot.TimestampUtc + wall-clock fallback.
3. Check global cooldown: if (GlobalActionLockUntilUtc.HasValue && effectiveNow < it) return empty result.
4. === OPEN path ===
   if (LiveAndPendingTotal < MaxTotalOpens):
     triggers = _openSignalEngine.ProcessSnapshot(snapshot, config);
     openTrigger = triggers.FirstOrDefault(t => t.Triggered && t.Action == Open);
     if openTrigger != null:
       return new(OpenTrigger=openTrigger, CloseTargetSlot=null, CloseTrigger=null);
5. === CLOSE path ===
   eligibleCloses = []
   foreach slot in LiveSlots where !IsCloseExecutionPending && IsHoldingTimeElapsed:
     closeTrigger = slot.CloseSignalEngine.ProcessSnapshot(snapshot, config, slot.OpenMode);
     if closeTrigger != null && closeTrigger.Triggered && closeTrigger.Action == Close:
       eligibleCloses.add((slot, closeTrigger));
   if eligibleCloses.Count > 0:
     winner = eligibleCloses.OrderByDescending(x => x.slot.LastProfitSnapshot ?? double.MinValue).First();
     return new(OpenTrigger=null, CloseTargetSlot=winner.slot, CloseTrigger=winner.closeTrigger);
6. return empty.
```

**AllocatePendingOpenSlot:**

```
slot = _portfolioState.AllocateNewSlot(pairId);
slot.MarkOpenTriggered(
    side: trigger.PrimarySide == Buy ? Buy : Sell,
    mode: trigger.TriggerType == OpenByGapBuy ? GapBuy : GapSell,
    triggerAtUtc: trigger.TriggeredAtUtc,
    holdingSeconds: NextSecondsInRange(config.StartTimeHold, config.EndTimeHold));
return slot;
```

**MarkSlotOpenConfirmed:**

```
slot = GetSlotByPairId(pairId);
slot.MarkOpenConfirmed(ticketA, ticketB, confirmedAtUtc);
slot.Status = Live;
// Phase 0: KHÔNG kích cooldown, KHÔNG update LastOpenConfirmed*.
// Để giữ behavior cũ identical. Sẽ enable ở Phase 2.
```

**MarkSlotCloseConfirmed:**

```
slot.MarkCloseConfirmed(confirmedAtUtc);
slot.Status = Closed;
// Phase 0: tính CurrentWaitSeconds giống TradingFlowEngine cũ để adapter tương thích.
// Random Uniform(_lastSeenStartWait, _lastSeenEndWait), set GlobalActionLockUntilUtc.
```

### `TradeDesktop.Application/Services/Portfolio/PortfolioCoordinatorAdapter.cs`
Adapter implement `ITradingFlowEngine` cũ, delegate sang `PortfolioCoordinator`.

Mục đích: code cũ trong `DashboardViewModel` (gọi `_tradingFlowEngine.ProcessSnapshot`, `_tradingFlowEngine.CurrentPhase`, etc.) chạy nguyên không cần sửa. Adapter map state portfolio sang scalar API cũ.

**Mapping:**
- `CurrentPhase`:
  - Có pending open chưa confirm → `WaitingCloseFromGap*` (theo OpenMode của slot pending)
  - Có Live slot → `WaitingCloseFromGap*` (theo OpenMode của slot live đầu tiên)
  - Không có slot live/pending → `WaitingOpen`
- `CurrentOpenMode`, `CurrentPositionSide`, `OpenedAtUtc`, `ClosedAtUtc`: lấy từ slot Live đầu tiên (Phase 0 chỉ có max 1).
- `CurrentHoldingSeconds`: từ slot đó.
- `CurrentWaitSeconds`: tính từ `GlobalActionLockUntilUtc - ClosedAtUtc`.
- `ProcessSnapshot(snapshot, config)`: gọi `coordinator.ProcessSnapshot`, convert `PortfolioSnapshotResult` thành `GapSignalTriggerResult?` cũ (chỉ trả về OpenTrigger hoặc CloseTrigger, ưu tiên close nếu cả 2 cùng có — nhưng Phase 0 cap=1 không thể có cả 2).
- `BeginWaitAfterClose(closeCompletedAtUtc, startWait, endWait)`: gọi `coordinator.MarkSlotCloseConfirmed`.
- `AbortPendingCloseExecution()`: tìm slot có `IsCloseExecutionPending=true`, gọi `coordinator.AbortPendingClose(slot.PairId)`.
- `AbortPendingOpenExecution()`: tương tự.
- `ForceWaitingClose(side)`, `ForceWaitingOpen()`, `Reset()`: implement bằng cách reset portfolio state.
- `TryConsumeQualifyingForOpen/Close`, `ResetQualifyingCounters`: giữ counter trong adapter (Phase 0 chưa rời sang coordinator).
- `LastSkipDiagnostic`: forward từ coordinator.

---

## 0.2 Files cần sửa

### `TradeDesktop.App/Composition/DependencyInjectionExtensions.cs` (hoặc nơi đăng ký DI)
- Đăng ký `IPortfolioCoordinator` → `PortfolioCoordinator` (singleton).
- `ITradingFlowEngine` đổi từ `TradingFlowEngine` → `PortfolioCoordinatorAdapter`.
- Giữ `IOpenSignalEngine`, `ICloseSignalEngine` đăng ký cũ.

### `TradeDesktop.Application/Services/TradingFlowEngine.cs`
**Không xóa.** Mark `[Obsolete("Use IPortfolioCoordinator. Kept for reference in Phase 0.")]` nhưng giữ code cũ nguyên để có thể revert nhanh nếu adapter sai.

---

## 0.3 Tests

### `TradeDesktop.Tests/Portfolio/PositionSlotTests.cs`
- `Constructor_SetsSlotIdAndPairId`
- `MarkOpenTriggered_SetsStatusToPendingOpen`
- `MarkOpenConfirmed_SetsStatusToLive_AndTickets`
- `MarkCloseTriggered_SetsIsCloseExecutionPending`
- `MarkCloseConfirmed_SetsStatusToClosed`
- `IsHoldingTimeElapsed_FalseBeforeHolding`
- `IsHoldingTimeElapsed_TrueAfterHolding`
- `IsHoldingTimeElapsed_FalseWhenOpenConfirmedAtUtcNull`

### `TradeDesktop.Tests/Portfolio/PortfolioStateTests.cs`
- `AllocateNewSlot_IncreasesSlotId`
- `CountLiveAndPendingBuy_CountsPendingAndLive_NotClosed`
- `CountLiveAndPendingTotal_SumsBuyAndSell`
- `GetSlotByPairId_ReturnsCorrectSlot`
- `GetSlotByTicket_ReturnsSlotWhenTicketAMatches`
- `GetSlotByTicket_ReturnsSlotWhenTicketBMatches`
- `RemoveClosed_KeepsOnlyNonClosedSlots`

### `TradeDesktop.Tests/Portfolio/PortfolioCoordinatorTests.cs`
- `ProcessSnapshot_WhenNoSlots_ReturnsOpenTriggerOnSignal`
- `ProcessSnapshot_WhenMaxTotalReached_DoesNotReturnOpenTrigger` (cap=1, đã có 1 slot Live)
- `ProcessSnapshot_WhenInGlobalCooldown_ReturnsEmpty`
- `ProcessSnapshot_WhenLiveSlotHoldingNotElapsed_DoesNotCheckClose`
- `ProcessSnapshot_WhenLiveSlotHoldingElapsed_ReturnsCloseTrigger`
- `AllocatePendingOpenSlot_AddsSlotWithCorrectFields`
- `MarkSlotOpenConfirmed_TransitionsToLive`
- `MarkSlotCloseConfirmed_TransitionsToClosed_AndKicksGlobalCooldown`
- `AbortPendingOpen_RemovesSlot`

### `TradeDesktop.Tests/PortfolioCoordinatorAdapterTests.cs`
- **Mục đích quan trọng nhất**: prove rằng adapter API == TradingFlowEngine cũ.
- Copy 100% test cases từ `TradingFlowEngineTests.cs`, đổi `var sut = new TradingFlowEngine(...)` thành `var sut = new PortfolioCoordinatorAdapter(new PortfolioCoordinator(...))`. Tất cả assertions phải pass.
- Đây là contract test bảo đảm Phase 0 no-op.

### Giữ nguyên (không sửa):
- `TradeDesktop.Tests/TradingFlowEngineTests.cs` — test class cũ, vẫn chạy trên `TradingFlowEngine` nguyên gốc (vì class chưa xóa).
- `GapSignalConfirmationEngineTests.cs`, `CloseSignalEngineTests.cs`, `SignalEntryGuardTests.cs`, etc.

---

## 0.4 Acceptance criteria Phase 0

- [ ] `dotnet build` clean, no warnings về `[Obsolete]` (suppress trong project file).
- [ ] `dotnet test` pass 100% (cả test cũ + test mới).
- [ ] PortfolioCoordinatorAdapterTests = TradingFlowEngineTests (100% same assertions).
- [ ] Chạy app, click Start session, mở 1 lệnh auto, đợi close → behavior y hệt trước (CurrentPhase, CurrentPositionText, signal log identical).
- [ ] Không có thay đổi nào ở UI (XAML không sửa).
- [ ] Không có thay đổi nào ở DB schema.
- [ ] PR description liệt kê 6 file mới + 1 file sửa DI + Obsolete annotation.

---

## 0.5 Lưu ý implementation

**SlotId tăng monotonic theo open order**, không tái sử dụng. SlotId 1, 2, 3, ... cứ thế tăng. Khi slot close, ID không recycle. Mục đích: dễ debug log, mỗi lệnh có ID unique trong session.

**`_openSignalEngine` shared:** trong Phase 0 chỉ có 1 slot tại 1 thời điểm nên reset state sau trigger không gây mất tiến độ. Phase 2 sẽ cần xem lại logic reset khi quota chặn.

**Adapter mapping multiple Live slots → 1 scalar:** Phase 0 cap=1 nên luôn ≤ 1 Live slot, mapping đơn giản. Phase 1 cần re-think nhưng tạm thời Phase 0 cứ pick FirstOrDefault.

**Cooldown trong Phase 0:** dùng `start_wait_time / end_wait_time` từ config cũ làm `GlobalCooldownMin/Max`. Phase 2 mới đổi sang 3/125 hardcode default + field DB mới.

**Random `_random : Random`:** dùng instance riêng trong PortfolioCoordinator, không share với TradingFlowEngine cũ (để testable). Test có thể inject seeded Random qua constructor optional.

---

Sau khi bạn confirm done Phase 0 (build pass, test pass, smoke test app OK), reply "done Phase 0" hoặc paste output `dotnet test`, tôi sẽ ra Phase 1 plan.
