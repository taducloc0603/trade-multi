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

**Đặc thù multi-slot:** mỗi pair là một `PositionSlot` độc lập với `CloseSignalEngine`
riêng. `max_total_opens` là trần tổng cố định từ DB; `max_buy_opens` và
`max_sell_opens` là cận trên cho quota Buy/Sell random theo chu kỳ. Runtime fallback
hiện tại là tổng `5`, cận trên Buy `3`, cận trên Sell `3`. `PortfolioState` khởi tạo
`1/1/1` chỉ là trạng thái nội bộ trước lần `SyncPortfolioCoordinatorConfig` đầu tiên.

Đọc `README.md` cho chi tiết signal logic, gap formula, state machine.
Đọc `README.md` Section 13 cho multi-slot architecture.

---

## 2. Critical business rules (DO NOT violate)

### Rule A — Random quota Open
- Trần tổng cấu hình động nhưng không random; fallback runtime: tổng 5, cận trên Buy 3, cận trên Sell 3.
- Đếm bao gồm `PendingOpen + Live + PendingClose`.
- `max_total_opens` là giới hạn tổng trực tiếp. Mỗi chu kỳ chọn `effectiveBuy=random(1..max_buy_opens)`, `effectiveSell=random(1..max_sell_opens)` và `X=random(2..5)` (inclusive).
- Chỉ pair Open confirmed đủ hai chân A/B tăng tiến độ đúng một lần. Signal/dispatch fail, partial Open rollback, Close, recovery và slot restore không tăng tiến độ. Đủ X thì tạo chu kỳ mới.
- Quota mới thấp hơn số slot hiện tại chỉ chặn Open mới; tuyệt đối không force-close. Close không bị quota Open chặn.
- Persist `previousBuy/Sell`, `effectiveBuy/Sell`, X, progress và cycle cùng `current_slots`; restart restore chu kỳ cũ. Reload config không reroll, chỉ clamp quota chiều nếu cận trên giảm; Total mới áp dụng ngay.
- Trading Signal hiển thị Old/Current/Total/Cycle/Progress/Active; `OVER TARGET` chỉ là trạng thái quan sát và chặn Open mới, không force-close.
- Implementation: `coordinator.CanOpenNewSlot(side, out reason)`.

### Rule B — Auto cooldown + non-auto close barrier
- Auto dùng transition gate theo action/side trước và action/side kế tiếp; không dùng một global post-action timer.
- Open cùng chiều→Open cùng chiều và Close→Close: random theo `rd_start_same_action_lock_seconds..rd_end_same_action_lock_seconds`, sinh đúng một lần tại dispatch; invalid fallback 3..10.
- Close→Open: random một lần trong `rd_start_post_close_lock_seconds..rd_end_post_close_lock_seconds`, lưu theo slot Auto Close. Open ngược chiều: `opposite_side_lock_seconds` + Open point policy.
- Open→Close theo phương án B: từng slot random một lần khi Open confirmed trong `rd_start_post_open_lock_seconds..rd_end_post_open_lock_seconds`, rồi chỉ eligible khi hết deadline riêng.
- Auto Open đảo chiều còn phải qua `opposite_open_min_distance_pts`: Buy→Sell dùng `(A.Bid-AvgBuyOpenA)*Point`; Sell→Buy dùng `(AvgSellOpenA-A.Ask)*Point`. Chỉ tính ticket A của slot Auto Live/PendingClose, re-check trong router mutex; Manual/Recovery không áp dụng.
  Open slot mới không refresh thời gian Close của slot cũ.
- `GlobalActionLockUntilUtc` chỉ dành cho startup/recovery cooldown toàn cục, không dùng cho ma trận thường.
- Manual/recovery KHÔNG đọc/ghi Auto transition state và dùng non-auto close barrier.
- Khi manual/recovery đang xử lý, `HasNonAutoCloseInFlight=true`: chặn auto dispatch và disable toàn bộ
  nút Close đến khi MMF xác nhận pair cân bằng/flat. Barrier không có thời gian chờ thêm sau confirm.
