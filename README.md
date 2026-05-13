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

### 3.1 OpenByGapBuy

1. `GapBuy` hiện tại phải tồn tại và `>= ConfirmGapPts`.
2. Mở cửa sổ confirm, gom chuỗi gap theo thời gian.
3. Khi đủ `HoldConfirmMs`, toàn bộ `BuyGaps` trong cửa sổ phải thỏa `>= ConfirmGapPts`.
4. Tick cuối cùng phải thỏa `>= OpenPts`.
5. Nếu `OpenMaxTimesTick > 0` thì số tick trong cửa sổ không được vượt ngưỡng này.
6. Thỏa hết điều kiện -> trigger `OpenByGapBuy`.

### 3.2 OpenByGapSell

Đối xứng với ngưỡng âm:

1. `GapSell <= -ConfirmGapPts`.
2. Duy trì liên tục đủ `HoldConfirmMs`.
3. Toàn bộ `SellGaps` trong cửa sổ thỏa `<= -ConfirmGapPts`.
4. Tick cuối cùng thỏa `<= -OpenPts`.
5. Nếu `OpenMaxTimesTick > 0`, số tick phải nằm trong giới hạn.
6. Thỏa hết -> trigger `OpenByGapSell`.

> Bất kỳ điều kiện nào fail trong window -> reset state của nhánh đó.

---

## 4) Logic CLOSE signal (xác nhận đóng lệnh)

File: `TradeDesktop.Application/Services/CloseSignalEngine.cs`

Config close được chuẩn hóa:

- `CloseConfirmGapPts = Abs(config.CloseConfirmGapPts)`
- `ClosePts = Abs(config.ClosePts)`
- `CloseHoldConfirmMs = Max(0, config.CloseHoldConfirmMs)`
- `CloseMaxTimesTick = Max(0, config.CloseMaxTimesTick)`

Rule theo mode đã mở:

- Nếu đang mở từ `GapBuy` (`TradingOpenMode.GapBuy`)  
  -> close theo nhánh `GapSell` với điều kiện âm:
  - confirm: `GapSell <= -CloseConfirmGapPts`
  - tick cuối: `GapSell <= -ClosePts`

- Nếu đang mở từ `GapSell` (`TradingOpenMode.GapSell`)  
  -> close theo nhánh `GapBuy` với điều kiện dương:
  - confirm: `GapBuy >= CloseConfirmGapPts`
  - tick cuối: `GapBuy >= ClosePts`

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
