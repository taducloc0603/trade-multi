# CLAUDE.md — Project context for AI assistants

> **Đọc file này trước khi đề xuất bất kỳ thay đổi nào.** Mô tả architecture,
> business rules, conventions, và pitfall thường gặp khi phát triển TradeDesktop.

---

## 0. Project rules (CRITICAL — đọc đầu tiên)

### 0.1 Risk Assessment (bắt buộc trước khi thực hiện)
Trước mỗi thay đổi, đánh giá theo các tiêu chí:
- Thay đổi có ảnh hưởng đến logic giao dịch hiện tại không? (signal engine, state machine, gap calculation, trade execution)
- Thay đổi có ảnh hưởng đến luồng dữ liệu hiện tại không? (shared memory reader, config loading, event flow)
- Thay đổi có side effect ngoài phạm vi yêu cầu không?

**Nếu có bất kỳ rủi ro nào → dừng lại và thông báo cho user trước khi tiếp tục.**

### 0.2 Không thay đổi logic hiện tại
- Chỉ thêm hoặc sửa đúng những gì được yêu cầu rõ ràng.
- Không refactor, không dọn dẹp code xung quanh, không tối ưu hóa ngoài phạm vi task.
- Không thay đổi behavior của các method/service đang hoạt động, dù thấy có thể cải thiện.
- Khi thêm code mới, đảm bảo không làm thay đổi kết quả của code cũ.

---

## 1. What this project does

TradeDesktop là WPF (.NET 8) app điều phối auto-trading qua 2 sàn MT4/MT5.
Đọc giá realtime từ shared memory, tính gap (`B.Bid - A.Ask`), khi gap đủ
mạnh trong cửa sổ confirm thì trigger open/close pair trên cả 2 sàn.

**Đặc thù multi-slot (Phase 0-7 refactor):** hỗ trợ tối đa 7 lệnh đồng thời,
mỗi lệnh là 1 `PositionSlot` độc lập với `CloseSignalEngine` riêng. Production
hiện tại vẫn cap=1 (RuntimeConfigState defaults) — cap-up qua DB config (Phase 5
DB integration deferred).

Đọc `README.md` cho chi tiết signal logic, gap formula, state machine.
Đọc `README.md` Section 13 cho multi-slot architecture.

---

## 2. Critical business rules (DO NOT violate)

### Rule A — Quota
- Max 7 lệnh tổng, max 4 Buy, max 4 Sell.
- Đếm bao gồm `PendingOpen + Live + PendingClose`.
- Config: `max_total_opens` / `max_buy_opens` / `max_sell_opens` (Phase 5 từ DB).
- Implementation: `coordinator.CanOpenNewSlot(side, out reason)`.

### Rule B — Auto cooldown + non-auto close barrier
- Auto dùng transition gate theo action/side trước và action/side kế tiếp; không dùng một global post-action timer.
- Open cùng chiều→Open cùng chiều và Close→Close: random 3–10s, sinh đúng một lần tại dispatch.
- Close→Open: random một lần trong `rd_start_post_close_lock_seconds..rd_end_post_close_lock_seconds`, lưu theo slot Auto Close. Open ngược chiều: `opposite_side_lock_seconds` + Open point policy.
- Open→Close theo phương án B: từng slot random một lần khi Open confirmed trong `rd_start_post_open_lock_seconds..rd_end_post_open_lock_seconds`, rồi chỉ eligible khi hết deadline riêng.
  Open slot mới không refresh thời gian Close của slot cũ.
- `GlobalActionLockUntilUtc` chỉ dành cho startup/recovery cooldown toàn cục, không dùng cho ma trận thường.
- Manual/recovery KHÔNG đọc/ghi Auto transition state và dùng non-auto close barrier.
- Khi manual/recovery đang xử lý, `HasNonAutoCloseInFlight=true`: chặn auto dispatch và disable toàn bộ
  nút Close đến khi MMF xác nhận pair cân bằng/flat. Barrier không có thời gian chờ thêm sau confirm.