- **Semantics khi nhiều guard cùng áp dụng:** tất cả điều kiện phải pass (AND), action được phép tại
  deadline muộn nhất; KHÔNG cộng số giây của các lock. Ví dụ Close→Close còn phải chờ post-open riêng
  của slot sắp đóng; Open đảo chiều phải vừa hết opposite-side lock vừa pass price guard.
- Post-open chỉ chặn **Auto Close của chính slot**, không chặn Open slot khác. Nó được pre-check trong
  close scan và re-check trong router/transition gate bằng cùng deadline
  `slot.OpenConfirmedAtUtc + slot.SelectedPostOpenLockSeconds`; đây là defense-in-depth chống race,
  không phải nhiều timer nối tiếp. Manual/Recovery/partial-open rollback bypass Auto post-open timer.

### Rule C — Opposite-side OPEN lock + Post-close range (2 nhóm ĐỘC LẬP)
- **Hai nhóm config độc lập** (`<= 0` → giữ fallback an toàn):
  - `opposite_side_lock_seconds` → `RuntimeConfigState.CurrentOppositeSideLockSeconds` → `coordinator.UpdateOppositeSideLockConfig` (`DefaultOppositeSideLockSeconds`).
  - `rd_start_post_close_lock_seconds` / `rd_end_post_close_lock_seconds` → `RuntimeConfigState` → `coordinator.UpdatePostCloseLockConfig(start, end)` (`DefaultPostCloseLockSeconds` cho từng đầu không hợp lệ).
  - Cả hai nhóm được push trong `SyncPortfolioCoordinatorConfig`.
- **Lock 1 (sau OPEN)** — dùng `OppositeSideLockSeconds`: sau OPEN confirm → CHỈ block OPEN opposite-side. Same-side OPEN refresh timer. KHÔNG block CLOSE. State: `LastOpenConfirmedAtUtc`, `LastOpenConfirmedSide`.
- **Lock 2 (post-close auto)** — random tại Auto Close dispatch và lưu theo slot; sau MMF confirm, cùng duration được neo lại tại `CloseConfirmedAtUtc` để block Auto Open/re-entry;
  Auto Close tiếp theo chỉ chờ random theo same-action range DB.
  Manual/recovery close không set `LastCloseConfirmedAtUtc` và không tạo/gia hạn auto cooldown.
- Close→Open hiện có hai lần bảo vệ dùng cùng duration: transition gate tính từ Auto Close dispatch và
  `CanOpenNewSlot` tính lại từ Close confirm. Hai khoảng không cộng nhau; deadline từ confirm thường muộn
  hơn và quyết định thời điểm Auto Open thực tế.
- Cả hai check nằm trong `CanOpenNewSlot` (chỉ chặn, không tạo open/close → không vi phạm Rule E). Block reason: `OPPOSITE_SIDE_LOCK` / `POST_CLOSE_LOCK`.

### Rule D — Priority close theo profit cao nhất
- Nhiều slot trigger close cùng tick → chỉ close 1 slot. Nếu có overtime slot
  (`age > max_life_time_by_second`), chọn slot già nhất rồi profit cao nhất làm tie-break;
  nếu không có overtime, chọn `LastProfitSnapshot` cao nhất.
- Slot losers giữ nguyên close window — sẽ trigger lại tick sau nếu vẫn đủ điều kiện.
- Implementation: `coordinator.ProcessSnapshot` close path: `OrderByDescending(LastProfitSnapshot ?? double.MinValue)`.
- `min_profit_to_close > 0` lọc Auto Close trước khi vào `eligibleCloses`: khi
  `age < max_life_time_by_second` cần tổng profit hai chân đạt ngưỡng; tại `age >=` thì bỏ qua gate.
  Nếu max lifetime bằng `0`, gate không hết hạn. Manual/recovery không đi qua gate này.

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

### Rule G — HWND profile phải giữ nguyên theo slot

- Mỗi dòng `manualHwndColumns` là một profile nguyên tử gồm `ChartHwndA`, `TradeHwndA`,
  `ChartHwndB`, `TradeHwndB`.
- Random một lần khi bắt đầu Open; lưu bản sao profile vào `PositionSlot` và mapping `pairId`.
- Open dùng `slot.ChartHwndA/B`; Close/retry/rollback/recovery dùng `slot.TradeHwndA/B` hoặc bản sao
  pending cùng nguồn. Không đọc `CurrentTradeHwndA/B` cho slot đã có profile.
