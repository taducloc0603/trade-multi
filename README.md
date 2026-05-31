# TRADE-MULTI - WPF Trading Dashboard (.NET 8)

README này mô tả lại ngắn gọn nhưng đầy đủ các phần quan trọng của dự án: **chức năng**, **logic giao dịch**, và **công thức tính tín hiệu** theo đúng implementation hiện tại.

---

## 1) Tổng quan hệ thống

`TradeDesktop` là ứng dụng WPF (.NET 8) dùng để:

- Đọc dữ liệu giá thời gian thực từ shared memory của 2 nguồn (A/B).
- Tính `GapBuy/GapSell` theo point multiplier cấu hình.
- Xác nhận tín hiệu `OPEN/CLOSE` bằng cửa sổ thời gian (anti-noise).
- Điều phối luồng giao dịch theo state machine (mở/giữ/đóng/chờ).
- Gửi lệnh xuống executor MT4/MT5 theo từng leg A/B.
- Ghi log tín hiệu chi tiết để truy vết công thức và giá kích hoạt.

Kiến trúc layer:

```text
TradeDesktop.sln
├─ TradeDesktop.App/             # WPF UI + ViewModels + orchestration thực thi lệnh
├─ TradeDesktop.Application/     # Business rules, flow engine, models, abstractions
├─ TradeDesktop.Domain/          # Domain models thuần
├─ TradeDesktop.Infrastructure/  # Shared memory reader, Supabase repository, signal infra
└─ TradeDesktop.Tests/           # Unit tests cho các logic cốt lõi
```

---

## 2) Công thức cốt lõi

File: `TradeDesktop.Application/Services/GapCalculator.cs`

### 2.1 Công thức Gap

- `GapBuy = (B.Bid - A.Ask) * Point`
- `GapSell = (B.Ask - A.Bid) * Point`

Trong code kết quả được ép `int`:

- `gapBuy = (int)((sanB.Bid - sanA.Ask) * pointMultiplier)`
- `gapSell = (int)((sanB.Ask - sanA.Bid) * pointMultiplier)`

`Point` lấy từ runtime config (`CurrentPoint`), fallback về `1` nếu `<= 0`.

### 2.2 Ý nghĩa nghiệp vụ

- `GapBuy` đủ dương -> thiên hướng trigger nhánh Buy.
- `GapSell` đủ âm -> thiên hướng trigger nhánh Sell.
- Trigger không dựa 1 tick đơn lẻ, mà yêu cầu giữ điều kiện trong khoảng thời gian xác nhận.

---

## 3) Logic OPEN signal (xác nhận mở lệnh)

File: `TradeDesktop.Application/Services/GapSignalConfirmationEngine.cs`

Config trước khi áp rule được chuẩn hóa:

- `ConfirmGapPts = Abs(config.ConfirmGapPts)`
- `OpenPts = Abs(config.OpenPts)`
- `HoldConfirmMs = Max(0, config.HoldConfirmMs)`
- `OpenMaxTimesTick = Max(0, config.OpenMaxTimesTick)`
- `LimitMaxGap = Max(0, config.LimitMaxGap)` — `0` = disabled

### 3.1 OpenByGapBuy

1. `GapBuy` hiện tại phải tồn tại và `>= ConfirmGapPts`.
2. Nếu `LimitMaxGap > 0` và `|GapBuy| > LimitMaxGap` → **reset window ngay** (chu kỳ mới).
3. Mở cửa sổ confirm, gom chuỗi gap theo thời gian.
4. Khi đủ `HoldConfirmMs`, toàn bộ `BuyGaps` trong cửa sổ phải thỏa `>= ConfirmGapPts`.
5. Tick cuối cùng phải thỏa `>= OpenPts`.
6. Nếu `OpenMaxTimesTick > 0` thì số tick trong cửa sổ không được vượt ngưỡng này.
7. Thỏa hết điều kiện -> trigger `OpenByGapBuy`.

### 3.2 OpenByGapSell