### Rule C — Opposite-side OPEN lock + Post-close lock (2 giá trị ĐỘC LẬP)
- **2 cột DB riêng, mỗi cái default 300s** (`<= 0` → giữ default):
  - `opposite_side_lock_seconds` → `RuntimeConfigState.CurrentOppositeSideLockSeconds` → `coordinator.UpdateOppositeSideLockConfig` (`DefaultOppositeSideLockSeconds`).
  - post-close start/end → `RuntimeConfigState` → `coordinator.UpdatePostCloseLockConfig(start, end)` (`DefaultPostCloseLockSeconds`).
  - Cả 2 push trong `SyncPortfolioCoordinatorConfig`.
- **Lock 1 (sau OPEN)** — dùng `OppositeSideLockSeconds`: sau OPEN confirm → CHỈ block OPEN opposite-side. Same-side OPEN refresh timer. KHÔNG block CLOSE. State: `LastOpenConfirmedAtUtc`, `LastOpenConfirmedSide`.
- **Lock 2 (post-close auto)** — random tại Auto Close dispatch và lưu theo slot; sau MMF confirm, cùng duration được neo lại tại `CloseConfirmedAtUtc` để block Auto Open/re-entry;
  Auto Close tiếp theo chỉ chờ random 3–10s.
  Manual/recovery close không set `LastCloseConfirmedAtUtc` và không tạo/gia hạn auto cooldown.
- Cả 2 check nằm trong `CanOpenNewSlot` (chỉ chặn, không tạo open/close → không vi phạm Rule E). Block reason: `OPPOSITE_SIDE_LOCK` / `POST_CLOSE_LOCK`.

### Rule D — Priority close theo profit cao nhất
- Nhiều slot trigger close cùng tick → chỉ close 1 slot có `LastProfitSnapshot` cao nhất.
- Slot losers giữ nguyên close window — sẽ trigger lại tick sau nếu vẫn đủ điều kiện.
- Implementation: `coordinator.ProcessSnapshot` close path: `OrderByDescending(LastProfitSnapshot ?? double.MinValue)`.

### Rule E — Signal-only mandate (HIGHEST — đứng trên tất cả)
- **Quy tắc cao nhất**: MỌI điều kiện OPEN/CLOSE một vị thế cân bằng PHẢI bắt nguồn từ signal engine.
  - OPEN → `GapSignalConfirmationEngine` (qua `PortfolioCoordinator.ProcessSnapshot`).
  - CLOSE → `CloseSignalEngine` (TP hoặc Gap đảo chiều). Có signal mới được thực hiện.
- **Cấm tuyệt đối**: thêm path mở/đóng theo thời gian / loss / lifetime / thủ công mà BỎ QUA signal.
  - Force-close theo `max_life_time` đã bị gỡ (commit `01f1f50`). `max_life_time_by_second` CHỈ được dùng
    để **ưu tiên trong nhóm slot ĐÃ có close signal** (`eligibleCloses`) — KHÔNG bao giờ tự tạo close.