- Invariant: `cN -> tN`; cấm ghép chéo `cN -> tM` khi `N != M`.
- Profile của slot đang chạy là immutable trước thay đổi config và được persist trong `current_slots`.
- Snapshot legacy không có profile được phép fallback về config hiện tại để tương thích ngược; không
  được mô tả fallback đó như một mapping lịch sử chính xác.

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
- Mỗi phiên Start mở **4 file** cùng prefix `{yyyyMMdd_HHmmss}`: `-trade-log`,
  `-gap-stability-raw`, `-signal-outcome`, `-gap-tick`. Xem README §11.2.
- `-gap-tick.log` là ngoại lệ format: timestamp chỉ `HH:mm:ss.fff` (ngày ở header), là thời điểm
  TICK chứ không phải lúc ghi, và **không bị `LOG_LEVEL` lọc**. Vì vậy `LogGapTickRaw` nhận
  nguyên văn cả dòng và KHÔNG đi qua `LogCore` — đừng nối lại vào `LogCore`.
- Thêm channel mới vào `TradeSessionFileLogger` phải: (1) mở file trong `try/catch` RIÊNG — lỗi
  mở file của một kênh phụ mà rơi vào `catch` lớn của `StartSession` sẽ làm `_writeQueue` không
  được tạo và **cả phiên mất log**; (2) nếu kênh ghi tần suất cao thì đếm drop bằng counter riêng,
  báo trong `[LOGGER][HEALTH]`, không dùng `_droppedLogCount` (nó sinh WARN ra main log + panel
  Signal); (3) không được chạm `pendingGapRaw` trong `DrainQueue` — sẽ thu hẹp cửa sổ gộp
  gap-stability đang có.

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
- `SignalCycleStatuses` cũng theo quy tắc trên: `SyncSignalCycleStatuses` đồng bộ theo khóa
  `(Kind, SlotId)`, chỉ Remove/Insert/Move/thay ô đã đổi. Panel có throttle RIÊNG
  `SignalCycleStatusRenderMinIntervalMs=500` (chậm hơn nhịp render 200ms) vì số dòng nhân theo
  số slot và `LastValue` đổi gần như mỗi tick.

### UI thread budget khi nhiều slot (đã gặp treo ở ~9 lệnh)
- `OnSnapshotReceived` bọc TOÀN BỘ thân hàm trong `Dispatcher.Invoke` → mọi signal engine, guard và
  log chạy trên UI thread mỗi 50ms, chi phí **tỉ lệ tuyến tính với số slot**. Đây là trần khả năng
  mở rộng hiện tại; muốn vượt phải đưa logic sang một context nền ĐƠN LUỒNG (giữ tuần tự) và
  chuyển luôn phần `Dispatcher.Invoke` trong `RunOrderInfoPollingAsync` sang cùng context đó,
  nếu không sẽ đẻ race trên state slot.
- Log lặp mỗi tick PHẢI đi qua `ISlotLogger.LogVerbose` (→ `LogFileOnly`), không dùng `Log`:
  mỗi dòng realtime tốn thêm `SystemLogItem.Parse` + một chặng Dispatcher, nhân theo số slot.
  Hiện áp dụng cho `[*_CYCLE][PROGRESS]` và `[TP_CYCLE][PROGRESS]`. Các event chuyển trạng thái
  (STARTED/RESET/COMPLETED/TRIGGERED) tần suất thấp nên vẫn giữ realtime.
- `SignalLogItems` có ~80 chỗ gọi thẳng `Insert(0, ...)` trên UI thread. KHÔNG chuyển riêng lẻ
  một đường nào sang `BeginInvoke` — sẽ xáo thứ tự hiển thị của panel Signal. Muốn tối ưu thì
  gom nhiều dòng vào MỘT lần `Invoke`, hoặc chuyển toàn bộ sang cùng một hàng đợi. Lưu ý caller
  chính đã ở trên UI thread nên `Dispatcher.Invoke` chạy inline (`CheckAccess`), gần như miễn phí —
  đây KHÔNG phải nguồn gây treo.
