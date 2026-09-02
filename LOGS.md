# LOGS.md — TradeDesktop Log Catalog

> Tài liệu này mô tả toàn bộ log categories trong hệ thống TradeDesktop.
> Dùng để debug production, trace lệnh, và hiểu luồng dữ liệu.

---

## Format chung

```
[yyyy-MM-dd HH:mm:ss.fff] [CATEGORY][LEVEL] message
```

| Level | Ý nghĩa |
|-------|---------|
| `DEBUG` | Chẩn đoán chi tiết (bật qua `LOG_LEVEL=Debug`) |
| `INFO`  | Hoạt động bình thường |
| `WARN`  | Bất thường nhưng không crash |
| `ERROR` | Lỗi cần điều tra |

**Output:** `~/Desktop/trade-log/`, rotate mỗi 50 MB  
**Env vars:** `LOG_LEVEL` (Debug/Info/Warn/Error, default Info) · `LOG_MAX_FILE_SIZE_MB` (default 50)

---

## Mục lục

1. [[VM]](#vm--viewmodel-internal)
2. [[CYCLE]](#cycle--trading-cycle-progression)
3. [[SLOT]](#slot--portfolio-slot-lifecycle)
4. [[RECOVERY]](#recovery--session-recovery--persistence)
5. [[WATCHDOG]](#watchdog--invariant-violation-monitor)
6. [[GUARD]](#guard--entry-guard-validation)
7. [[ROUTER]](#router--trade-execution-router)
8. [[TRADE_GATE]](#trade_gate--auto-transition--dispatch-gate)
9. [[MT4]](#mt4--metatrader-4-executor)
10. [[MT5]](#mt5--metatrader-5-executor)
11. [[FLOW]](#flow--trading-flow-state-transitions)
12. [[MARKET]](#market--market-data--latency)
13. [[CLOSE_SELECT]](#close_select--close-slot-selection)
14. [[PERSIST]](#persist--database-slot-persistence)
15. [[DB]](#db--database-config-load)
16. [[CONFIG]](#config--runtime-configuration-update)
17. [[NOTIFY]](#notify--telegram-notifications)
18. [[MMF_TRADES] / [MMF_HISTORY]](#mmf_trades--mmf_history--shared-memory-change-detection)
19. [[MANUAL]](#manual--manual-trade-operations)
20. [[SIGNAL_OUTCOME]](#signal_outcome--gap-sau-signal-file-rieng)
21. [Debug by scenario](#debug-by-scenario)

---

## [VM] — ViewModel Internal

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Bắt lỗi và warning nội bộ của `DashboardViewModel` — những exception bị suppress để không crash UI.

**Giải thích:** Các operation không critical trong ViewModel (update binding, collection change, in-flight guard check) được wrap try/catch và log ở đây thay vì throw.

**Ví dụ:**
```
[VM][WARN]  Suppressed exception at OnSignalLogItemsCollectionChanged: Collection was modified
[VM][WARN]  Suppressed exception at FindRowIndexForTicket: Element not found
[VM][ERROR] Auto trade error: IndexOutOfRangeException at ProcessOpenTrigger
[VM][ERROR] Auto close error: NullReferenceException on _tradingFlowEngine
[VM][ERROR] Error closing remaining leg after external close: Socket connection lost
```

**Case đặc biệt:**
- `[VM][ERROR]` trong auto-open/close loop → cần check stack trace ngay, có thể do race condition trong Dispatcher thread
- `Suppressed exception at FindRowIndexForTicket` nhiều lần liên tiếp → suspect MMF stale (broker latency), thường vô hại nhưng cần monitor

---

## [CYCLE] — Trading Cycle Progression

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Trace toàn bộ vòng đời một lệnh từ pending-open → confirmed → pending-close → resolved.

**Giải thích:** Mỗi bước quan trọng trong cycle (capture pending, confirm leg, resolve pair, timeout) đều có log [CYCLE] để dễ trace khi debug production.

**Ví dụ:**
```
[CYCLE][INFO]  Pending open captured: pairId=EURUSD slot=1 side=Buy mode=GapBuy at 2024-01-15T14:20:30Z
[CYCLE][INFO]  Pending open leg confirmed: pairId=EURUSD exchange=A ticketA=12345 side=Buy
[CYCLE][INFO]  Pending open resolved: pairId=EURUSD ticketA=12345 ticketB=67890
[CYCLE][INFO]  Close cycle resolved: slot=1 closeCompletedAtUtc=2024-01-15T14:25:45Z
[CYCLE][WARN]  Auto open blocked by quota: total=7/7 blockingPairId=GBPUSD
[CYCLE][WARN]  Auto open blocked by unresolved pending cycle: blockingPairId=EURUSD
[CYCLE][ERROR] Pending open timeout: pairId=EURUSD elapsedMs=30000 mode=GapBuy side=Buy
```

**Case đặc biệt:**
- `[CYCLE][ERROR] Pending open timeout` → broker không confirm trong 30s. Slot vẫn ở PendingOpen, cần kiểm tra manually có lệnh mở thật không
- `blocked by unresolved pending cycle` → slot trước chưa xong mà signal mới đến. Bình thường với cap=1
- Thấy `Pending open leg confirmed` chỉ 1 leg rồi im → leg kia bị lỗi click, kiểm tra log [MT4]/[MT5] cùng timestamp

---

## [SLOT] — Portfolio Slot Lifecycle

**Files:** `TradeDesktop.Application/Services/Portfolio/PortfolioCoordinator.cs` · `DashboardViewModel.cs`

**Dùng để làm gì:** Trace chi tiết lifecycle của từng `PositionSlot` — allocation, state transition, quota check, cooldown, close confirm.

**Giải thích:** Đây là log category quan trọng nhất cho multi-slot. Mỗi event trong `PortfolioCoordinator` (allocate, open-confirm, close-confirm, abort) đều log với `slot=N` để distinguish.

**Ví dụ:**
```
[SLOT][SKIP]            Open Buy blocked: QUOTA_TOTAL_FULL (7/7)
[SLOT][OPEN_CONFIRMED]  slot=1 side=Buy ticketA=12345 ticketB=67890 lockUntil=14:30:45UTC
[SLOT][CLOSE_CONFIRMED] slot=1 side=Buy profit=1.50 closeReason=Gap lockNone
[SLOT][WAITING]         Block ALL open/close trong 120s (đến 14:32:15 UTC) — reason=opposite-side lock sau OPEN Buy
[SLOT][WAITING]         Block OPEN Sell trong 300s (đến 14:35:30 UTC) — reason=opposite-side lock sau OPEN Buy
[SLOT][TP_CHECK]        slot=1 profit=1.25 confirm=2.00 tp=2.50 holdMs=5000 maxTick=10
[SLOT][ABORT]           PendingOpen removed: pairId=EURUSD slot=1
[SLOT][RECOVERY]        Restored slot 1: side=Buy mode=GapBuy ticketA=12345 ticketB=67890 openConfirmedAt=2024-01-15T14:20:30Z holding=300s
[SLOT][COOLDOWN][BLOCK] ProcessSnapshot bị skip — cooldown active (remaining 45.3s, until 14:32:15 UTC)
[SLOT][COOLDOWN][CLEAR] ProcessSnapshot resumed — cooldown đã hết
[SLOT][COOLDOWN][SOS_BYPASS] slot=3 pairId=AUTO-0003 side=Buy source=A_OPEN_DISTANCE closeMode=TP remainingSeconds=12.4 cooldownUntil=...
[TRADE_GATE][SOS_RESET] pairId=AUTO-0003 slot=3 side=Buy source=A_OPEN_DISTANCE bypassedGlobalRemainingMs=12400 sameActionSeconds=30 postCloseSeconds=60 dispatchAt=...
[SLOT][COOLDOWN][SKIP]  cooldown=0s (config min=5/max=15) — reason=open-dispatch
[SLOT][COOLDOWN][KEEP]  rolled=8s không extend lock (existing đến 14:30:45 UTC, remaining=45.2s, proposed đến 14:30:43 UTC) — reason=close-dispatch
```

**Case đặc biệt:**
- `[SLOT][COOLDOWN][SOS_BYPASS]` → slot SOS có close signal hợp lệ được đi xuyên global startup/recovery cooldown; các guard close khác vẫn giữ nguyên.
- `[TRADE_GATE][SOS_RESET]` → router đã nhận SOS Close như Auto Close mới và tạo lại same-action/post-close cooldown theo DB.
- `[SLOT][COOLDOWN][KEEP]` → cooldown đang chạy, roll mới ngắn hơn nên giữ nguyên (MAX semantics — Rule B)
- `[SLOT][COOLDOWN][SKIP] cooldown=0s` → config min=max=0, hệ thống không cooldown. Cần review nếu unexpected
- `[SLOT][ABORT]` → slot bị remove khỏi PendingOpen (thường do timeout hoặc close leg confirm trước khi open resolve). Check có lệnh thật trên sàn không
- `[SLOT][WAITING] Block ALL` → opposite-side lock sau OPEN, toàn bộ hệ thống bị block, không chỉ Sell
- Startup luôn kick cooldown mới qua `RecoverSlotsFromPersisted` → `[SLOT][COOLDOWN]` ngay sau khởi động là expected

---

## [RECOVERY] — Session Recovery & Persistence

**Files:** `TradeDesktop.Application/Services/Portfolio/PortfolioCoordinator.cs` · `DashboardViewModel.cs`

**Dùng để làm gì:** Trace quá trình khôi phục slot từ DB sau restart app, và việc save/clear slot data vào DB.

**Giải thích:** Khi app restart, `PortfolioCoordinator` đọc `current_slots` từ DB, verify ticket còn trong MMF shared memory, rồi restore. Mọi bước được log để debug orphan slot.

**Ví dụ:**
```
[RECOVERY][INFO]  Found persisted tickets in DB: ticketA=12345 ticketB=67890. Checking shared memory...
[RECOVERY][INFO]  Previous tickets not found in shared memory (foundA=false foundB=true). Clearing DB.
[RECOVERY][INFO]  Registered coordinator slot: pairId=EURUSD side=Buy mode=GapBuy ticketA=12345 ticketB=67890
[RECOVERY][INFO]  Recovered tickets from previous session: ticketA=12345 ticketB=67890 pairId=EURUSD
[RECOVERY][INFO]  Cleared current ticks/current_slots from DB after close finalize
[RECOVERY][INFO]  Saved current ticks to DB: ticketA=12345 ticketB=67890
[RECOVERY][WARN]  Failed to save current ticks: Database timeout exceeded
[RECOVERY][WARN]  Trade map names not available yet, cannot recover persisted slots.
[RECOVERY][ERROR] Failed to recover persisted slots: Connection to shared memory failed
```

**Case đặc biệt:**
- `foundA=false foundB=true` → 1 leg đã close trên sàn, 1 leg còn mở. Orphan handling chưa wire (Phase 5 deferred) → slot bị discard, cần xử lý manually
- `Trade map names not available yet` → app chưa connect xong MMF khi recovery chạy. Bình thường nếu chỉ xuất hiện 1-2 lần đầu startup
- `[RECOVERY][ERROR]` → MMF không accessible, toàn bộ recovery skip → mất slot state

---

## [WATCHDOG] — Invariant Violation Monitor

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Monitor và tự-heal khi hệ thống phát hiện invariant bị vi phạm (tick stale, dữ liệu không nhất quán).

**Giải thích:** Khi phát hiện anomaly (tick stale quá lâu), system chuyển sang PAUSED. Sau N polls ổn định liên tiếp mới RESUME.

**Ví dụ:**
```
[WATCHDOG][WARN] Invariant violation detected: Stale tick from exchange A. state=PAUSED
[WATCHDOG][INFO] Invariant clear counting started: 1/5
[WATCHDOG][INFO] Invariant cleared after 5 stable polls. state=RESUMED
```

**Case đặc biệt:**
- `state=PAUSED` kéo dài → tick feed bị mất hoàn toàn. Check kết nối MT4/MT5 → shared memory writer
- Counter reset về 0 nếu có 1 poll xấu giữa chừng → cần 5 polls sạch liên tiếp để resume

---

## [GUARD] — Entry Guard Validation

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Log khi `SignalEntryGuard` block một signal vì vượt ngưỡng safety (latency, max gap, spread, price freeze).

**Giải thích:** 4 lớp guard: Latency / MaxGap / Spread / PriceFreeze. Chỉ log khi bị reject.

**Ví dụ:**
```
[GUARD][WARN] Auto trade rejected: trigger=GapConfirmation side=Buy action=Open
              reason="Gap exceeds max allowed" gap=125 confirmLatencyMs=500
              maxGap=100 maxSpread=5
```

**Case đặc biệt:**
- `reason="Gap exceeds max allowed"` → gap quá lớn, thường do spike giá bất thường. Safety filter đúng
- Nhiều `[GUARD][WARN]` liên tiếp → cần review config `maxGap` / `maxSpread` có còn phù hợp không
- Latency guard trigger → network/processing lag, cân nhắc tăng threshold hoặc check infrastructure

---

## [QUOTA_RANDOM] — Quota Open ngẫu nhiên

**File:** `TradeDesktop.Application/Services/Portfolio/PortfolioCoordinator.cs`

**Dùng để làm gì:** Theo dõi quota Buy/Sell hiệu lực, tiến độ số pair Open confirmed và việc khôi phục chu kỳ sau restart. `maxTotal` luôn lấy cố định từ DB.

**Ví dụ:**
```text
[QUOTA_RANDOM][NEW_CYCLE] cycle=1 reason=INITIALIZED description="Khởi tạo chu kỳ quota ngẫu nhiên đầu tiên" previousBuy=n/a previousSell=n/a effectiveBuy=2 effectiveSell=3 maxTotal=5 count=0/4
[QUOTA_RANDOM][PROGRESS] cycle=1 pairId=AUTO-0001 description="Pair đã mở thành công đủ hai chân A/B; tăng tiến độ chu kỳ quota" count=1/4 effectiveBuy=2 effectiveSell=3 maxTotal=5
[QUOTA_RANDOM][NEW_CYCLE] cycle=2 reason=OPEN_THRESHOLD_REACHED description="Đã đủ số pair Open thành công của chu kỳ trước; bắt đầu chu kỳ quota mới" previousBuy=2 previousSell=3 effectiveBuy=1 effectiveSell=2 maxTotal=5 count=0/3
[QUOTA_RANDOM][RESTORED] cycle=8 reason=APP_RESTART description="Khôi phục quota và tiến độ chu kỳ cũ sau khi ứng dụng khởi động lại" previousBuy=3 previousSell=1 effectiveBuy=2 effectiveSell=3 maxTotal=5 count=2/4
```

**Log chặn Open:**
```text
[SLOT][SKIP] Open Buy blocked: QUOTA_BUY_FULL (2/2) description="Số lệnh Buy hiện tại đã đạt quota Buy ngẫu nhiên 2; không mở thêm Buy"
[SLOT][SKIP] Open Sell blocked: QUOTA_SELL_FULL (3/3) description="Số lệnh Sell hiện tại đã đạt quota Sell ngẫu nhiên 3; không mở thêm Sell"
[SLOT][SKIP] Open Buy blocked: QUOTA_TOTAL_FULL (5/5) description="Tổng số lệnh hiện tại đã đạt giới hạn cố định 5; không mở thêm lệnh mới"
```

- `count=n/X`: đã có n pair Open confirmed trong chu kỳ; X chỉ có thể là 2, 3, 4 hoặc 5.
- Log `[SLOT][SKIP]` được throttle: ghi khi side/reason đổi hoặc sau tối thiểu 30 giây.
- Open bị chặn không tăng `count` và không làm reroll. Close không ảnh hưởng chu kỳ.
- UI Trading Signal giữ Old gần nhất và Current; `OVER TARGET` không đóng lệnh cũ.
- `[PERSIST][INFO] Saved current_slots (...)` chứa object `slots` và `randomQuota`; `[PERSIST][WARN]` nghĩa là trạng thái restart chưa được lưu thành công.

---

## [ROUTER] — Trade Execution Router

**File:** `TradeDesktop.App/Services/TradeExecutionRouter.cs`

**Dùng để làm gì:** Trace việc route lệnh open/close pair đến đúng platform (MT4/MT5) và kết quả.

**Giải thích:** `TradeExecutionRouter` nhận pair request, phân tách thành 2 legs (legA=exchange A, legB=exchange B), dispatch lần lượt và log kết quả.

**Ví dụ:**
```
[ROUTER][INFO]  Open pair request:  legA={platform=Mt5,action=Buy,hwnd=0x12AB34CD,delay=100ms} legB={...}
[ROUTER][INFO]  Open pair result:   success=true  legA={ok=true,detail=clicked} legB={ok=true,detail=clicked} elapsed=350ms
[ROUTER][WARN]  Open pair result:   success=false legA={ok=true,detail=clicked} legB={ok=false,detail=click failed} elapsed=220ms
[ROUTER][ERROR] Open pair threw after 1050ms: ObjectDisposedException on WindowHandle
[ROUTER][INFO]  Close pair request: legA={platform=Mt5,action=CLOSE,hwnd=0x12AB34CD,ticket=12345,delay=50ms} legB={...}
```

**Case đặc biệt:**
- `success=false` với 1 leg ok, 1 leg failed → half-open state. Coordinator giữ slot ở PendingOpen chờ timeout
- `ObjectDisposedException on WindowHandle` → cửa sổ MT4/MT5 bị đóng khi router đang click. Cần restart MT4/MT5
- `elapsed` > 1000ms thường xuyên → suspect platform lag

---

## [TRADE_GATE] — Auto Transition & Dispatch Gate

**Files:** `TradeDesktop.Application/Services/Portfolio/PortfolioCoordinator.cs` · `TradeDesktop.App/Services/TradeExecutionRouter.cs`

**Dùng để làm gì:** Cho biết request được phép dispatch hay bị chặn bởi cooldown, transition lock,
non-auto barrier hoặc ngữ cảnh slot. Gate chạy trước MT4/MT5 executor.

**Ví dụ:**
```
[TRADE_GATE][BLOCKED] action=OPEN side=Buy reason=POST_CLOSE_OPEN_LOCK remainingMs=18849
[TRADE_GATE][BLOCKED] action=OPEN side=Buy reason=SAME_SIDE_OPEN_RANDOM_LOCK remainingMs=797
[TRADE_GATE][BLOCKED] action=CLOSE side=Sell reason=OPEN_TO_CLOSE_RANDOM_LOCK remainingMs=7349
[TRADE_GATE][ACQUIRED] action=OPEN side=Buy transitionFrom=SAME_SIDE_OPEN_RANDOM_LOCK nextSameTypeRandomSec=6
[SIGNAL_OPEN_BLOCKED][WARN] reasonCode=POST_CLOSE_OPEN_LOCK remainingMs=18849 description="Chưa thể mở vị thế mới vì đang trong thời gian khóa sau khi đóng"
```

**Case đặc biệt:**
- `POST_CLOSE_OPEN_LOCK` → Auto Open đang chờ timer sau Auto Close; đây là hành vi bình thường.
- `SAME_SIDE_OPEN_RANDOM_LOCK` → hai Auto Open cùng chiều đến quá gần nhau.
- `OPEN_TO_CLOSE_RANDOM_LOCK` → Auto Close đang chờ `rd_same` tính từ Auto Open gần nhất.
- `GLOBAL_ACTION_COOLDOWN` → startup/recovery cooldown toàn cục còn hiệu lực.
- `NON_AUTO_CLOSE_IN_FLIGHT` → Auto tạm dừng trong lúc manual/recovery close chưa được MMF xác nhận xong.
- Request bị gate chặn chưa gọi MT4/MT5 và có `Legs=[]`. Outcome đúng là `*_BLOCKED`, không phải
  `*_FAILED`/`EXECUTION_FAILED`. Chỉ điều tra HWND/native click khi có dòng `[ROUTER] Open pair request`
  và `[MT4]`/`[MT5] Open leg` cùng timestamp.
- `[FLOW][WAIT] BothFlat observed but Close is not finalized` → Trades A/B đã biến mất nhưng
  pending close/history chưa finalize. Đây là race window bình thường; flow cố ý giữ WaitingClose để
  không xóa post-close anchor. Sau đó phải xuất hiện `[SLOT][CLOSE_CONFIRMED]` và
  `[SLOT][WAITING][POST_CLOSE_LOCK]` trước khi Auto Open được xét lại.

---

## [MT4] — MetaTrader 4 Executor

**File:** `TradeDesktop.App/Services/Mt4TradeExecutor.cs`

**Dùng để làm gì:** Log chi tiết từng thao tác native click trên cửa sổ MT4 qua P/Invoke.

**Giải thích:** `Mt4TradeExecutor` dùng `SendMessage` để tương tác với MT4 window handle. Mỗi attempt (open leg, close leg) được log cùng hwnd và kết quả.

**Ví dụ:**
```
[MT4][INFO]  Open leg A action=BUY result=ok
[MT4][WARN]  Open leg A failed: invalid chart hwnd=0x00000000
[MT4][WARN]  Open leg A failed: chart hwnd not valid (0x12AB34CD)
[MT4][WARN]  Open leg A action=BUY result=failed
[MT4][ERROR] Open leg A threw: SendMessage timeout after 5 seconds
[MT4][ERROR] Close leg A action=CLOSE threw: Access denied to chart window
```

**Case đặc biệt:**
- `hwnd=0x00000000` → window handle chưa được set hoặc MT4 chưa mở. Config lại hwnd trong UI
- `hwnd not valid` → hwnd có giá trị nhưng window đã bị destroy (MT4 crash/restart). Cần re-capture hwnd
- `Access denied to chart window` → UAC/privilege issue, MT4 chạy với elevation cao hơn app

---

## [MT5] — MetaTrader 5 Executor

**File:** `TradeDesktop.App/Services/Mt5TradeExecutor.cs`

**Dùng để làm gì:** Tương tự [MT4] nhưng cho MT5. MT5 có thêm logic tìm row theo ticket trước khi click close.

**Giải thích:** MT5 executor cần tìm `rowIndex` trong bảng open trades theo ticket number trước khi click close. Quá trình này có thêm log riêng.

**Ví dụ:**
```
[MT5][INFO]  Open leg A action=BUY result=ok
[MT5][WARN]  Open leg A failed: invalid chart hwnd=0x00000000
[MT5][WARN]  Close leg A ticket=12345 failed: chart hwnd not valid (0x12AB34CD)
[MT5][INFO]  Close leg A ticket=12345 skipped: no open trade
[MT5][WARN]  Close leg A ticket=12345 failed: unresolved rowIndex rowCount=5
[MT5][INFO]  Close leg A ticket=12345 row=3 result=ok
[MT5][ERROR] Close leg A ticket=12345 threw: NullReferenceException on context
```

**Case đặc biệt:**
- `skipped: no open trade` → ticket không còn trong MT5 (đã close manually hoặc stop-out). Bình thường trong external close flow
- `unresolved rowIndex rowCount=5` → ticket có trong bảng nhưng không map được row. Bug nghiêm trọng, cần investigate MMF data
- `NullReferenceException on context` → MT5 context object bị null (platform chưa sẵn sàng). Check MT5 initialization

---

## [FLOW] — Trading Flow State Transitions

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Log state machine transitions của trading session (start, stop, phase change).

**Giải thích:** Trace khi session bắt đầu/kết thúc và phase nào đang active.

**Ví dụ:**
```
[FLOW][INFO]       Session start: phase=WaitingOpen openMode=GapBuy side=Buy
[FLOW][INFO]       Session stop:  phase=WaitingClose openMode=GapBuy side=Buy
[FLOW][TRANSITION] reason=close-aborted-by-guard ...
```

**Case đặc biệt:**
- `Session stop` trong khi `phase=WaitingClose` → user stop app khi đang có lệnh mở. Recovery flow sẽ handle khi restart
- `close-aborted-by-guard` → close signal triggered nhưng guard reject, slot vẫn ở Live, không bị close

---

## [MARKET] — Market Data & Latency

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Monitor chất lượng dữ liệu realtime — tick staleness, latency spike, performance summary.

**Giải thích:** Đo latency từ khi tick được đọc từ MMF đến khi xử lý. Alert khi vượt threshold.

**Ví dụ:**
```
[MARKET][WARN] Latency spike: exchange=A latencyMs=1250.50 thresholdMs=1000.00
[MARKET][WARN] Stale tick detected: exchange=A staleForMs=15000
[MARKET][INFO] Tick resumed: exchange=A staleSec=10
[MARKET][INFO] Perf summary: avgLatencyMs=45.23 peakLatencyMs=500 ...
```

**Case đặc biệt:**
- `Latency spike` liên tục → machine overloaded hoặc MMF writer (EA) bị lag. Không nên trade trong trạng thái này
- `Stale tick` + `Tick resumed` nhanh → brief network hiccup, bình thường
- `Stale tick` kéo dài > 30s không resume → kết nối MT4/MT5 EA bị đứt hoàn toàn. [WATCHDOG] sẽ PAUSE system

---

## [CLOSE_SELECT] — Close Slot Selection

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Log quá trình chọn slot nào sẽ được close khi nhiều slot cùng trigger (Rule D — priority close theo profit cao nhất).

**Giải thích:** Khi `ProcessSnapshot` nhận close trigger từ nhiều slot, chỉ slot có `LastProfitSnapshot` cao nhất được dispatch. Log này trace decision.

**Ví dụ:**
```
[CLOSE_SELECT][INFO]  Slot 1 resolved: pairId=EURUSD profit=1.25 closeReason=Gap
[CLOSE_SELECT][WARN]  Ticket 12345 found at row 3 but not tool-opened — skipping
[CLOSE_SELECT][ERROR] Ticket 12345 NOT found in A MMF (count=0) at row=0
```

**Case đặc biệt:**
- `NOT found in A MMF` → ticket đã close trên sàn nhưng slot state chưa update. Check recovery flow
- `not tool-opened — skipping` → ticket tồn tại trong MMF nhưng không phải lệnh app mở (manual trade). Safety check đúng
- Không thấy log này khi có nhiều slot → signal chưa trigger close, check CloseSignalEngine config

---

## [PERSIST] — Database Slot Persistence

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Log khi save/clear `current_slots` JSON vào DB (để recovery sau restart).

**Giải thích:** Sau mỗi close-confirm, slot state được serialize thành JSON và upsert vào Supabase. Sau close-finalize, record được clear.

**Ví dụ:**
```
[PERSIST][INFO] Saved current_slots (after-close-finalize): {"slots":[...]}
[PERSIST][WARN] Failed to save current_slots (on-close-confirmed): Database connection timeout
```

**Case đặc biệt:**
- `[PERSIST][WARN] Failed to save` → DB không accessible. Nếu app restart ngay sau đó, slot **không** được recover. Critical issue
- Thấy `[PERSIST][INFO]` nhưng không thấy `[RECOVERY][INFO]` tương ứng ở lần restart sau → ticket đã không còn trong MMF khi recovery chạy

---

## [DB] — Database Config Load

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Log khi load runtime config từ Supabase DB theo `MachineHostName`.

**Ví dụ:**
```
[DB] id=1 | hostname=win-vps-01 | point=100 | open_pts=50 | ...
[DB] Không lấy được dữ liệu config từ DB cho hostname win-vps-01
```

**Case đặc biệt:**
- `Không lấy được dữ liệu config` → hostname không match record trong DB (check lowercase normalize), hoặc Supabase offline. App chạy với default config
- Config load sai hostname → tất cả config parameters (gap threshold, lot size…) sai → **không trade** cho đến khi fix

---

## [CONFIG] — Runtime Configuration Update

**File:** `TradeDesktop.App/ViewModels/ConfigViewModel.cs`

**Dùng để làm gì:** Confirm khi runtime config được apply thành công từ `ConfigViewModel`.

**Ví dụ:**
```
[CONFIG][INFO] Runtime config updated: host=win-vps-01 map1=Map_A map2=Map_B
               platformA=Mt5 platformB=Mt5 manualColumns=4
```

**Case đặc biệt:**
- Không thấy log này sau khi user save config → `ApplyRuntimeConfig` không được gọi. Bug trong binding flow

---

## [NOTIFY] — Telegram Notifications

**File:** `TradeDesktop.App/Services/TelegramNotifier.cs`

**Dùng để làm gì:** Log khi gửi Telegram alert bị lỗi (success không log để tránh noise).

**Ví dụ:**
```
[NOTIFY][WARN] Telegram send failed: status=401 event=TRADE_OPEN_CONFIRMED
[NOTIFY][WARN] Telegram send exception: event=TRADE_CLOSE_CONFIRMED error=HTTP request timeout
```

**Case đặc biệt:**
- `status=401` → BotToken sai hoặc hết hạn. Cần update credentials
- `HTTP request timeout` liên tục → firewall block port 443 từ VPS đến Telegram servers

---

## [MMF_TRADES] / [MMF_HISTORY] — Shared Memory Change Detection

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Log khi ticket set trong shared memory thay đổi — trace khi nào lệnh xuất hiện/biến mất trong MMF.

**Ví dụ:**
```
[MMF_TRADES][INFO]  Ticket set changed: map=Mt5_Trades total=3 new=[12345,67890,11111] removed=[] changed=true
[MMF_HISTORY][INFO] Ticket set changed: map=Mt5_History total=25 new=[99999] removed=[] changed=true
```

**Case đặc biệt:**
- `removed=[12345]` trong `[MMF_TRADES]` → lệnh bị close (hoặc stop-out). Recovery/close flow sẽ được trigger
- `new=[...]` xuất hiện nhưng không có `[CYCLE][INFO] Pending open leg confirmed` → lệnh mở không qua app (manual trade). Check `not tool-opened` guard ở [CLOSE_SELECT]

---

## [MANUAL] — Manual Trade Operations

**File:** `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**Dùng để làm gì:** Log khi thao tác trade thủ công được thực hiện (Phase 6 đã ẩn manual buttons, nhưng fallback path vẫn log).

**Case đặc biệt:**
- Thấy `[MANUAL]` trong Phase 6+ → có path bypass manual buttons visible check. Cần investigate

---

## [SIGNAL_OUTCOME] — Gap sau signal (file riêng)

**File nguồn:** `TradeDesktop.Application/Services/SignalGapOutcomeTracker.cs`
· hook trong `TradeDesktop.App/ViewModels/DashboardViewModel.cs`

**File log:** `~/Desktop/trade-log/{yyyyMMdd_HHmmss}-signal-outcome.log` — kênh RIÊNG, tách khỏi
`trade-log.log` và `gap-stability-raw.log`, dùng chung rotation 50 MB.

**Dùng để làm gì:** Đánh giá hậu nghiệm chất lượng signal. Các log `[OPEN_CYCLE]` /
`[NORMAL_CLOSE_CYCLE]` chỉ cho biết gap TRƯỚC lúc trigger; file này ghi thêm gap của **50 tick
kế tiếp** để so sánh signal với diễn biến thị trường thật.

**Phạm vi:** signal OPEN và signal CLOSE loại **Normal**, và chỉ những signal **thực sự được
dispatch**. SOS close, TP close đều không xuất hiện.

**Signal bị chặn không được ghi.** Trace mở tại tick signal nhưng chỉ ghi ra file khi lệnh đã
được gửi thật; trace không tới được điểm đó sẽ bị bỏ im lặng sau ~30 s. Bao gồm mọi gate:
quota/cooldown/opposite-lock (lọc trong `PortfolioCoordinator`), `SignalEntryGuard`, qualifying
count, và cả các gate nằm sâu trong đường dispatch — với OPEN là watchdog, opposite price guard,
in-flight lock, pending cycle, allocate slot; với CLOSE là `_closeDispatchInFlight`, transition
gate, non-auto barrier, `ShouldSkipTradeOp`.

Ngoại lệ có chủ đích: nếu lệnh đã được gửi tới sàn nhưng **thất bại ở router**, signal vẫn được
ghi — giữ đúng ngữ nghĩa "đã vào lệnh" và khớp với hành vi của panel Signal.

**Mỗi signal đúng 2 dòng:**

| Event | Ghi khi nào | Nội dung |
|-------|-------------|----------|
| `[SIGNAL]` | signal được dispatch (đã có STT) | giá A/B, gap tại signal, ngưỡng config, `signal_gaps` |
| `[END]` | đủ 50 tick (~2.5 s sau) | `signal_gaps` + `future_gaps` (50 giá trị) + `elapsed_ms` |

**Ví dụ:**
```
[SIGNAL_OUTCOME][SIGNAL] stt=7 pair_id=AUTO-0003-8412553 signal_id=3f2a1c90... cycle_id=OPEN-BUY-000417 slot_id=3 action=OPEN side=BUY trigger_type=OpenByGapBuy triggered_at=2026-08-31T03:15:32.140Z symbol="XAUUSD|XAUUSD.s" point=100 confirm_gap_pts=25 open_pts=30 ... gap_buy=40 gap_sell=-20 track_gap=BUY gap_at_signal=40 gaps_order=oldest_to_newest gaps_unit=point signal_gaps="38|39|40|40|41|40|40|39|40|40" future_ticks_planned=50
[SIGNAL_OUTCOME][END]    stt=7 pair_id=AUTO-0003-8412553 signal_id=3f2a1c90... action=OPEN side=BUY track_gap=BUY status=COMPLETED captured=50/50 skipped_null_ticks=0 gap_at_signal=40 elapsed_ms=2554 ... signal_gaps="38|...|40" future_gaps="40|41|43|44|...|33"
```

**Các trường quan trọng:**
- `stt` — **đúng số STT hiển thị trên UI của lệnh** (cột STT grid Trade/History, nút `Close(n)`,
  panel Signal). Cùng nguồn `ResolveStt(pairId)` nên tra ngược log ↔ màn hình luôn khớp.
  Vì `PositionSlot.PairId` bất biến, **OPEN và CLOSE của cùng một lệnh có cùng `stt`** — lọc
  `stt=7` là ra trọn vòng đời lệnh đó. STT reset mỗi lần bấm Start (cùng nhịp với grid).
- `gap_at_signal` — gap tại thời điểm signal, là mốc để so sánh với `future_gaps`.
- `signal_gaps` — dãy gap của chu kỳ đã xác nhận signal; lặp lại ở cả 2 dòng để dòng `[END]`
  tự đủ dữ liệu paste vào Excel.
- `future_gaps` — 50 gap của 50 tick kế tiếp, kể cả khi gap không đổi. Đơn vị point, cũ → mới.
- `track_gap` — chiều gap **thực sự kích hoạt signal** (`BUY` → `GapBuy`, `SELL` → `GapSell`),
  lấy theo `TriggerType`. Với signal CLOSE nó **khác** `side`: `side` là chiều của vị thế đang
  đóng, còn một vị thế Buy được đóng bằng gap Sell đảo chiều — nên `side=BUY track_gap=SELL`
  là bình thường và đúng. `gap_at_signal`, `signal_gaps`, `future_gaps` đều bám theo `track_gap`.
- `status` — `COMPLETED` (đủ 50 tick) · `SESSION_STOP` (dừng session giữa chừng) ·
  `STALE` (feed tick đứt > 30 s) · `EVICTED` (vượt 16 trace đồng thời). `captured=N/50` cho
  biết dòng có đủ dữ liệu không.

**Case đặc biệt:**
- Có dòng `[SIGNAL]` mà không có `[END]` cùng `stt` → session dừng trước khi đủ 50 tick, hoặc
  feed tick đứt. Kiểm tra `[MARKET]`.
- `skipped_null_ticks` lớn → MMF trả gap null nhiều, suspect mất kết nối một sàn.
- `signal_gaps=""` trên signal CLOSE → **bug đã sửa**: gap list từng bị chọn theo `PrimarySide`
  thay vì `TriggerType`, nên với close vị thế Buy (gaps nằm ở `SellGaps`) sẽ đọc trúng list rỗng
  và `future_gaps` bám sai chiều gap. Log cũ trước bản sửa không dùng để phân tích được.
- `stt=-` → UI chưa cấp số cho pair đó tại thời điểm ghi (hiếm; thường chỉ xảy ra với slot vừa
  restore sau restart mà grid chưa render). Đường log **không bao giờ tự cấp số mới** để tránh
  làm lệch thứ tự đánh số của grid, nên dùng `pair_id` để dò ngược trong trường hợp này.

---

## Debug by scenario

| Vấn đề | Log cần xem |
|--------|-------------|
| Lệnh không mở | `[CYCLE]`, `[SLOT][SKIP]`, `[GUARD]`, `[SLOT][COOLDOWN][BLOCK]` |
| Click thất bại | `[ROUTER]`, `[MT4]`, `[MT5]` |
| Slot không close | `[CLOSE_SELECT]`, `[SLOT][TP_CHECK]`, `[SLOT][COOLDOWN]` |
| Sau restart không recover | `[RECOVERY]`, `[PERSIST]`, `[MMF_TRADES]` |
| Config không load | `[DB]`, `[CONFIG]` |
| Hệ thống bị pause | `[WATCHDOG]`, `[MARKET]` |
| Cooldown không clear | `[SLOT][COOLDOWN][BLOCK]` + `[SLOT][COOLDOWN][CLEAR]` |
| Telegram không nhận | `[NOTIFY]` |
| Half-open (1 leg thành công, 1 leg thất bại) | `[ROUTER][WARN]`, `[MT4][WARN]`, `[MT5][WARN]`, `[CYCLE][ERROR]` |
| Orphan slot sau restart | `[RECOVERY][WARN] foundA=false`, `[PERSIST]` |
| Signal đúng hay sai (đánh giá chất lượng) | file `-signal-outcome.log`: `[SIGNAL_OUTCOME][END]`, lọc theo `stt` |
