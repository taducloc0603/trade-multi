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

### Rule B — Global cooldown
- Sau mỗi OPEN/CLOSE confirm, random `Uniform(GlobalCooldownMin, Max)` seconds.
- Cooldown bắt đầu từ **MMF confirm time**, không phải tool click time.
- Khoá toàn hệ thống — không slot nào được open/close.
- Implementation: `coordinator.CanCloseNow(out reason)` + check trong ProcessSnapshot top.

### Rule C — Opposite-side OPEN lock 5 phút
- **Hardcode 300 giây** (`PortfolioCoordinator.OppositeSideLockSeconds`).
- CHỈ block OPEN opposite-side. Same-side OPEN refresh timer.
- KHÔNG block CLOSE.
- State: `coordinator.LastOpenConfirmedAtUtc`, `LastOpenConfirmedSide`.

### Rule D — Priority close theo profit cao nhất
- Nhiều slot trigger close cùng tick → chỉ close 1 slot có `LastProfitSnapshot` cao nhất.
- Slot losers giữ nguyên close window — sẽ trigger lại tick sau nếu vẫn đủ điều kiện.
- Implementation: `coordinator.ProcessSnapshot` close path: `OrderByDescending(LastProfitSnapshot ?? double.MinValue)`.

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

### Cooldown
- Cooldown đo từ **MMF confirm**, không phải tool click.
- App restart luôn kích cooldown mới (`coordinator.RecoverSlotsFromPersisted` kicks startup cooldown).
- Manual buttons hidden (Phase 6 `IsManualTradeButtonsVisible=false`) — không có path bypass cooldown.

### Config & recovery
- Config load theo `MachineHostName` (lowercase, normalize).
- `SyncPortfolioCoordinatorConfig` push từ `RuntimeConfigState` → coordinator. Gọi mỗi `ApplyRuntimeConfig`.
- Recovery slots verify với MMF → slot không match → discard (orphan handling chưa wire, Phase 5 deferred).
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
- ❌ Đừng kích cooldown từ tool click — phải đợi MMF confirm.
- ❌ Đừng modify `TradingFlowEngine.cs` cũ — `[Obsolete]`, dùng `PortfolioCoordinator`.
- ❌ Đừng hardcode profit threshold cho Rule D — Rule D pick max, không filter.
- ❌ Đừng remove logs `[CYCLE]` `[SLOT]` `[CLOSE_SELECT]` — cần cho debug production.
- ❌ Đừng fallback close to row 0 nếu ticket missing trong MMF — skip + log warn.
- ❌ Đừng change `RuntimeConfigState` defaults từ 1 → 7 cho cap → phải qua DB config (Phase 5).

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