- **Exceptions hợp lệ** (KHÔNG coi là vi phạm — đây là recovery/integrity cho trạng thái BẤT THƯỜNG,
  không phải quyết định giao dịch theo thị trường):
  1. `CloseOpenedLegByTimeoutAsync` — rollback leg mở dở khi sàn kia fail trong `OpenPendingTimeoutMs`.
  2. `CloseRemainingLegAfterExternalCloseAsync` — đóng leg còn lại khi 1 leg bị đóng bên ngoài (EA/broker/tay).
  3. `RetryCloseLegByPendingAsync` — chỉ hoàn tất một close ĐÃ được signal trigger trước đó (retry execution).
  4. Manual open/close buttons (legacy `CloseOrderAsync`) — dormant, ẩn qua `IsManualTradeButtonsVisible=false` (Phase 6).
  4b. **Nút "Đóng" per-pair ở tab Trade** (grid `TradeRealtimeProfitRows`) → `ManualClosePairBySlotAsync(pairId)`.
     Đường RIÊNG coordinator-safe, ĐỘC LẬP với auto: resolve slot qua `GetSlotByPairId` (bấm là đóng ngay,
     không modal confirm), dùng chung lock vật lý `_closeDispatchInFlight` (chặn khi close khác in-flight), claim
     slot bằng `TryClaimSlotClose(..., CloseExecutionOwner.Manual, ...)` (PendingClose), còn router
     lấy physical mutex/non-auto gate lúc dispatch; finalize qua polling gọi `CloseSlotManually(pairId)`
     (confirm + remove slot). KHÔNG đụng `_activeAutoCycle` / `_activeAutoCloseRecoveryCycle` / `_autoSlot`,
     KHÔNG đăng ký isAutoFlow. Pending state dùng `PendingCloseOrigin.ManualPair` (không reset khi retry).
     Router chỉ cho phép reason `ManualPairClose` khi pair/slot/ticket và manual ownership khớp chính xác.
     Đây là user-override đóng tay, KHÔNG phải auto path bỏ qua signal.
  5. Watchdog self-heal resync — khi coordinator under-count drift (số tool-pair mở thật trên MMF >
     số slot coordinator giữ, nhưng trạng thái vật lý vẫn ≤ cap), `EvaluateAndApplyAutoOpenInvariantWatchdog`
     gọi `TryRebuildCoordinatorFromMmf` (dùng chung với resync lúc Start) để rebuild slot từ MMF, KHÔNG tạo
     open/close. Có guard: chỉ resync khi rebuild ≤ cap, rebuild > coordinator, không close/open dang dở,
     và throttle `WatchdogSelfHealMinIntervalSeconds`. Decision thuần ở `WatchdogSelfHealDecision.Decide`.
- **Khi thêm path mở/đóng mới**: nếu KHÔNG qua signal engine thì BẮT BUỘC phải là recovery/integrity
  rõ ràng, có guard chống đóng/mở nhầm, và phải document thêm vào danh sách exception ở trên.

### Rule F — Close ownership contract (KHÔNG ĐƯỢC THAY ĐỔI NGẦM)

- OPEN vị thế cân bằng chỉ được khởi phát bởi auto signal. Manual open legacy tiếp tục bị execution policy chặn.
- Có đúng 3 nguồn CLOSE hợp lệ:
  1. **Recovery** — rollback khi OPEN chỉ thành công một leg, hoặc đóng leg còn lại sau external partial close.
  2. **Auto** — `CloseSignalEngine` tạo strategic close signal hợp lệ.
  3. **Manual per-pair** — người dùng nhấn nút Close của một pair; dùng reason `ManualPairClose`, không cần signal.
- Quyền đóng phải được quản lý theo `pairId` bằng `CloseExecutionOwner`:
  - Một pair chỉ có tối đa một owner tại một thời điểm.
  - Recovery/manual đang xử lý pair X thì auto không được dispatch trùng pair X.
  - Auto đã claim pair X thì manual không được chiếm quyền; UI phải báo pair đang được xử lý.
  - Manual/recovery trên pair X không được sửa close engine, active cycle hoặc ownership của pair Y.
- Trạng thái bắt buộc:
  - Partial OPEN giữ `PendingOpen`, không được đưa vào tập auto-close eligible.
  - Auto/manual claim thành công chuyển slot `Live -> PendingClose`.
  - Bị block trước dispatch phải release claim và trả slot về `Live`.
  - Đã bắt đầu dispatch hoặc chỉ đóng thành công một leg phải giữ pending/retry; không trả slot về `Live` giả tạo.
  - Chỉ confirm/remove slot sau khi MMF xác nhận các ticket liên quan không còn mở.
- Manual per-pair authorization bắt buộc đúng source, pairId, slotId, đủ hai leg và ticket A/B khớp slot.
  `ManualOpen`/`ManualClose` legacy vẫn bị `MANUAL_WITHOUT_SIGNAL_DISABLED`.