Đối xứng với ngưỡng âm:

1. `GapSell <= -ConfirmGapPts`.
2. Nếu `LimitMaxGap > 0` và `|GapSell| > LimitMaxGap` → **reset window ngay** (chu kỳ mới).
3. Duy trì liên tục đủ `HoldConfirmMs`.
4. Toàn bộ `SellGaps` trong cửa sổ thỏa `<= -ConfirmGapPts`.
5. Tick cuối cùng thỏa `<= -OpenPts`.
6. Nếu `OpenMaxTimesTick > 0`, số tick phải nằm trong giới hạn.
7. Thỏa hết -> trigger `OpenByGapSell`.

> Bất kỳ điều kiện nào fail trong window -> reset state của nhánh đó.

> **`LimitMaxGap`** áp dụng trên mỗi tick — khác với `max_gap` trong `SignalEntryGuard` chỉ kiểm tra tại thời điểm trigger. Khi gap spike vượt ngưỡng, window reset ngay; khi gap về lại bình thường, window mở lại từ đầu.

---

## 4) Logic CLOSE signal (xác nhận đóng lệnh)

File: `TradeDesktop.Application/Services/CloseSignalEngine.cs`

Config close được chuẩn hóa:

- `CloseConfirmGapPts = Abs(config.CloseConfirmGapPts)`
- `ClosePts = Abs(config.ClosePts)`
- `CloseHoldConfirmMs = Max(0, config.CloseHoldConfirmMs)`
- `CloseMaxTimesTick = Max(0, config.CloseMaxTimesTick)`
- `LimitMaxGap = Max(0, config.LimitMaxGap)` — `0` = disabled (dùng chung với open signal)
- `LimitMaxTp = Abs(config.LimitMaxTp)` — `0` = disabled

Rule theo mode đã mở:

- Nếu đang mở từ `GapBuy` (`TradingOpenMode.GapBuy`)  
  -> close theo nhánh `GapSell` với điều kiện âm:
  - confirm: `GapSell <= -CloseConfirmGapPts`
  - tick cuối: `GapSell <= -ClosePts`
  - `LimitMaxGap` áp dụng tương tự open: nếu `|GapSell| > LimitMaxGap` → reset window

- Nếu đang mở từ `GapSell` (`TradingOpenMode.GapSell`)  
  -> close theo nhánh `GapBuy` với điều kiện dương:
  - confirm: `GapBuy >= CloseConfirmGapPts`
  - tick cuối: `GapBuy >= ClosePts`
  - `LimitMaxGap` áp dụng tương tự

**TP close path** (song song với gap close, thắng nếu trigger trước):

- Profit phải `>= CloseConfirmTpProfit` để mở window.
- Nếu `LimitMaxTp > 0` và `profit > LimitMaxTp` → **reset TP window ngay** (chu kỳ mới).
- Sau `CloseHoldConfirmMs`, toàn bộ profits trong window phải `>= CloseConfirmTpProfit`.
- Tick cuối phải `>= CloseTpProfit` và `<= CloseMaxTpProfit` (nếu set).

> **`LimitMaxTp`** khác với `CloseMaxTpProfit`: `CloseMaxTpProfit` chỉ kiểm tra tại trigger (suspend, không reset), còn `LimitMaxTp` reset window ngay tại tick spike.

---

## 5) State machine giao dịch

File: `TradeDesktop.Application/Services/TradingFlowEngine.cs`

### 5.1 Trạng thái

- `WaitingOpen`
- `WaitingCloseFromGapBuy`
- `WaitingCloseFromGapSell`

### 5.2 Luồng xử lý

1. Ở `WaitingOpen`: chỉ kiểm tra open khi đã qua `CurrentWaitSeconds` kể từ lần close gần nhất.
2. Khi open trigger thành công:
   - Set `CurrentOpenMode`, `CurrentPositionSide`, `OpenedAtUtc`
   - Random `CurrentHoldingSeconds` trong `[StartTimeHold..EndTimeHold]`
   - Chuyển qua state chờ close tương ứng.