- **KHÔNG gọi `ITradeSessionFileLogger.IsSessionActive` trên đường chạy mỗi tick.** Property đó
  lấy `lock(_sync)`, mà drain thread của logger giữ đúng lock ấy quanh MỖI lần ghi file
  (`AutoFlush=true` → một flush syscall từng dòng). Gọi mỗi tick sẽ kéo UI thread vào tranh chấp
  lock của logger, nặng nhất đúng lúc log dồn dập. Đường log bình thường (`LogCore` → `TryAdd`)
  không khoá — giữ nguyên như vậy. Muốn gate theo phiên trong `OnSnapshotReceived` thì dùng
  `IsTradingLogicEnabled` (field read thuần); các `Log*Raw` đã tự no-op khi `_writeQueue` null.

### Profit feed cho quyết định TP (logic-critical, KHÔNG throttle theo UI)
- `slot.LastProfitSnapshot` nuôi quyết định TP + priority-close PHẢI được refresh **mỗi tick**,
  đồng bộ với `metrics` mà `ProcessSnapshot` dùng. `DashboardViewModel.RefreshSlotProfitsEveryTick`
  chạy ngay trước `ProcessSnapshot` trong `OnSnapshotReceived` lo việc này.
- **KHÔNG** kẹp update profit vào nhánh `canRenderUi` (`RefreshTradeRowsFromSnapshot`, throttle
  `SnapshotUiRenderMinIntervalMs=200`) — đó chỉ để vẽ panel. Nếu kẹp → profit ôi tới ~200ms, lệch
  tick so với gap-snapshot, `_tpState.Profits` bị nhồi-lặp giá trị cũ và có thể latch spike ảo
  (lời lúc tín hiệu hoá lỗ lúc khớp). `RefreshTradeRowsFromSnapshot` vẫn gọi `UpdateProfit` (dư
  thừa, vô hại) — đừng coi nó là nguồn profit cho logic.

### Signal cycle & price-freeze (NHÁNH TIME)
- **Nhánh này chạy confirmation mode `TIME_AND_MIN_SAMPLES`**, không phải FIXED_SIZE. Một Cycle chỉ
  được công nhận Stable khi đạt **CẢ HAI**: `>= MinStableSamples` mẫu **VÀ** `>= *_hold_confirm_ms`
  thời gian; sau đó vẫn phải qua Dispersion/Drift. Đường code là `GapCycleState.Process`.
  `ProcessFixedSize` / `FixedSizeSignalCycle` còn trong source nhưng KHÔNG có caller production.
- Áp dụng cho **Open, Normal Close, SOS Close và TP**. Mỗi loại giữ cycle/state RIÊNG
  (`_closeByGap*Cycle` vs `_sosCloseByGap*Cycle` vs `_tpState`) — đừng gộp, chuyển Normal ↔ SOS sẽ
  lẫn dữ liệu. Normal Close, SOS Close và TP dùng CHUNG `close_hold_confirm_ms`.
- **Khác biệt then chốt so với FIXED_SIZE:** khi Cycle đã Stable nhưng mẫu cuối chưa đạt
  `open_pts` / `close_pts`, engine trả `null` mà **KHÔNG reset** — Cycle tiếp tục thu mẫu và có thể
  trigger ở mẫu kế tiếp. Đừng thêm `state.Reset(...)` ở nhánh đó.
- Chế độ TIME **không có khử trùng lặp theo fingerprint**: mỗi snapshot hợp lệ là một mẫu.
- `open_hold_confirm_ms`, `close_hold_confirm_ms`, `open_max_times_tick`, `close_max_times_tick`
  đã được **nối lại đầy đủ** vào `ConfigRow` → `ConfigRecord` → `ConfigLoadResult` →
  `RuntimeConfigState` → `GapSignalConfirmationConfig` và log `[DB]`.
  **KHÔNG chạy `docs/DROP-DEPRECATED-SIGNAL-COLUMNS.sql` trên nhánh này.**
- `signal_cycle_size` vẫn được load, validate (`>= 1`) và ghi kèm log/`-signal-outcome.log` để đối
  chiếu với nhánh TICK, nhưng **không tham gia quyết định signal**. Đừng nối nó lại vào engine.