- Ba flow tách biệt ở tầng quyết định/orchestration nhưng dùng chung lớp an toàn vật lý:
  `_closeDispatchInFlight`, `TryAcquireTradeAction`, ticket/row validation và pending-leg retry.
- Auto action gate không áp dụng timer cho manual/recovery. Thay vào đó, non-auto barrier chặn auto và disable
  các nút Close khác cho tới khi MMF confirm hoàn tất; sau đó nhả ngay, không tạo cooldown.
- Mọi OPEN/CLOSE phải đi qua physical dispatch mutex chung trong router. CLOSE phải truyền `TradeMapName` và
  router phải resolve lại `RowIndex` theo `Ticket` sau khi lấy mutex; map/ticket không hợp lệ thì fail closed,
  không fallback row 0. Policy/signal cũng phải được kiểm tra lại sau thời gian chờ mutex.
- Khi sửa logic này, bắt buộc giữ/pass `ManualPairClosePolicyTests`, các test `TryClaimSlotClose*`,
  `ManualClaim_DoesNotPreventAutoFromClaimingAnotherPair` và
  `PartialOpenRecoverySlot_CannotBeClaimedByAutoClose`.

---

## 3. Architecture map

### Layers
```
TradeDesktop.App/              # WPF UI, ViewModels, orchestration
TradeDesktop.Application/      # Business logic, signal engines, coordinator
TradeDesktop.Infrastructure/   # Shared memory readers, Supabase repo
TradeDesktop.Domain/           # Pure domain models
TradeDesktop.Tests/            # xUnit tests
```

### Multi-slot core files (Application layer)
- `Services/Portfolio/PortfolioCoordinator.cs` — orchestrator, ProcessSnapshot, rule checks
- `Services/Portfolio/PositionSlot.cs` — slot model với own CloseSignalEngine
- `Services/Portfolio/PortfolioState.cs` — slot collection + global counters
- `Services/Portfolio/PortfolioCoordinatorAdapter.cs` — wrap ITradingFlowEngine cho legacy paths
- `Services/Portfolio/CloseSignalEngineFactory.cs` — per-slot engine factory
- `Services/Portfolio/SlotPersistence.cs` — JSON serialize/deserialize cho DB JSONB
- `Abstractions/IPortfolioCoordinator.cs` — main interface
- `Abstractions/ISlotLogger.cs` — layer-clean logger (App impl forwards to ITradeSessionFileLogger)
- `Abstractions/ICloseSignalEngineFactory.cs`

### Signal engines (shared logic, đừng modify lightly)
- `GapCalculator.cs` — `GapBuy = (B.Bid - A.Ask) * Point`
- `GapSignalConfirmationEngine.cs` — open signal với hold-confirm window
- `CloseSignalEngine.cs` — close signal, **mỗi slot có 1 instance riêng**
- `SignalEntryGuard.cs` — 4 lớp guard: Latency / MaxGap / Spread / PriceFreeze

### Legacy single-slot (kept for reference)
- `Services/TradingFlowEngine.cs` — `[Obsolete]`, vẫn pass `TradingFlowEngineTests`
- DI registers `ITradingFlowEngine` → `PortfolioCoordinatorAdapter` (not the engine cũ)
- CS0618 suppressed via `<NoWarn>` in csproj

### Critical orchestration files
- `App/ViewModels/DashboardViewModel.cs` — main orchestrator (~5800 lines)
- `App/Services/TradeExecutionRouter.cs` — route pair open/close to MT4/MT5
- `App/Services/Mt4TradeExecutor.cs` / `Mt5TradeExecutor.cs` — native click via P/Invoke

---

## 4. Coding conventions

### General
- C# 12 / .NET 8.
- Nullable reference types enabled.
- Sealed classes by default.
- Records for immutable data (e.g., `PortfolioMetrics`, `RecoveredSlotData`).
- `Math.Abs` / `Math.Max(0, x)` normalize config inputs.