3. Ở state close: chỉ kiểm tra close khi đã giữ lệnh đủ `CurrentHoldingSeconds`.
4. Khi close trigger xuất hiện: đánh dấu pending close execution.
5. Khi close thực sự hoàn tất (`BeginWaitAfterClose`):
   - reset trạng thái position về none
   - set `ClosedAtUtc`
   - random `CurrentWaitSeconds` trong `[StartWaitTime..EndWaitTime]`
   - quay về `WaitingOpen`.

=> Mục tiêu nghiệp vụ: hạn chế spam lệnh, tránh vào/ra liên tục theo nhiễu ngắn hạn.

### 5.3 Race protection cho auto-open (3 lớp)

Giữa thời điểm tool click open và thời điểm MMF cập nhật ticket vào cache, có thể có độ trễ vài trăm ms đến vài giây. Để đảm bảo **an toàn vốn** và tránh mở chồng, hệ thống dùng 3 lớp bảo vệ:

1. **Layer 1 — Pending Open Cycle Lock (deterministic):**
   - Trước khi `AutoBuyAsync/AutoSellAsync` mở lệnh mới, hệ thống check `_pendingOpenPairById`.
   - Nếu tồn tại cycle auto chưa hoàn tất (`!IsResolved && !TimeoutCloseTriggered && !(OpenConfirmedA && OpenConfirmedB)`) thì block open mới.
2. **Layer 2 — Debounce ngắn sau click (`AutoOpenDebounceMs=1500ms`):**
   - Backup chống edge-race trong cửa sổ rất ngắn ngay sau click.
   - Nếu vừa click và MMF chưa phản ánh ticket tool thì block thêm 1 lần mở mới.
3. **Layer 3 — Invariant watchdog auto-pause + auto-clear:**
   - Nếu phát hiện vi phạm `toolA>1` hoặc `toolB>1` / nhiều live auto pairs -> pause auto-open.
   - Tự clear sau `InvariantClearPollsRequired` poll ổn định khi cả 2 map healthy.

### 5.4 Tunable parameters

| Parameter | Default | Vị trí | Ý nghĩa |
|---|---|---|---|
| `CurrentOpenPendingTimeMs` | `1000ms` | `RuntimeConfigState` | Timeout cho pending open cycle |
| `AutoOpenDebounceMs` | `1500ms` | `DashboardViewModel` | Debounce backup sau lần click open gần nhất |
| `InvariantClearPollsRequired` | `5` polls | `DashboardViewModel` | Số poll ổn định trước khi auto-resume invariant watchdog |

---

## 6) Mapping Trigger -> Instruction -> Log

Files:

- `TradeDesktop.Application/Services/TradeInstructionFactory.cs`
- `TradeDesktop.Application/Services/TradeSignalLogBuilder.cs`

Khi trigger xảy ra:

1. Build `TradeSignalInstruction` từ `GapSignalTriggerResult`:
   - xác định trigger type (`OpenByGapBuy`, `CloseByGapSell`, ...)
   - chọn trigger gaps (BuyGaps hoặc SellGaps)
   - ánh xạ biểu thức nguồn giá:
     - nhánh GapBuy: `(B.Bid - A.Ask) * Point`
     - nhánh GapSell: `(B.Ask - A.Bid) * Point`
2. Build log gồm 4 dòng:
   - header: `[OPEN BY GAP_BUY] GAP ...`
   - explain: công thức + giá input + point
   - leg A
   - leg B

Chuỗi này giúp debug theo logic: **giá nguồn -> gap -> trigger -> lệnh**.

---

## 7) Runtime config và thực thi lệnh

### 7.1 Runtime config

Files:

- `TradeDesktop.Application/Services/ConfigService.cs`
- `TradeDesktop.App/State/RuntimeConfigState.cs`
- `TradeDesktop.Infrastructure/Supabase/SupabaseConfigRepository.cs`

Điểm chính:

- Config được load/save theo `machine host name` (đã normalize lowercase).
- Nhiều trường số được normalize an toàn (`Abs`, `Max(0)`, fallback point = 1).
- `platform_a/platform_b` normalize về `mt4` hoặc `mt5` (default `mt5`).

DB fields liên quan đến guard/limit (tất cả `= 0` là disabled):

| DB column | C# property | Kiểu | Ý nghĩa |
|---|---|---|---|
| `max_gap` | `CurrentMaxGap` | `int` | Chặn open/close tại trigger nếu `|gap| > max_gap` (post-trigger guard) |
| `limit_max_gap` | `CurrentLimitMaxGap` | `int` | Reset confirm window ngay nếu `|gap| > limit_max_gap` trong mỗi tick |
| `limit_max_tp` | `CurrentLimitMaxTp` | `double` | Reset TP window ngay nếu `profit > limit_max_tp` trong mỗi tick |

### 7.2 Routing thực thi lệnh

File: `TradeDesktop.App/Services/TradeExecutionRouter.cs`

- Router nhận request pair (open/close), validate platform từng leg.
- Điều phối executor theo platform (`Mt4TradeExecutor` / `Mt5TradeExecutor`).
- Hỗ trợ delay từng leg (`DelayOpenAMs`, `DelayOpenBMs`, `DelayCloseAMs`, `DelayCloseBMs`).

---

## 8) Data source shared memory

Files chính:

- `TradeDesktop.Infrastructure/MarketData/SharedMemoryMarketDataReader.cs`
- `TradeDesktop.Infrastructure/SharedMemory/TradesSharedMemoryReader.cs`
- `TradeDesktop.Infrastructure/SharedMemory/HistorySharedMemoryReader.cs`

Chức năng:

- Poll map tick theo chu kỳ 50ms.
- Parse/validate tick record (version, bid/ask/spread, symbol, timestamp).
- Tính thêm thống kê runtime (`latency`, `max/avg latency`, `TPS`).
- Đọc map trades/history để đối soát lệnh và hiển thị bảng realtime/history.

---

## 9) File nên đọc khi onboarding

### App

- `TradeDesktop.App/ViewModels/DashboardViewModel.cs` (orchestration chính)
- `TradeDesktop.App/ViewModels/ConfigViewModel.cs` (config runtime)
- `TradeDesktop.App/MainWindow.xaml` (UI dashboard)

### Application

- `GapCalculator.cs`
- `GapSignalConfirmationEngine.cs`
- `CloseSignalEngine.cs`
- `TradingFlowEngine.cs`
- `SignalEntryGuard.cs`
- `TradeInstructionFactory.cs`
- `TradeSignalLogBuilder.cs`
- `Models/GapSignalModels.cs`

### Tests

- `TradeDesktop.Tests/GapCalculatorTests.cs`
- `TradeDesktop.Tests/GapSignalConfirmationEngineTests.cs`
- `TradeDesktop.Tests/CloseSignalEngineTests.cs`
- `TradeDesktop.Tests/TradingFlowEngineTests.cs`
- `TradeDesktop.Tests/TradeSignalLogBuilderTests.cs`

---

## 10) Checklist debug nhanh khi tín hiệu sai

Đối chiếu theo thứ tự:

1. Snapshot giá A/B có hợp lệ không?
2. `Point` runtime có đúng không?
3. `GapBuy/GapSell` tính ra có đúng kỳ vọng không?
4. Cửa sổ confirm có đủ `HoldConfirmMs` và thỏa toàn bộ tick không?
5. Tick cuối có đạt ngưỡng open/close không?
6. `OpenMaxTimesTick/CloseMaxTimesTick` có chặn trigger không?
7. State flow hiện tại là gì (`WaitingOpen` hay `WaitingClose*`)?
8. Trigger -> Instruction -> Log có khớp công thức nguồn giá không?

---

## 11) Logging

### 11.1 Cấu hình `.env`