- `*_max_times_tick > 0` chặn Cycle dài quá giới hạn: kiểm tra SAU khi Cycle đã Stable và mẫu cuối
  đã đạt ngưỡng, vượt thì `state.Reset(...)`. `0` = tắt.
- Price-freeze **vẫn hoàn toàn độc lập với hold-time**: `open_price_freeze_ms` /
  `close_price_freeze_ms` chỉ dùng giá trị của chính nó, `0` = tắt. Fallback cũ
  (`price_freeze <- hold_confirm`) đã bị bỏ và KHÔNG được khôi phục — `PriceFreezeConfigMappingTests`
  khoá điều này.
- **4 cột ngưỡng gap thường giữ nguyên DẤU.** `open_pts`, `confirm_gap_pts`, `close_pts`,
  `close_confirm_gap_pts` không còn bị `Math.Abs` ở bất kỳ tầng nào (`ConfigService`,
  `RuntimeConfigState`, `GapSignalConfirmationEngine`, `CloseSignalEngine`,
  `TradeExecutionRouter`, `SosCloseConfigResolver.ValidateLatestGap`) — ĐỪNG nối lại `Abs`.
  Quy ước: nhánh GapBuy `gap >= threshold`, nhánh GapSell `gap <= -threshold`. Ngưỡng dương giữ
  nguyên hành vi cũ; ngưỡng ÂM nới về phía trong đúng như SOS. Engine và router phải sửa cùng
  nhịp — sửa engine mà quên router sẽ bị chặn sạch bằng `LATEST_*_CONDITION_INVALID`.
- Ngưỡng Open âm cho phép `OpenByGapBuy` và `OpenByGapSell` **cùng trigger một tick** (~21.8 % số
  tick theo mô phỏng; bất khả thi khi ngưỡng dương vì `GapSell >= GapBuy`). `PortfolioCoordinator`
  chỉ trả về một `OpenTrigger` mỗi snapshot — đừng đổi vòng lặp đó thành gom nhiều trigger.
- `GapStabilityCalculator` đo center/MAD/dispersion/drift trên `Math.Abs(gap)`, nên một cycle chứa
  hai dấu (`-5, +5, -5`) bị chấm là "hoàn toàn ổn định". Guard cùng dấu trong
  `GapCycleState.ProcessFixedSize`/`Process` là thứ chặn việc đó — **đừng gỡ**. Guard là no-op với
  mọi config `>= 0` vì confirm gate dương đã ép cycle về một dấu.
- **`ValidateLatestSosGap` lỏng hơn engine SOS — CỐ Ý giữ, không phải bug mới.** Validator này
  (`SosCloseConfigResolver.cs:58-90`) chỉ xét `sos_close_gap_pts`, bỏ qua
  `sos_close_confirm_gap_pts`; trong khi engine đòi mẫu cuối qua CẢ HAI. Khi
  `|sos_confirm| < |sos_close|` router cho qua ở 1 330 tổ hợp mà engine sẽ chặn (đo trên
  `|C|,|K| ∈ [1,20] × gap ∈ [-30,30]`). Khác biệt này có TRƯỚC khi 4 cột gap thường được cho phép
  mang dấu. Muốn siết về `min(|confirm|, |close|)` thì phải đánh giá lại toàn bộ SOS dispatch trước
  — nó sẽ chặn bớt SOS close đang qua được.
- **Mỗi snapshot chỉ trả về đúng một `OpenTrigger`.** `PortfolioCoordinator.ProcessSnapshot` return
  ở trigger đầu tiên được phép, mà engine tính nhánh Buy trước — nên khi ngưỡng Open âm làm cả hai
  chiều cùng trigger thì Buy luôn thắng. Trigger bị bỏ được ghi vào `BlockedSignals` với reason
  `DUAL_SIDE_TRIGGER_DROPPED` (chỉ logging, `RecordDroppedOpenTriggers`) — đừng biến nó thành đầu
  vào quyết định, và đừng đổi vòng lặp thành gom nhiều trigger.
- `open_price_freeze_ms` / `close_price_freeze_ms` là hai cột ĐỘC LẬP, mỗi cột chỉ dùng giá trị
  của chính nó. KHÔNG khôi phục fallback về hold-time. `0` = tắt, số âm normalize về `0`.
  Tham số guard tên là `priceFreezeMs` (`SignalEntryGuard.Check`), không phải `holdConfirmMs`.