### Logging
- File logger: `Desktop/trade-log/` với rotation 50MB.
- Format: `[YYYY-MM-DD HH:mm:ss.fff] [CATEGORY][LEVEL] message`.
- Categories: `[VM]`, `[MMF_TRADES]`, `[CYCLE]`, `[ROUTER]`, `[MT4]`, `[MT5]`,
  `[GUARD]`, `[RECOVERY]`, `[WATCHDOG]`, `[SLOT]` (Phase 2+),
  `[CLOSE_SELECT]` (Phase 4), `[METRICS]` (Phase 7+).

### Async patterns
- `Task.Run` cho I/O không UI.
- `Application.Current.Dispatcher.Invoke` khi update WPF binding từ background.
- `Interlocked.CompareExchange` cho cờ in-flight + `Interlocked.Increment` cho metric counters.

### Naming
- Money/profit: `double` (MT4/MT5 native).
- Prices: `decimal` (precision).
- Time: `DateTime` (UTC) hoặc `DateTimeOffset` (local).
- Slots: `SlotId` là `int` monotonic, không bao giờ recycle trong session.

### Thread safety
- `PortfolioCoordinator._allocateLock` cho `AllocatePendingOpenSlot` (race protection).
- `Interlocked` cho metric counters.
- ViewModel mostly Dispatcher-thread → single-threaded by design.

---

## 5. Common pitfalls

### Race conditions
- MMF cập nhật ticket có thể trễ vài trăm ms đến vài giây so với tool click.
- LUÔN dùng `_autoOpenInFlight` / `_autoCloseInFlight` cờ.
- Capture pending request BEFORE dispatch executor (tránh race với poll).
- `AllocatePendingOpenSlot` quota check phải dưới `lock(_allocateLock)`.

### Multi-slot specific
- KHÔNG share `CloseSignalEngine` giữa các slot — mỗi slot 1 instance (`ICloseSignalEngineFactory`).
- KHÔNG reset close window của slot loser (Rule D) — sẽ mất tiến độ.
- KHÔNG dùng `_pendingOpenPairById.Values.Any(...)` để block — đã chuyển sang quota check Rule A.
- Per-side debounce: `_lastAutoOpenBuyAtLocal` / `Sell`. KHÔNG dùng chung `_lastAutoOpenClickAtLocal`.
- Per-side in-flight lock: `_autoOpenInFlightBuy` / `Sell` (Phase 3).
- Grid Close per-pair dùng chung `TradeRealtimeProfitRows` với profit realtime. KHÔNG `Clear()` rồi tạo lại
  toàn bộ collection mỗi UI refresh: Button có thể bị thay giữa mouse-down/mouse-up làm WPF nuốt click
  (người dùng phải bấm 2-3 lần). Phải giữ instance theo `Stt`, chỉ update property/move/insert/remove khi cần.

### Profit feed cho quyết định TP (logic-critical, KHÔNG throttle theo UI)
- `slot.LastProfitSnapshot` nuôi quyết định TP + priority-close PHẢI được refresh **mỗi tick**,
  đồng bộ với `metrics` mà `ProcessSnapshot` dùng. `DashboardViewModel.RefreshSlotProfitsEveryTick`
  chạy ngay trước `ProcessSnapshot` trong `OnSnapshotReceived` lo việc này.
- **KHÔNG** kẹp update profit vào nhánh `canRenderUi` (`RefreshTradeRowsFromSnapshot`, throttle
  `SnapshotUiRenderMinIntervalMs=200`) — đó chỉ để vẽ panel. Nếu kẹp → profit ôi tới ~200ms, lệch
  tick so với gap-snapshot, `_tpState.Profits` bị nhồi-lặp giá trị cũ và có thể latch spike ảo
  (lời lúc tín hiệu hoá lỗ lúc khớp). `RefreshTradeRowsFromSnapshot` vẫn gọi `UpdateProfit` (dư
  thừa, vô hại) — đừng coi nó là nguồn profit cho logic.