| Biến | Mô tả | Giá trị mặc định |
|------|--------|-------------------|
| `LOG_LEVEL` | Mức log tối thiểu ghi vào file. Giá trị: `DEBUG`, `INFO`, `WARN`, `ERROR` | `INFO` |
| `LOG_MAX_FILE_SIZE_MB` | Kích thước tối đa mỗi file log (MB) trước khi rotation | `50` |

Ví dụ trong `.env`:

```env
LOG_LEVEL=INFO
LOG_MAX_FILE_SIZE_MB=50
```

### 11.2 Log rotation

- Khi file log đạt ngưỡng `LOG_MAX_FILE_SIZE_MB`, hệ thống tự động tạo file mới với suffix `.001.log`, `.002.log`, ...
- Mỗi file mới có header continuation ghi rõ session gốc, thời điểm rotate, host name.
- File structure ví dụ:

```
Desktop/trade-log/
├── 20260424_143020-trade-log.log       (50MB, full)
├── 20260424_143020-trade-log.001.log   (50MB, full)
├── 20260424_143020-trade-log.002.log   (đang ghi)
└── 20260424_150000-trade-log.log       (session khác)
```

### 11.3 Level filter

- Method `Log(string message)` tự suy level từ substring: `][ERROR]` → Error, `][WARN]` → Warn, `][DEBUG]` → Debug, còn lại → Info.
- Method `Log(TradeLogLevel level, string message)` dùng level truyền vào trực tiếp.
- Dòng log có level thấp hơn `LOG_LEVEL` sẽ bị bỏ qua, không ghi vào file.

### 11.4 UI menu

- **Log Folder**: mở thư mục `Desktop/trade-log` trong Explorer/Finder.
- **Current Log**: mở file log hiện tại bằng editor mặc định (Notepad/VSCode/...). Nếu chưa Start session → hiện thông báo.

---

## 12) Build / Run / Test

```bash
dotnet restore TradeDesktop.sln
dotnet build TradeDesktop.sln
dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
```

Chạy app:

```bash
dotnet run --project TradeDesktop.App/TradeDesktop.App.csproj
```

---

## 13) Portfolio Coordinator — multi-slot architecture

File chính: `TradeDesktop.Application/Services/Portfolio/PortfolioCoordinator.cs`

### 13.1 Khái niệm

Phase 0-7 refactor: từ single-slot `TradingFlowEngine` (Section 5) → multi-slot
`PortfolioCoordinator`. Mỗi lệnh = 1 `PositionSlot` độc lập với `CloseSignalEngine` riêng,
profit tracking riêng, lifecycle riêng.

`TradingFlowEngine` cũ vẫn tồn tại (`[Obsolete]`) cho legacy `TradingFlowEngineTests`.
DI registration: `ITradingFlowEngine` → `PortfolioCoordinatorAdapter` (Application/DI line 23).

### 13.2 PositionSlot lifecycle

```
PendingOpen → Live → PendingClose → Closed
     │         │           │
     │         │           └── MMF confirm cả 2 leg close
     │         └── Close signal trigger + execution dispatched
     └── MMF confirm cả 2 leg open
```

Fields chính:
- `SlotId : int` — monotonic, unique trong session, không recycle.
- `PairId : string` — format `"AUTO-{SlotId:D4}-{rawMs}"`.
- `Side / OpenMode` — Buy/Sell và GapBuy/GapSell.
- `TicketA, TicketB : ulong?` — ticket MT4/MT5 sau MMF confirm.
- `OpenConfirmedAtUtc` — lúc MMF confirm cả 2 leg open.
- `HoldingSeconds` — random `[StartTimeHold..EndTimeHold]` per slot.
- `CloseSignalEngine` — instance riêng, KHÔNG share giữa slots.
- `LastProfitSnapshot` — profit hiện tại từ MMF poll (dùng cho Rule D).

### 13.3 Business rules