- Log vòng đời: `[OPEN_CYCLE]` / `[NORMAL_CLOSE_CYCLE]` / `[SOS_CLOSE_CYCLE]` / `[TP_CYCLE]` với
  event `STARTED|PROGRESS|RESET|COMPLETED|TRIGGERED`. Tên nhóm sinh từ `action` trong
  `GapCycleDiagnostics` — đổi chuỗi action sẽ đổi tên log, cẩn thận khi refactor.
  Nhóm log này chạy ở CẢ hai mode (`GapCycleDiagnostics.LogCycleLifecycleTransition`) để hai nhánh
  TIME/TICK so sánh được. Ở mode TIME dòng log mang thêm `hold={durationMs}/{holdConfirmMs}ms` và
  mẫu số của `count=` là `MinStableSamples`. **Khoá đối chiếu giữa hai nhánh là
  `confirmation_mode=`** (`TIME_AND_MIN_SAMPLES` vs `FIXED_SIZE`), có trong `[*_CYCLE]`,
  `[GAP_STABILITY]` và `-signal-outcome.log`.
- `[*_CYCLE][PROGRESS]` và `[TP_CYCLE][COMPLETED]` với `result=TARGET_NOT_REACHED` /
  `CLOSE_MAX_TP_EXCEEDED` / `CLOSE_MAX_TIMES_TICK_EXCEEDED` lặp MỖI TICK cho MỖI slot →
  bắt buộc đi qua `LogVerbose`, không dùng `Log`.

### Cooldown
- Auto action thường dùng transition matrix, không dùng global post-action cooldown. Same-action và
  post-close duration được chọn tại dispatch; per-slot post-open được chọn tại Open confirm.
- `GlobalActionLockUntilUtc` chỉ còn dành cho startup/recovery cooldown. Recovery chỉ tạo timer nếu
  legacy `GlobalCooldownMinSec/MaxSec` lớn hơn 0; runtime hiện sync hai giá trị này về 0.
- Ngoại lệ SOS: trong global startup/recovery cooldown, chỉ slot đang thỏa SOS tại cùng snapshot và có
  close signal hợp lệ mới được bypass global timer. Holding, post-open, Min Profit, transition gate và
  non-auto barrier vẫn áp dụng. Dispatch này phải ghi nhận như Auto Close mới, random lại same-action và
  post-close theo DB; SOS không được bypass cooldown transition vừa tạo bởi chính close trước đó.
- Manual buttons legacy hidden (Phase 6 `IsManualTradeButtonsVisible=false`). Manual per-pair là path riêng,
  bypass auto cooldown nhưng bắt buộc qua non-auto barrier và physical close mutex.

### Config & recovery
- Config load theo `MachineHostName` (lowercase, normalize).
- `SyncPortfolioCoordinatorConfig` push từ `RuntimeConfigState` → coordinator. Gọi mỗi `ApplyRuntimeConfig`.
- `current_slots` đã được load/save. Recovery verify từng ticket với MMF, discard snapshot stale và persist
  lại danh sách hợp lệ; fallback legacy `current_tick_a/current_tick_b` vẫn tồn tại.
- Coordinator under-count drift được watchdog self-heal bằng rebuild từ MMF khi rebuild nằm trong quota
  và không có open/close in-flight; over-count thật vẫn pause. Self-heal throttle 30 giây.
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
3. Test cover đủ chưa? Audit 2026-08-07 ghi nhận 13 legacy/adapter failures do Gap Close chọn sai gap list; xem `docs/audits/SYSTEM-AUDIT-2026-08-07.md`.
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
- ❌ Đừng hardcode quota; giữ fallback runtime `5/3/3` và để DB config quyết định production cap.
- ❌ Đừng thêm bất kỳ path mở/đóng vị thế nào bỏ qua signal engine (Rule E) — trừ các recovery/integrity exception đã liệt kê ở Section 2.

---

## 9. Key external dependencies

- **Supabase** (Postgres): config + persistence `current_slots` và legacy current tickets.
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