### Cooldown
- Cooldown kick tại **DISPATCH time** (lúc tool gửi request — qua `AllocatePendingOpenSlot` / `MarkSlotCloseTriggered`), KHÔNG phải confirm time (Phase 8). MAX semantics: lock chỉ extend, confirm không reset.
- App restart luôn kích cooldown mới (`coordinator.RecoverSlotsFromPersisted` kicks startup cooldown).
- Manual buttons legacy hidden (Phase 6 `IsManualTradeButtonsVisible=false`). Manual per-pair là path riêng,
  bypass auto cooldown nhưng bắt buộc qua non-auto barrier và physical close mutex.

### Config & recovery
- Config load theo `MachineHostName` (lowercase, normalize).
- `SyncPortfolioCoordinatorConfig` push từ `RuntimeConfigState` → coordinator. Gọi mỗi `ApplyRuntimeConfig`.
- Recovery slots verify với MMF → slot không match → discard (orphan handling chưa wire, Phase 5 deferred).
- **Coordinator under-count drift → invariant watchdog pause vĩnh viễn**: `BeginWaitAfterClose`
  (adapter) gỡ slot `PendingCloseSlots.First() ?? LiveSlots.First()` — KHÔNG khớp PairId pair vừa đóng;
  với ≥2 pair mở có thể gỡ nhầm slot còn sống → `coordinatorActiveCount < toolRows` → `toolOverTrackingViolation`
  latch mãi (không bao giờ đủ 10 clean polls). Watchdog có **self-heal**: khi drift mà rebuild từ MMF ≤ cap,
  tự resync để nhả (xem Rule E exception #5). Log `[WATCHDOG][WARN]` đã in chi tiết `cond/coordSlots/toolTickets/rebuild`
  để debug khi self-heal không nhả (vd vi phạm thật quotaSide).
- DB field `current_slots` là JSON list (Phase 5).

### Closing wrong trade (Phase 4)
- `SelectCloseCandidateForTicket(slot.Ticket)` resolve RowIndex bằng ticket lookup trong MMF.
  **KHÔNG fallback row 0** nếu không tìm thấy → skip + log warn.
- `AutoCloseOrderAsync(trigger, targetSlot)` nhận slot từ `DispatchCloseTriggerAsync`.
- Legacy `SelectCloseCandidateForExchange` (`[Obsolete]`) chỉ cho manual close + recovery fallback.

### Adapter wrap (Phase 0)
- DI: `ITradingFlowEngine` → `PortfolioCoordinatorAdapter` (NOT `TradingFlowEngine`).
- Adapter có auto-allocate synthetic slot trong `ProcessSnapshot` cho legacy test paths.
- Phase 1+ ViewModel gọi `_portfolioCoordinator.ProcessSnapshot` direct, bypass adapter.
- Adapter scalar (`CurrentPhase`, `CurrentOpenMode`, etc.) vẫn được ViewModel UI binding.

---

## 6. Testing requirements

### Unit tests (mandatory)
- Mỗi service mới trong `Application/Services/` phải có test class tương ứng.
- Coverage tối thiểu: happy path + 2 edge cases mỗi method public.

### Test categories (current)
- `Portfolio/PositionSlotTests` — lifecycle transitions
- `Portfolio/PortfolioStateTests` — collection counters
- `Portfolio/PortfolioCoordinatorTests` — slot lifecycle methods
- `Portfolio/PortfolioCoordinatorAdapterTests` — TradingFlowEngine parity (17 tests)
- `Portfolio/QuotaRuleTests` (Rule A)
- `Portfolio/CooldownRuleTests` (Rule B)
- `Portfolio/OppositeSideLockTests` (Rule C)
- `Portfolio/PriorityCloseRuleTests` (Rule D với scripted close engine factory)
- `Portfolio/MultiSlotIntegrationTests` — cross-rule scenarios
- `Portfolio/RaceProtectionTests` — concurrent allocations
- `Portfolio/PortfolioCoordinatorRecoveryTests` — restore from persisted
- `Portfolio/SlotPersistenceTests` — JSON round-trip
- `Portfolio/StabilityStressTests` — 100+ cycles
- `Portfolio/MetricsTests` — counters

### Run tests
```bash
DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
```
Pre-existing baseline: 19 fails (9 TradingFlowEngineTests + 4 CloseSignalEngineTests + 6 adapter mirrors).
New code MUST NOT introduce new failures.

### Smoke test trước khi merge
- Build clean, no warnings.
- `dotnet test` không tăng baseline failures.
- Chạy app local (Windows): open 2 lệnh, đợi close, verify log không có ERROR/WARN bất thường.

---

## 7. Khi phát triển feature mới

### Checklist trước khi code
1. Feature có vi phạm 4 business rules A/B/C/D không?
2. Có cần thêm config DB không? Document trong README section 7.1.
3. Có touch `DashboardViewModel.cs` không? File ~5800 lines, cẩn thận merge conflict.
4. Có ảnh hưởng recovery flow không? Test bằng restart app giữa cycle.
5. Có cross-phase contract change không? (e.g., `IPortfolioCoordinator` signature)

### Checklist sau khi code
1. README section 5/7/10/13 có cần update không?
2. CLAUDE.md có cần thêm pitfall mới không?
3. Test cover đủ chưa? (baseline ≤ 19 fails)
4. Log message rõ ràng (category, level, context)?

### Commit conventions
- Phase 0-7 refactor đã commit. New work commit message format:
  `<phase>: <summary>` hoặc plain summary nếu không thuộc phase.

---

## 8. What NOT to do

- ❌ Đừng bypass cooldown vì "user click tay" — manual buttons hidden (Phase 6).
- ❌ Đừng share `CloseSignalEngine` giữa slots — Rule D requires isolated window state.
- ❌ Đừng dùng scalar state (`CurrentOpenMode`, `CurrentPositionSide`) ở code mới — dùng `PositionSlot`.
- ❌ Đừng đếm quota chỉ theo Live — phải bao gồm `PendingOpen + PendingClose`.
- ❌ Đừng khôi phục global post-action lock cũ. Ma trận Auto phải qua transition gate; per-slot post-open lock dùng Open confirm của chính slot.
- ❌ Đừng modify `TradingFlowEngine.cs` cũ — `[Obsolete]`, dùng `PortfolioCoordinator`.
- ❌ Đừng hardcode profit threshold cho Rule D — Rule D pick max, không filter.
- ❌ Đừng remove logs `[CYCLE]` `[SLOT]` `[CLOSE_SELECT]` — cần cho debug production.
- ❌ Đừng fallback close to row 0 nếu ticket missing trong MMF — skip + log warn.
- ❌ Đừng change `RuntimeConfigState` defaults từ 1 → 7 cho cap → phải qua DB config (Phase 5).
- ❌ Đừng thêm bất kỳ path mở/đóng vị thế nào bỏ qua signal engine (Rule E) — trừ các recovery/integrity exception đã liệt kê ở Section 2.

---

## 9. Key external dependencies

- **Supabase** (Postgres): config + persistence (Phase 5 deferred).
- **Shared memory (MMF)**: tick prices + open trades + history.
- **MT4/MT5 native click**: via `NativeMethodsMt4/Mt5.cs` (P/Invoke, Windows-only).
- **Telegram notifier**: critical event alerts.
- **xUnit**: test framework, `[Fact]` + `[Theory]`.

---

## 10. When in doubt

1. Đọc `README.md` cho business logic + multi-slot architecture (Section 13).
2. Đọc `CLAUDE.md` (file này) cho conventions + pitfalls.
3. Đọc test class tương ứng — test là spec.
4. Search `[CYCLE][INFO]` / `[SLOT][INFO]` trong logs để track flow runtime.
5. Hỏi user trước khi modify business rules — không tự ý thay đổi quota / cooldown / opposite-lock / priority close.
6. Hỏi user khi cần thay đổi RuntimeConfigState defaults — production behavior phụ thuộc.