| Rule | Spec |
|------|------|
| **A — Quota** | Max 7 tổng, max 4 Buy, max 4 Sell. Đếm cả `PendingOpen + Live + PendingClose`. |
| **B — Cooldown** | Random `Uniform(GlobalCooldownMinSec, MaxSec)` sau mỗi OPEN/CLOSE confirm. Khóa toàn hệ thống. |
| **C — Opposite-side lock** | Hardcode 300s sau OPEN. Chỉ block OPEN opposite-side. Same-side refresh timer. KHÔNG block CLOSE. |
| **D — Priority close (extended)** | Khi nhiều slot trigger close cùng tick: (1) nếu `max_life_time_by_second > 0`, lọc ra các slot có tuổi `(now - OpenConfirmedAtUtc) > max_life_time_by_second` (overtime tier) → chọn profit cao nhất trong tier đó; (2) nếu không có slot nào overtime, chọn profit cao nhất trong tất cả eligible (Rule D gốc). Losers giữ window. `max_life_time_by_second = 0` (default) = disable tier, hành vi giống Rule D gốc. |

Rule A + C check trong `CanOpenNewSlot(side, out reason)`. Rule B check trong `CanCloseNow(out reason)`.
Rule D pick trong `ProcessSnapshot` close path: overtime tier nếu có, fallback toàn bộ eligible — cả 2 đều `OrderByDescending(LastProfitSnapshot ?? double.MinValue).First()`.

### 13.4 Cap config

Mặc định Phase 0: `MaxTotalOpens=1, MaxBuy=1, MaxSell=1` để giữ behavior production identical
với single-slot. Phase 5 sẽ load từ DB column `current_slots` (đã có code-side
`SlotPersistence.Serialize/Deserialize` ready, DB migration deferred).

ViewModel push config xuống coordinator qua `SyncPortfolioCoordinatorConfig()`:
- Gọi từ constructor + sau mỗi `ApplyRuntimeConfig()`.
- Map `RuntimeConfigState.CurrentMaxTotal/Buy/SellOpens` → `coordinator.UpdateQuotaConfig`.
- Map `CurrentStartWaitTime/EndWaitTime` → `coordinator.UpdateCooldownConfig`.
- Map `CurrentMaxLifeTimeBySecond` (từ DB column `max_life_time_by_second`) → `coordinator.UpdateMaxLifeTimeConfig`.

### 13.5 ProcessSnapshot flow (mỗi tick 50ms)

```
1. Cache lastSeen* hold range cho close-gate fallback.
2. Resolve effectiveNow với wall-clock tolerance.
3. Check global cooldown (Rule B) → return Empty nếu active.
4. OPEN path:
   - Quota A allow → openSignalEngine.ProcessSnapshot → trigger.
   - Loop triggers, skip nếu CanOpenNewSlot fail (logs skip reason).
   - Return PortfolioSnapshotResult(OpenTrigger: trigger).
5. CLOSE path (nếu open path không return):
   - Loop Live slots, skip slot IsCloseExecutionPending hoặc holding chưa elapsed.
   - Per-slot slot.CloseSignalEngine.ProcessSnapshot → eligibleCloses.
   - Pick winner (Rule D extended):
     - Nếu `MaxLifeTimeBySecond > 0`: lọc overtime slots (`now - OpenConfirmedAtUtc > maxLifeTimeSec`) → nếu có, chọn profit cao nhất trong overtime.
     - Nếu không có overtime slot (hoặc `MaxLifeTimeBySecond = 0`): chọn profit cao nhất toàn bộ eligible (Rule D gốc).
   - MarkCloseTriggered, reset engines.
   - Return PortfolioSnapshotResult(CloseTarget: slot, CloseTrigger: trigger).
6. Else return Empty.
```

### 13.6 Race protection (Phase 3)

- **Layer 1 — Quota lock**: `AllocatePendingOpenSlot` dùng `lock(_allocateLock)` quanh
  `CanOpenNewSlot + AllocateNewSlot` để 2 callers concurrent không vượt quota.
- **Layer 2 — Per-side debounce**: `_lastAutoOpenBuyAtLocal` + `_lastAutoOpenSellAtLocal`
  (DashboardViewModel). Buy không cản Sell và ngược lại.
- **Layer 3 — Per-side in-flight lock**: `_autoOpenInFlightBuy` + `_autoOpenInFlightSell`
  Interlocked, plus global `_autoOpenInFlight` để defend-in-depth.
- **Layer 4 — Multi-slot watchdog**: `EvaluateAndApplyAutoOpenInvariantWatchdog` formula
  `toolCount > coordinator.LiveCount` hoặc `coordinator counts > cap`.

### 13.7 Persistence (Phase 5)

Code-side ready:
- `SlotPersistence.Serialize(liveSlots) : string` — JSON cho DB JSONB column.
- `SlotPersistence.Deserialize(json) : IReadOnlyList<RecoveredSlotData>`.
- `coordinator.RecoverSlotsFromPersisted(slots)`: restore slots, set next SlotId, kick startup cooldown, restore LastOpenSide.

Deferred (Infrastructure work):
- DB schema `current_slots JSONB` migration.
- `SupabaseConfigRepository.SaveCurrentSlotsAsync` + load.
- ViewModel periodic save + recovery flow on startup.

### 13.8 Close routing (Phase 4)

`SelectCloseCandidateForTicket(targetTicket, exchange, mapName, hwnd)` — ticket-precise
RowIndex lookup. KHÔNG fallback row 0 nếu ticket missing. Đảm bảo close đúng slot trong
multi-slot mode (cap>1). `AutoCloseOrderAsync(trigger, targetSlot)` nhận slot từ
`DispatchCloseTriggerAsync` → ticket-precise selection.

Legacy `SelectCloseCandidateForExchange` (first-tool-opened) còn `[Obsolete]` cho manual
close + history paths (cap=1 không có vấn đề).

### 13.9 Monitoring (Phase 7)

`coordinator.GetMetrics() : PortfolioMetrics`:
- Current live/pending counts (Buy/Sell/total/pendingOpen/pendingClose).
- Monotonic counters: `TotalOpensAllTime`, `TotalClosesAllTime`.
- Skip counters: `QuotaSkipCount`, `OppositeLockSkipCount`, `CooldownSkipCount`.

### 13.10 Acceptance invariants

Hệ thống luôn phải thỏa các invariants (verified qua integration tests):

1. **Quota**: `LiveBuyCount ≤ MaxBuyOpens ∧ LiveSellCount ≤ MaxSellOpens ∧ LiveAndPendingTotalCount ≤ MaxTotalOpens`.
2. **Cooldown**: giữa 2 confirm consecutive (open hoặc close), `elapsed ≥ GlobalCooldownMinSec ∧ ≤ MaxSec` (cộng MMF tolerance).
3. **Opposite-lock**: nếu OPEN side X tại `T`, không OPEN side ¬X trong `[T, T+300s]` (CLOSE bypass).
4. **Close priority**: slot được close phải có `LastProfitSnapshot ≥` mọi slot khác trong eligibleCloses.
5. **Slot isolation**: mỗi slot có CloseSignalEngine riêng, không share window state.
6. **Recovery**: sau app restart load persisted slots, GlobalActionLockUntilUtc active `[3,125]s` từ restart time.
7. **No quota leak**: abort path (timeout / failure) phải release slot khỏi coordinator.

### 13.11 Phase status

Phase 0-7 đã thực thi (code + tests). Phase 8 (docs) is this section.

Code-side hoàn toàn ready cho cap=7 production. Activation phụ thuộc:
- DB schema migration (Phase 5 deferred).
- `SupabaseConfigRepository.LoadAsync` thêm fields `max_total_opens`, `current_slots`, etc.
- ViewModel recovery flow on startup + periodic save 5s.
- XAML UI updates: status bar wider, manual button hide, Active Slots panel, OrderInfoPanel group.

Production behavior vẫn cap=1 (RuntimeConfigState defaults = 1) cho tới khi DB load.

