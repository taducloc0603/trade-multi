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

Confirmation mode là **TIME_AND_MIN_SAMPLES**: một chu kỳ chỉ được công nhận Stable khi đạt
**cả hai** điều kiện — đủ `open_gap_min_stable_samples` mẫu **và** đủ `open_hold_confirm_ms`
thời gian — rồi mới xét Dispersion/Drift.

> **Nhánh này là biến thể TIME** dùng để chạy song song và so sánh với nhánh TICK
> (`FIXED_SIZE` theo `signal_cycle_size`). Khoá đối chiếu giữa hai nhánh trong log là
> `confirmation_mode=`. Xem mục 7.1.

Config trước khi áp rule được chuẩn hóa:

- `ConfirmGapPts = config.ConfirmGapPts` — **giữ nguyên dấu**, xem §3.2
- `OpenPts = config.OpenPts` — **giữ nguyên dấu**, xem §3.2
- `HoldConfirmMs = Max(0, config.HoldConfirmMs)` — `0` = không yêu cầu thời gian
- `OpenMaxTimesTick = Max(0, config.OpenMaxTimesTick)` — `0` = không giới hạn độ dài chu kỳ
- `LimitMaxGap = Max(0, config.LimitMaxGap)` — `0` = disabled
- `OpenMaxLastGapPts = config.OpenMaxLastGapPts` — **giữ nguyên dấu, KHÔNG clamp về `>= 0`**;
  `null` = tắt gate, `0` và số âm vẫn hiệu lực

Khi chu kỳ đã Stable nhưng mẫu cuối chưa đạt `open_pts`, engine **không reset** — chu kỳ tiếp tục
thu mẫu và có thể trigger ở mẫu kế tiếp. Chế độ TIME cũng **không khử trùng lặp snapshot**.

Ngược lại, khi mẫu cuối đã đạt `open_pts` nhưng **chạm trần `open_max_last_gap_pts`**, engine
**reset chu kỳ ngay** (mở chu kỳ mới) và không phát trigger — xem mục 7.1.

`signal_cycle_size` vẫn được load, validate và ghi log để đối chiếu, nhưng **không tham gia
quyết định signal** trên nhánh này.

### 3.1 OpenByGapBuy

1. `GapBuy` hiện tại phải tồn tại và `>= ConfirmGapPts`; không đạt -> **reset chu kỳ**.
2. Nếu `LimitMaxGap > 0` và `|GapBuy| > LimitMaxGap` → **reset chu kỳ ngay**.
3. Mẫu hợp lệ được gom vào chu kỳ; snapshot trùng (cùng fingerprint) **không tăng count**.
4. Mẫu lệch quá `Tolerance` so với center của chu kỳ hiện tại → mở chu kỳ mới từ mẫu đó.
5. Khi đủ `SignalCycleSize` mẫu, chu kỳ phải đạt ngưỡng ổn định (`Dispersion <= MaxDispersion`
   và `Drift <= MaxDrift`) mới được coi là Stable.
6. Mẫu cuối phải thỏa `>= OpenPts`; không đạt thì kết thúc chu kỳ, mẫu tiếp theo mở chu kỳ mới.
7. Thỏa hết điều kiện -> trigger `OpenByGapBuy`.

### 3.2 OpenByGapSell

Đối xứng với ngưỡng âm:

1. `GapSell <= -ConfirmGapPts`.
2. Nếu `LimitMaxGap > 0` và `|GapSell| > LimitMaxGap` → **reset chu kỳ ngay**.
3. Gom đủ `SignalCycleSize` mẫu thỏa `<= -ConfirmGapPts`.
4. Mẫu cuối thỏa `<= -OpenPts`.
5. Thỏa hết -> trigger `OpenByGapSell`.

> Bất kỳ điều kiện confirm nào fail -> reset chu kỳ của nhánh đó và loại luôn mẫu vừa fail.

### 3.3 Ngưỡng mang dấu — `confirm_gap_pts` / `open_pts` được phép ÂM

Hai cột này **giữ nguyên dấu** từ DB (giống cặp `sos_close_*_pts`), không còn bị `Math.Abs`
ép dương ở bất kỳ tầng nào (config, engine, router).

| Giá trị | Nhánh GapBuy | Nhánh GapSell | Ý nghĩa |
|---|---|---|---|
| `open_pts = +8` | `GapBuy >= +8` | `GapSell <= -8` | Chuẩn cũ — chỉ mở khi gap rộng hẳn về một phía |
| `open_pts = -3` | `GapBuy >= -3` | `GapSell <= +3` | Nới về phía trong (cùng hướng SOS) — mở sớm khi gap còn hẹp/nghịch nhẹ |

Hai hệ quả bắt buộc phải biết:

- **Gate mẫu cuối chỉ có tác dụng khi `confirm_gap_pts < open_pts`** (so sánh **có dấu**). Nếu
  `confirm >= open` thì mẫu nào qua confirm cũng tự qua gate cuối, chỉ còn
  `open_gap_min_stable_samples` + `open_hold_confirm_ms` quyết định. App log `[DB][WARN]` cho cấu
  hình dạng này.
- **Hai nhánh Open có thể cùng trigger trong một tick khi và chỉ khi cả hai ngưỡng `<= 0`.** Vì
  `GapSell >= GapBuy` luôn đúng (Ask ≥ Bid), điều này bất khả thi với ngưỡng dương. Mô phỏng
  20 000 tick cho thấy ~21.8 % số tick rơi vào vùng đó khi đặt `(-5, -3)`. `PortfolioCoordinator`
  vẫn chỉ trả về **một** `OpenTrigger` mỗi snapshot, nên không có double-open; trigger còn lại bị
  bỏ qua.
- Tổ hợp "một giá trị âm, một giá trị `0`" **không có ý nghĩa**: `(0, âm)` tương đương `(0, 0)`,
  còn `(âm, 0)` gần tương đương `(0, 0)` và chỉ tạo thêm cycle chạy không. Muốn nới về phía trong
  phải đặt **cả hai** giá trị âm với `|confirm| > |open|`.

> **`LimitMaxGap`** áp dụng trên mỗi tick — khác với `max_gap` trong `SignalEntryGuard` chỉ kiểm tra tại thời điểm trigger. Khi gap spike vượt ngưỡng, window reset ngay; khi gap về lại bình thường, window mở lại từ đầu.

---

## 4) Logic CLOSE signal (xác nhận đóng lệnh)

File: `TradeDesktop.Application/Services/CloseSignalEngine.cs`

Config close được chuẩn hóa:

- `CloseConfirmGapPts = config.CloseConfirmGapPts` — **giữ nguyên dấu**, xem cuối §4
- `ClosePts = config.ClosePts` — **giữ nguyên dấu**, xem cuối §4
- `CloseHoldConfirmMs = Max(0, config.CloseHoldConfirmMs)` — dùng chung cho Normal Close,
  SOS Close và TP
- `CloseMaxTimesTick = Max(0, config.CloseMaxTimesTick)` — `0` = không giới hạn độ dài chu kỳ
- `LimitMaxGap = Max(0, config.LimitMaxGap)` — `0` = disabled (dùng chung với open signal)
- `LimitMaxTp = Abs(config.LimitMaxTp)` — `0` = disabled

> Normal Close và SOS Close chốt chu kỳ theo `close_gap_min_stable_samples` **và**
> `close_hold_confirm_ms`; TP chốt theo cửa sổ thời gian `close_hold_confirm_ms` (không có số mẫu
> đích). Ba loại giữ state hoàn toàn tách nhau. Xem mục 7.1.

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

**`close_confirm_gap_pts` / `close_pts` được phép ÂM.** Hai cột này giữ nguyên dấu từ DB, dùng
đúng công thức trên với ngưỡng có dấu — `close_pts = -8` cho slot GapBuy nghĩa là `GapSell <= +8`,
tức **chốt sớm khi gap chưa đảo chiều, thường là chốt lỗ** (bằng đúng hành vi SOS với abs 8).
Ba lưu ý vận hành:

- Gate mẫu cuối chỉ có tác dụng khi `close_confirm_gap_pts < close_pts` (so sánh có dấu);
  ngược lại `ConfigService` log `[DB][WARN]`.
- `min_profit_to_close > 0` sẽ chặn phần lớn close bằng gap âm cho tới khi
  `age >= max_life_time_by_second` — phải chỉnh hai cột này cùng nhau.
- `limit_max_gap` tác động **ngược nhau** tuỳ dấu của `close_pts`:
  - **`close_pts > 0`** — cần `gap` đi xa (`<= -close_pts`) nên nếu `limit_max_gap < close_pts` thì
    hai điều kiện loại trừ nhau → **cấu hình chết, không bao giờ trigger**. Đo được:
    `(+12, +8)` với `limit_max_gap = 5` cho **0 trigger / 20 000 tick**. Đây là bẫy **có sẵn từ
    trước**, không phải do ngưỡng âm sinh ra.
  - **`close_pts < 0`** — vùng thoả (`gap <= +|close_pts|`) luôn giao với `|gap| <= limit_max_gap`
    nên **không bao giờ chết**, chỉ bị thu hẹp. `limit_max_gap` ở đây là một **van giảm tần suất**
    khá hữu dụng: `(-12, -8)` cho `3 184 / 1 192 / 501 / 110` trigger / 20 000 tick ứng với
    `limit_max_gap = 0 / 5 / 3 / 1`.

Theo mô phỏng 20 000 tick, chuyển cặp Close sang `(-12, -8)` làm tần suất Close tăng khoảng
**15 lần** so với `(+12, +8)`. Nên đổi từng cặp một và theo dõi log trước khi áp production.

**SOS close path (theo từng slot):** SOS bật khi một trong hai điều kiện đúng: khoảng cách tuyệt đối giữa giá hiện tại và giá mở chân A `>= sos_trigger_a_open_distance_pts` (Buy dùng Bid A, Sell dùng Ask A; `0` là tắt), hoặc tuổi lệnh `>= sos_trigger_after_seconds` (`0` là tắt). Khoảng cách chân A được xét cả khi giá chạy thuận và chạy ngược chiều; nếu khoảng cách quay xuống dưới ngưỡng trước khi đủ thời gian thì slot trở lại Normal. Khi điều kiện thời gian đã đạt thì SOS tiếp tục bật. SOS Close chạy **chu kỳ riêng** (state tách hoàn toàn khỏi Normal Close và TP) nhưng dùng CHUNG `close_hold_confirm_ms`, `close_gap_min_stable_samples` và `close_max_times_tick` với Normal Close. Kiểm tra theo chiều hồi vào trong: mọi mẫu `GapSell` phải `<= +abs(sos_close_confirm_gap_pts)` và mẫu cuối `<= +abs(sos_close_gap_pts)` để phát `CloseByGapSell`; mọi mẫu `GapBuy` phải `>= -abs(sos_close_confirm_gap_pts)` và mẫu cuối `>= -abs(sos_close_gap_pts)` để phát `CloseByGapBuy`. Mẫu không đạt ngưỡng confirm sẽ reset chu kỳ (mẫu đó bị loại) và mẫu hợp lệ tiếp theo mở chu kỳ mới. Khi chu kỳ đã đủ mẫu + đủ hold nhưng mẫu cuối chưa đạt `sos_close_gap_pts` thì **không reset** — chu kỳ tiếp tục thu mẫu và có thể trigger ở mẫu kế tiếp. Nếu một trong hai ngưỡng Gap SOS bằng `0`, hệ thống fail-safe về bộ Gap thường. Mỗi lần đổi Normal ↔ SOS chỉ reset window Gap của slot, không reset TP window; log chỉ ghi lúc chuyển trạng thái để tránh spam. Trước khi dispatch, Gap được kiểm tra lại bằng đúng mode của signal và phải không vượt `limit_max_gap`. Trong global startup/recovery cooldown, close thường và mọi Open vẫn bị chặn; riêng slot đang thỏa SOS tại cùng snapshot được tiếp tục đánh giá và chỉ bypass global cooldown khi có close signal hợp lệ. Holding, post-open, Min Profit, transition gate và các close guard khác vẫn áp dụng. SOS Close được ghi nhận như Auto Close mới tại dispatch, sinh lại random same-action và post-close theo DB; vì vậy hành động tiếp theo phải chờ theo ma trận #9–#16.

**TP close path** (song song với gap close, thắng nếu trigger trước):

- Profit phải `>= CloseConfirmTpProfit` để mở window.
- Nếu `LimitMaxTp > 0` và `profit > LimitMaxTp` → **reset TP window ngay** (chu kỳ mới).
- Đủ `SignalCycleSize` mẫu profit thỏa `>= CloseConfirmTpProfit` mới xét trigger.
- Mẫu cuối phải `>= CloseTpProfit` và `<= CloseMaxTpProfit` (nếu set).

> **`LimitMaxTp`** khác với `CloseMaxTpProfit`: `CloseMaxTpProfit` chỉ kiểm tra tại trigger (suspend, không reset), còn `LimitMaxTp` reset window ngay tại tick spike.

---

### 4.1 Log vòng đời chu kỳ signal và panel dashboard

File: `TradeDesktop.Application/Services/GapCycleDiagnostics.cs`

Mỗi chu kỳ ghi log theo cùng một bộ event, tên nhóm lấy theo action:
`OPEN_CYCLE`, `NORMAL_CLOSE_CYCLE`, `SOS_CLOSE_CYCLE`, `TP_CYCLE`.

```
[SOS_CLOSE_CYCLE][STARTED]   cycle_id=... action=SOS_CLOSE side=BUY slot_id=3 count=1/3 hold=0/2000ms ...
[SOS_CLOSE_CYCLE][PROGRESS]  cycle_id=... count=3/3 hold=1200/2000ms gap=4 confirmation_mode=TIME_AND_MIN_SAMPLES ...
[SOS_CLOSE_CYCLE][RESET]     cycle_id=... reason="Gap không đạt điều kiện Confirm; reset Cycle."
[SOS_CLOSE_CYCLE][COMPLETED] cycle_id=... count=5/3 hold=2000/2000ms ...
[SOS_CLOSE_CYCLE][TRIGGERED] cycle_id=... signal_id=... count=5/3 hold=2000/2000ms gap=3 ...
```

Ở mode TIME, mẫu số của `count=` là `*_gap_min_stable_samples` và trường `hold=` cho biết tiến độ
thời gian; `count` **có thể vượt** mẫu số vì chu kỳ tiếp tục thu mẫu cho tới khi đủ hold-time hoặc
tới khi mẫu cuối đạt ngưỡng. `confirmation_mode=` là khoá đối chiếu với nhánh TICK.

Dashboard hiển thị tiến độ từng chu kỳ theo slot (`SignalCycleStatuses`), SOS Close và TP
là hai dòng độc lập:

```
Slot 3 SOS Close    7/10    Collecting
Slot 3 TP           4/10    Collecting
```

Normal Close và SOS Close dùng state riêng (`_closeByGap*Cycle` vs `_sosCloseByGap*Cycle`),
nên chuyển mode Normal ↔ SOS không làm lẫn dữ liệu giữa hai chu kỳ.

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
   - `CurrentWaitSeconds = 0` trong runtime hiện tại; global gate chịu trách nhiệm chờ
   - quay về `WaitingOpen`.

=> Mục tiêu nghiệp vụ: hạn chế spam lệnh, tránh vào/ra liên tục theo nhiễu ngắn hạn.

> `start_wait_time`/`end_wait_time` đã được xóa khỏi DB và toàn bộ config pipeline.
> Auto transition gate dùng các khoảng random `rd_start_post_open_lock_seconds` →
> `rd_end_post_open_lock_seconds`, `rd_start_post_close_lock_seconds` →
> `rd_end_post_close_lock_seconds` và khoảng
> random theo `rd_start_same_action_lock_seconds` → `rd_end_same_action_lock_seconds`
> cho các transition cùng loại; chi tiết tại mục 13.3.

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
- Nhiều trường số được normalize an toàn (`Abs`, `Max(0)`, fallback point = 1). **Ngoại lệ:**
  `open_pts`, `confirm_gap_pts`, `close_pts`, `close_confirm_gap_pts`, `sos_close_gap_pts`,
  `sos_close_confirm_gap_pts` giữ nguyên dấu — đừng thêm lại `Abs` cho 6 cột này.
- `platform_a/platform_b` normalize về `mt4` hoặc `mt5` (default `mt5`).

DB fields liên quan đến guard/limit (mỗi đầu post-close `<= 0` fallback `300s`;
những limit còn lại dùng `0` để disable):

| DB column | C# property | Kiểu | Ý nghĩa |
|---|---|---|---|
| `confirm_gap_pts` / `open_pts` | `CurrentConfirmGapPts` / `CurrentOpenPts` | `int` | Ngưỡng confirm và ngưỡng mẫu cuối của Open. **Giữ nguyên dấu, được phép âm** (âm = nới về phía trong như SOS). Xem §3.3 |
| `close_confirm_gap_pts` / `close_pts` | `CurrentCloseConfirmGapPts` / `CurrentClosePts` | `int` | Ngưỡng confirm và ngưỡng mẫu cuối của Normal Close. **Giữ nguyên dấu, được phép âm**. Xem cuối §4 |
| `max_gap` | `CurrentMaxGap` | `int` | Chặn open/close tại trigger nếu `|gap| > max_gap` (post-trigger guard) |
| `limit_max_gap` | `CurrentLimitMaxGap` | `int` | Reset confirm window ngay nếu `|gap| > limit_max_gap` trong mỗi tick |
| `limit_max_tp` | `CurrentLimitMaxTp` | `double` | Reset TP window ngay nếu `profit > limit_max_tp` trong mỗi tick |
| `min_profit_to_close` | `CurrentMinProfitToClose` | `double` | Trước `max_life_time_by_second`, chỉ cho Auto Close khi trị tuyệt đối dịch chuyển chân A từ Open Price đạt ngưỡng point (`abs(profitA) >= ngưỡng`); `0` là tắt |
| `sos_trigger_a_open_distance_pts` | `CurrentSosTriggerAOpenDistancePts` | `double` | Khoảng cách tuyệt đối giữa giá hiện tại và giá mở chân A, tính theo point; `0` là tắt |
| `sos_trigger_after_seconds` | `CurrentSosTriggerAfterSeconds` | `int` | Tuổi lệnh để kích hoạt SOS; `0` tắt điều kiện thời gian |
| `sos_close_confirm_gap_pts` | `CurrentSosCloseConfirmGapPts` | `int` | Ngưỡng bắt đầu xác nhận Gap Close khi SOS bật; được phép là số âm |
| `sos_close_gap_pts` | `CurrentSosCloseGapPts` | `int` | Ngưỡng phát Gap Close khi SOS bật; được phép là số âm |
| `rd_start_post_open_lock_seconds` / `rd_end_post_open_lock_seconds` | Runtime post-open range | `int` | Random một lần cho từng slot khi Open confirmed; chặn Auto Close của slot đó |
| `rd_start_post_close_lock_seconds` / `rd_end_post_close_lock_seconds` | Runtime post-close range | `int` | Random một lần cho mỗi Auto Close; chặn Auto Open cho tới hết deadline |
| `rd_start_same_action_lock_seconds` / `rd_end_same_action_lock_seconds` | Runtime same-action range | `int` | Random cho Open cùng chiều→Open cùng chiều, Open→Close và Close→Close |

**Signal cycle và price-freeze:**

| DB column | C# property | Kiểu | Ý nghĩa |
|---|---|---|---|
| `open_hold_confirm_ms` | `CurrentHoldConfirmMs` | `int` | Thời gian giữ tối thiểu của một **Open** Cycle. Chu kỳ phải đạt cả cột này lẫn `open_gap_min_stable_samples`. `0` = không yêu cầu thời gian; âm normalize về `0` |
| `close_hold_confirm_ms` | `CurrentCloseHoldConfirmMs` | `int` | Thời gian giữ tối thiểu của **Normal Close, SOS Close và TP** (dùng chung). TP chốt **chỉ** theo cột này. `0` = không yêu cầu; âm normalize về `0` |
| `open_max_times_tick` | `CurrentOpenMaxTimesTick` | `int` | Chặn Open Cycle dài quá N mẫu: kiểm tra sau khi Cycle đã Stable và mẫu cuối đã đạt `open_pts`, vượt thì reset Cycle. `0` = tắt |
| `close_max_times_tick` | `CurrentCloseMaxTimesTick` | `int` | Như trên cho Normal Close, SOS Close và TP. `0` = tắt |
| `open_max_last_gap_pts` | `CurrentOpenMaxLastGapPts` | `int?` | **Trần cho GAP CUỐI** của Open Cycle. So sánh **giữ nguyên dấu, đối xứng** và **strict**: Buy cần `lastGap < C`, Sell cần `lastGap > -C`. Kiểm tra sau khi Cycle đã Stable và mẫu cuối đã đạt `open_pts`; vi phạm thì **reset Cycle** (mở chu kỳ mới), không phát trigger. `NULL` (hoặc DB chưa có cột) = tắt gate; `0` và số âm **vẫn hiệu lực**, không bị clamp. Router re-check cùng trần lúc dispatch với reason `LATEST_OPEN_MAX_LAST_GAP_EXCEEDED`. Migration: `docs/OPEN-MAX-LAST-GAP-MIGRATION.sql` |
| `signal_cycle_size` | `CurrentSignalCycleSize` | `int` | **Không tham gia quyết định signal trên nhánh TIME.** Vẫn được load, validate (`>= 1`, nhỏ hơn thì ConfigService từ chối load) và ghi kèm log để đối chiếu với nhánh TICK |
| `open_price_freeze_ms` | `CurrentOpenPriceFreezeMs` | `int` | Bảo vệ độ mới của giá khi thực thi **Open**: chặn nếu giá không đổi suốt cửa sổ này. `0` = tắt kiểm tra; giá trị âm normalize về `0` |
| `close_price_freeze_ms` | `CurrentClosePriceFreezeMs` | `int` | Bảo vệ độ mới của giá khi thực thi **Close**: chặn nếu giá không đổi suốt cửa sổ này. `0` = tắt kiểm tra; giá trị âm normalize về `0` |

Hai cột price-freeze **độc lập hoàn toàn** với hold-time: mỗi cột chỉ dùng giá trị của chính
nó, không còn fallback `open_price_freeze_ms <- open_hold_confirm_ms` /
`close_price_freeze_ms <- close_hold_confirm_ms`.

**⚠️ KHÔNG chạy `docs/DROP-DEPRECATED-SIGNAL-COLUMNS.sql` trên nhánh này.**

Nhánh TIME đã **nối lại** `open_hold_confirm_ms`, `close_hold_confirm_ms`, `open_max_times_tick`,
`close_max_times_tick` vào toàn bộ pipeline — `ConfigRow` (Supabase DTO) → `ConfigRecord` →
`ConfigLoadResult` → `RuntimeConfigState` → `IRuntimeConfigProvider` →
`GapSignalConfirmationConfig`, và in ra trong log cấu hình `[DB] ...`. DROP bốn cột này sẽ khiến
mọi hold-time đọc về `0`, chu kỳ chốt ngay khi đủ `min_stable_samples`.

Script DROP chỉ dành cho nhánh TICK. Contract hiện hành được khoá bởi
`PriceFreezeConfigMappingTests.ConfigLoadResult_ExposesHoldAndTickColumns`.

**So sánh hai nhánh (A/B):** cả hai nhánh ghi cùng bộ 4 file log mỗi phiên. Khoá đối chiếu là
`confirmation_mode=` trong `[*_CYCLE]`, `[GAP_STABILITY]` và `-signal-outcome.log`:
`TIME_AND_MIN_SAMPLES` (nhánh này) vs `FIXED_SIZE` (nhánh TICK). `-signal-outcome.log` ghi kèm
cả `open_hold_confirm_ms`, `close_hold_confirm_ms` và `signal_cycle_size` để mỗi dòng tự mô tả
tham số đã dùng.

`GapSignalConfirmationConfig` vẫn còn 4 field cùng tên nhưng mặc định `0` và **không có
nguồn dữ liệu nào đổ vào**; chúng chỉ phục vụ nhánh legacy `ProcessSide` /
`ProcessLegacyGap` mà test trực tiếp gọi. Đừng nối chúng lại vào config pipeline.

Same-action range được random đúng một lần tại mỗi Auto dispatch và lưu cùng
`LastAutoDispatchAtUtc`; không random lại mỗi snapshot. Nếu `start > end`, app tự đảo.
Mỗi đầu `<= 0` fallback lần lượt về `3` và `10`. Manual/Recovery không đọc hoặc mutate range này.

```sql
comment on column public.configs.rd_start_same_action_lock_seconds is
'Số giây nhỏ nhất chờ giữa hai Auto Open cùng chiều, từ Auto Open đến Auto Close, hoặc giữa hai Auto Close liên tiếp.';

comment on column public.configs.rd_end_same_action_lock_seconds is
'Số giây lớn nhất chờ giữa hai Auto Open cùng chiều, từ Auto Open đến Auto Close, hoặc giữa hai Auto Close liên tiếp.';
```

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
4. Chu kỳ đã đủ `*_gap_min_stable_samples` mẫu **và** đủ `*_hold_confirm_ms` chưa (xem
   `count=` và `hold=` trong `[..._CYCLE][PROGRESS]`, hoặc panel Signal Cycle trên dashboard)?
   Có mẫu nào fail confirm làm reset không?
5. Mẫu cuối có đạt ngưỡng open/close không?
6. Chu kỳ có bị `[..._CYCLE][RESET]` vì lệch Tolerance / Dispersion / Drift không?
7. State flow hiện tại là gì (`WaitingOpen` hay `WaitingClose*`)?
8. Trigger -> Instruction -> Log có khớp công thức nguồn giá không?

---

## 11) Logging

### 11.1 Cấu hình `.env`

| Biến | Mô tả | Giá trị mặc định |
|------|--------|-------------------|
| `LOG_LEVEL` | Mức log tối thiểu ghi vào file. Giá trị: `DEBUG`, `INFO`, `WARN`, `ERROR` | `INFO` |
| `LOG_MAX_FILE_SIZE_MB` | Kích thước tối đa mỗi file log (MB) trước khi rotation | `50` |
| `LOG_QUEUE_CAPACITY` | Số dòng log tối đa chờ ghi; WARN/ERROR vẫn được ghi trực tiếp khi queue đầy | `50000` |

Ví dụ trong `.env`:

```env
LOG_LEVEL=INFO
LOG_MAX_FILE_SIZE_MB=50
LOG_QUEUE_CAPACITY=50000
```

### 11.2 File theo phiên và rotation

Mỗi lần bấm **Start**, `TradeSessionFileLogger` mở **4 file** trong `Desktop/trade-log/`, cùng
prefix `{yyyyMMdd_HHmmss}` để dễ ghép nhóm theo phiên. Cả 4 đóng lại khi bấm **Stop**.

| File | Nội dung | Nhịp ghi |
|------|----------|----------|
| `-trade-log.log` | Log chính: VM, router, MT4/MT5, guard, recovery, watchdog, slot | Theo sự kiện |
| `-gap-stability-raw.log` | `[GAP_STABILITY_RAW]` — dãy gap của chu kỳ đã hoàn tất | Mỗi cycle completed |
| `-signal-outcome.log` | `[SIGNAL_OUTCOME]` — gap quanh thời điểm signal OPEN / NORMAL CLOSE | Mỗi signal |
| `-gap-tick.log` | `[GAP_TICK]` — gap **mỗi tick** kèm giá thô, spread, latency | ~20 dòng/giây |

Rotation:

- Khi file log đạt ngưỡng `LOG_MAX_FILE_SIZE_MB`, hệ thống tự động tạo file mới với suffix `.001.log`, `.002.log`, ...
- Mỗi file được rotate độc lập, đếm byte riêng.
- Mỗi file mới có header continuation ghi rõ session gốc, thời điểm rotate, host name.
- File structure ví dụ:

```
Desktop/trade-log/
├── 20260424_143020-trade-log.log       (50MB, full)
├── 20260424_143020-trade-log.001.log   (50MB, full)
├── 20260424_143020-trade-log.002.log   (đang ghi)
├── 20260424_143020-gap-stability-raw.log
├── 20260424_143020-signal-outcome.log
├── 20260424_143020-gap-tick.log        (~12MB/giờ → rotate sau ~4 giờ)
└── 20260424_150000-trade-log.log       (session khác)
```

Nếu riêng file `-gap-tick.log` mở lỗi (đĩa đầy, file bị khoá), phiên **vẫn chạy đủ 3 kênh còn
lại**; main log ghi một dòng `[LOGGER][WARN] Gap tick file disabled for this session: ...`.

#### Định dạng `-gap-tick.log`

Timestamp chỉ có giờ `HH:mm:ss.fff` (ngày của phiên nằm ở dòng `Date:` trong header) và là
**thời điểm của tick**, không phải thời điểm ghi file:

```
[14:32:07.412] [GAP_TICK] gap_buy=12 gap_sell=-3 a_sym=XAUUSD a_bid=2412.35 a_ask=2412.55
  a_spread=20 a_lat=8 b_sym=XAUUSD.m b_bid=2412.67 b_ask=2412.88 b_spread=21 b_lat=11 point=100
```

Giá trị không sẵn sàng tại tick ghi là `-`. Dòng được dựng bởi
`TradeDesktop.Application/Services/GapTickLineFormatter.cs` (hàm thuần, dùng `InvariantCulture`
nên dấu thập phân luôn là dấu chấm bất kể locale máy).

### 11.3 Level filter

- Method `Log(string message)` tự suy level từ substring: `][ERROR]` → Error, `][WARN]` → Warn, `][DEBUG]` → Debug, còn lại → Info.
- Method `Log(TradeLogLevel level, string message)` dùng level truyền vào trực tiếp.
- Dòng log có level thấp hơn `LOG_LEVEL` sẽ bị bỏ qua, không ghi vào file.
- **Ngoại lệ có chủ ý — `-gap-tick.log`:** `LogGapTickRaw` KHÔNG đi qua `LogCore` nên không bị
  `LOG_LEVEL` lọc; đặt `LOG_LEVEL=WARN` vẫn ghi đủ gap. Lý do: file gap là dữ liệu phân tích,
  tắt nó theo level log sẽ tạo lỗ hổng dữ liệu im lặng. Cũng vì vậy caller phải tự cung cấp
  nguyên văn cả dòng kể cả prefix thời gian. Đây không phải bug — đừng "sửa" bằng cách nối lại
  vào `LogCore`.
- Khi queue đầy, dòng gap-tick bị drop được đếm RIÊNG và chỉ báo trong `[LOGGER][HEALTH]`
  (`dropped_gap_tick=`), không sinh `[LOGGER][WARN] Dropped ...` ra main log/panel Signal —
  tránh 20 dòng/giây làm nhiễu chỗ theo dõi lỗi giao dịch.

### 11.4 UI menu

- UI chính có nút **Open Log** nằm sau **Reconnect** để mở cửa sổ Trading Logs.
- Cửa sổ Trading Logs chia 50:50: **Minimal Signal Logs** bên trái và **System / Execution Logs** bên phải.
- System filter `All` gồm cả structured Signal Logs; filter `Signal` chỉ hiển thị Signal. Log System
  thông thường dùng màu mặc định, còn Signal giữ màu lifecycle hiện tại.
- **Log Folder** và **Current Log** nằm trong cửa sổ Trading Logs; nếu chưa Start session thì Current Log hiện thông báo.
- Chi tiết kiến trúc, phân nhóm, throttle và giới hạn tài nguyên xem
  [`docs/LOG-UI-ARCHITECTURE-2026-08-12.md`](docs/LOG-UI-ARCHITECTURE-2026-08-12.md).

### 11.5 Danh mục log hiển thị

#### Structured Signal Logs trong System

Các event `DETECTED` được ghi một lần khi phát hiện signal. Mỗi signal chỉ ghi một outcome cuối
`CONFIRMED`, `BLOCKED`, `CANCELLED` hoặc `FAILED`. `signalId` dùng để nối event phát hiện với outcome.

| Panel hiển thị | Event | Level | Mô tả tiếng Việt | Khi nào xuất hiện | Ví dụ rút gọn | Tần suất / tối ưu | Ghi file |
|---|---|---|---|---|---|---|---|
| Signal Logs | `SIGNAL_OPEN` | Info | Phát hiện tín hiệu mở vị thế mới | Open signal đủ cửa sổ xác nhận | `[SIGNAL_OPEN][INFO] signalId=... side=Buy gap=12 allGaps=10\|11\|12 ... description="Phát hiện tín hiệu mở vị thế mới"` | Một lần mỗi signal | Có |
| Signal Logs | `SIGNAL_HEDGE` | Info | Phát hiện tín hiệu đối ứng với vị thế còn tồn tại | Vị thế gần nhất là Sell và signal mới là Buy, hoặc ngược lại | `[SIGNAL_HEDGE][INFO] signalId=... newSide=Buy originalSlot=1 ... description="Phát hiện tín hiệu Buy đối ứng với vị thế Sell còn tồn tại"` | Một lần mỗi signal; chỉ là nhãn quan sát | Có |
| Signal Logs | `SIGNAL_CLOSE_TP` | Info | Phát hiện tín hiệu đóng vì đạt điều kiện TP | Close engine phát signal TP | `[SIGNAL_CLOSE_TP][INFO] signalId=... slot=2 profit=15.2 target=12 ... description="Phát hiện tín hiệu đóng vì lợi nhuận đạt điều kiện TP"` | Một lần mỗi signal | Có |
| Signal Logs | `SIGNAL_CLOSE_GAP` | Info | Phát hiện tín hiệu đóng theo Gap thông thường | Close engine phát signal Gap ở Normal mode | `[SIGNAL_CLOSE_GAP][INFO] signalId=... side=Sell gap=-4 mode=Normal ... description="Phát hiện tín hiệu đóng vị thế theo Gap thông thường"` | Một lần mỗi signal | Có |
| Signal Logs | `SIGNAL_CLOSE_SOS` | Info | Phát hiện tín hiệu đóng khẩn cấp theo SOS | Slot ở SOS mode và close signal hợp lệ | `[SIGNAL_CLOSE_SOS][INFO] signalId=... slot=2 gap=-8 mode=Sos ... description="Phát hiện tín hiệu đóng khẩn cấp theo điều kiện SOS"` | Một lần mỗi signal | Có |
| Signal Logs | `SIGNAL_OPEN_CONFIRMED` | Info | Hai chân A/B đã mở thành công | MMF xác nhận đủ ticket A và B | `[SIGNAL_OPEN_CONFIRMED][INFO] pairId=p1 slot=2 aActualPrice=... aSlippagePts=... aExecutionMs=... bActualPrice=... bSlippagePts=... bExecutionMs=... description="Hai chân A/B đã mở thành công"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_HEDGE_CONFIRMED` | Info | Hai chân của vị thế Hedge đã mở thành công | MMF xác nhận đủ hai chân Hedge | `[SIGNAL_HEDGE_CONFIRMED][INFO] pairId=p2 slot=3 aSlippagePts=... bSlippagePts=... description="Hai chân của vị thế Hedge đã mở thành công"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_CLOSE_CONFIRMED` | Info | Hai chân của vị thế đã đóng thành công | MMF/history xác nhận cả hai ticket đã đóng | `[SIGNAL_CLOSE_CONFIRMED][INFO] pairId=p1 slot=2 aActualPrice=... aSlippagePts=... aExecutionMs=... bActualPrice=... bSlippagePts=... bExecutionMs=... description="Hai chân của vị thế đã đóng thành công"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_OPEN_BLOCKED` | Warn | Có Open signal nhưng không được phép mở | Quota, cooldown, guard, gate, kết nối, HWND hoặc policy chặn | `[SIGNAL_OPEN_BLOCKED][WARN] reasonCode=QUOTA_TOTAL_FULL ... description="Không thể mở vì tổng số vị thế đã đạt giới hạn"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_HEDGE_BLOCKED` | Warn | Có Hedge signal nhưng không được phép mở | Hedge gặp cùng guard/policy như Open thường | `[SIGNAL_HEDGE_BLOCKED][WARN] reasonCode=TRADE_GATE_BLOCKED ... description="Không thể mở Hedge vì execution gate đang bị khóa"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_CLOSE_BLOCKED` | Warn | Có Close signal nhưng chưa được phép dispatch | Close gate, cooldown, min-profit, connection hoặc policy chặn | `[SIGNAL_CLOSE_BLOCKED][WARN] reasonCode=MIN_PROFIT_WAITING ... description="Chưa thể đóng vì lợi nhuận chưa đạt mức tối thiểu"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_OPEN_CANCELLED` | Warn | Open signal bị hủy trước dispatch | Signal hết hạn hoặc trạng thái mục tiêu không còn hợp lệ | `[SIGNAL_OPEN_CANCELLED][WARN] reasonCode=SIGNAL_EXPIRED ... description="Hủy tín hiệu vì đã hết hạn trong thời gian chờ"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_HEDGE_CANCELLED` | Warn | Hedge signal bị hủy trước dispatch | Vị thế gốc đã đóng, mất slot hoặc đổi side | `[SIGNAL_HEDGE_CANCELLED][WARN] reasonCode=ORIGINAL_POSITION_CLOSED ... description="Hủy Hedge vì vị thế gốc đã đóng trước khi gửi lệnh"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_CLOSE_CANCELLED` | Warn | Close signal bị hủy trước dispatch | Signal hết hạn hoặc vị thế đã đóng trước đó | `[SIGNAL_CLOSE_CANCELLED][WARN] reasonCode=POSITION_ALREADY_CLOSED ... description="Hủy đóng vì vị thế đã được đóng trước đó"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_OPEN_FAILED` | Error | Thực thi Open thất bại | Router đã dispatch ít nhất một leg và cả hai leg fail, partial-open hoặc confirmation timeout; gate/policy block không thuộc nhóm này | `[SIGNAL_OPEN_FAILED][ERROR] reasonCode=PARTIAL_OPEN rollback=pending ... description="Mở vị thế thất bại vì chỉ một chân thành công"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_HEDGE_FAILED` | Error | Thực thi Hedge thất bại | Cả hai leg fail, partial-open hoặc confirmation timeout | `[SIGNAL_HEDGE_FAILED][ERROR] reasonCode=PARTIAL_OPEN ... description="Mở vị thế Hedge thất bại vì chỉ một chân thành công"` | Một outcome cuối | Có |
| Signal Logs | `SIGNAL_CLOSE_FAILED` | Error | Thực thi Close thất bại | Cả hai leg fail, partial-close hoặc confirmation timeout | `[SIGNAL_CLOSE_FAILED][ERROR] reasonCode=PARTIAL_CLOSE retry=pending ... description="Đóng vị thế thất bại vì một chân vẫn còn mở"` | Một outcome cuối | Có |

Các `reasonCode` hiện được chuẩn hóa như sau:

| Nhóm | Reason code | Ý nghĩa |
|---|---|---|
| Capacity | `QUOTA_TOTAL_FULL`, `QUOTA_BUY_FULL`, `QUOTA_SELL_FULL`, `NO_AVAILABLE_SLOT` | Hết quota tổng/quota hướng hoặc không còn slot |
| Cycle / timer | `UNRESOLVED_PENDING_CYCLE`, `COOLDOWN_ACTIVE`, `GLOBAL_ACTION_COOLDOWN`, `POST_CLOSE_OPEN_LOCK`, `SAME_SIDE_OPEN_RANDOM_LOCK`, `OPEN_TO_CLOSE_RANDOM_LOCK`, `CLOSE_TO_CLOSE_RANDOM_LOCK`, `PER_SLOT_POST_OPEN_LOCK`, `OPPOSITE_SIDE_LOCK`, `DUPLICATE_SIGNAL`, `QUALIFYING_NOT_REACHED` | Chu kỳ trước chưa xong, timer/transition lock còn hiệu lực hoặc signal chưa đủ xác nhận |
| Execution policy | `TRADE_GATE_BLOCKED`, `TRADE_POLICY_BLOCKED`, `NON_AUTO_CLOSE_IN_FLIGHT`, `AUTO_ACTION_CONTEXT_INVALID`, `AUTO_CLOSE_SLOT_CONTEXT_INVALID`, `SIGNAL_EXPIRED` | Gate/policy từ chối, ngữ cảnh dispatch không hợp lệ hoặc signal hết hạn khi chờ |
| Market guard | `LATENCY_GUARD`, `MAX_GAP_GUARD`, `SPREAD_GUARD`, `PRICE_FREEZE_GUARD` | Dữ liệu thị trường không đạt điều kiện an toàn |
| Runtime health | `CONNECTION_UNHEALTHY`, `HWND_INVALID`, `WATCHDOG_PAUSED`, `TRADING_STOPPED`, `SIDE_DISABLED` | Kết nối/UI/runtime không cho phép thực hiện |
| Close eligibility | `MIN_PROFIT_WAITING`, `POSITION_ALREADY_CLOSED` | Chưa đạt lợi nhuận tối thiểu hoặc vị thế không còn mở |
| Hedge validity | `ORIGINAL_POSITION_CLOSED`, `ORIGINAL_SLOT_NOT_FOUND`, `ORIGINAL_SIDE_CHANGED` | Trạng thái vị thế gốc không còn hợp lệ cho Hedge |
| Execution result | `PARTIAL_OPEN`, `PARTIAL_CLOSE`, `CONFIRMATION_TIMEOUT`, `EXECUTION_FAILED` | Thực thi một phần, timeout xác nhận hoặc cả hai leg thất bại |

`BLOCKED` và `FAILED` là hai outcome loại trừ nhau. Kết quả router có `IsDispatchBlocked=true`
được ghi `*_BLOCKED` với reason cụ thể và `remainingMs`; không được suy diễn thành
`EXECUTION_FAILED` chỉ vì danh sách `Legs` rỗng. `EXECUTION_FAILED` chỉ hợp lệ khi router đã trả
về ít nhất một kết quả leg thực thi và không leg nào thành công.

Khi Auto Close đang chờ đủ xác nhận A/B, snapshot Trades có thể tạm báo `BothFlat` trước khi
History của hai sàn về đủ. Flow phải giữ trạng thái Close và không được gọi `ForceWaitingOpen`/
`ClearAllSlots` trong khoảng này. Chỉ close-finalization được phép xóa slot; nó phải giữ Auto Close
dispatch anchor để Open tiếp theo đi qua `POST_CLOSE_OPEN_LOCK`.

#### System / Execution Logs

Bảng này gom theo category; từng category có thể có nhiều message cụ thể. Panel chỉ nhận log đã vượt
`LOG_LEVEL` và được file logger chấp nhận. `All` gồm cả structured Signal; filter `Signal` chỉ xem Signal.

| Panel hiển thị | Category / event | Level thường dùng | Phân nhóm bộ lọc | Giá trị chẩn đoán | Ví dụ rút gọn | Tần suất / tối ưu UI | Ghi file |
|---|---|---|---|---|---|---|---|
| System / Execution Logs | `FLOW` | Info | Trading | Chuyển phase, open mode và position side | `[FLOW][INFO] Session start: phase=WaitingOpen openMode=None side=None` | Khi flow/session đổi trạng thái | Có |
| System / Execution Logs | `CYCLE` | Info/Warn/Error | Trading | Vòng đời open/close, pending, resolve, timeout và rollback | `[CYCLE][INFO] Pending open resolved: pairId=p1 ticketA=101 ticketB=202` | Theo transition; không được loại bỏ | Có |
| System / Execution Logs | `SLOT` | Info/Warn/Skip | Trading | Cấp slot, confirm, timeout, quota và trạng thái slot | `[SLOT][INFO] Slot 2 allocated: pairId=p1 side=Buy mode=GapBuy` | Theo transition; không được loại bỏ | Có |
| System / Execution Logs | `CLOSE_SELECT` | Info/Warn | Trading | Chọn slot/ticket đóng và lý do bỏ qua candidate | `[CLOSE_SELECT][INFO] Selected slot=2 pairId=p1 profit=15.2` | Mỗi vòng chọn có giá trị; không được loại bỏ | Có |
| System / Execution Logs | `TRADE_GATE` | Info/Blocked | Trading | Acquire/release execution gate, reason và thời gian còn khóa | `[TRADE_GATE][BLOCKED] action=OPEN reason=POST_CLOSE_OPEN_LOCK remainingMs=820` | Throttle 10 giây theo subject | Có đầy đủ |
| System / Execution Logs | `COOLDOWN` | Info/Block | Trading | Deadline cooldown và nguyên nhân bị chặn | `[COOLDOWN][BLOCK] pairId=p1 remainingMs=2500` | Throttle 30 giây theo subject | Có đầy đủ |
| System / Execution Logs | `MIN_PROFIT` / `TP_CHECK` | Info/Waiting | Trading | Điều kiện lợi nhuận/TP chưa đạt hoặc đang xác nhận | `[SLOT][TP_CHECK] slot=2 profit=8.5 target=12 state=WAITING` | Throttle 10–30 giây theo pattern | Có đầy đủ |
| System / Execution Logs | `OPPOSITE_OPEN_GUARD` | Info/Warn/Block | Trading | Kiểm tra khoảng cách giá khi Open đảo chiều | `[OPPOSITE_OPEN_GUARD][BLOCK] pairId=p2 reasonCode=DISTANCE_NOT_REACHED` | Throttle 30 giây theo pair | Có đầy đủ |
| System / Execution Logs | `TRADE_POLICY` / `GUARD` | Info/Warn | Trading | Kết quả authorization và market guard | `[TRADE_POLICY][WARN] Strategic open denied: reason=SIGNAL_EXPIRED` | Theo yêu cầu dispatch | Có |
| System / Execution Logs | `ROUTER` | Info/Warn/Error | Execution | Policy recheck, mutex và dispatch hai leg | `[ROUTER][ERROR] Open pair failed: pairId=p1 reason=MT5_TIMEOUT` | Theo mỗi dispatch | Có |
| System / Execution Logs | `MT4` | Info/Warn/Error | Execution | Native click, HWND, row/ticket và kết quả MT4 | `[MT4][INFO] Open leg successful: exchange=A action=Buy` | Theo mỗi leg | Có |
| System / Execution Logs | `MT5` | Info/Warn/Error | Execution | Manual service/native action và kết quả MT5 | `[MT5][ERROR] Close leg failed: ticket=202` | Theo mỗi leg | Có |
| System / Execution Logs | `MANUAL` | Info/Warn/Error | Execution | Kết quả thao tác manual per-pair | `[MANUAL][ERROR] Close pair FAILED: pairId=p1` | Theo thao tác người dùng | Có |
| System / Execution Logs | `HWND_PROFILE` | Info/Open | Execution | Profile HWND nguyên tử được chọn cho slot | `[HWND_PROFILE][OPEN] pairId=p1 profile=2 chartA=... tradeA=...` | Một lần khi Open | Có |
| System / Execution Logs | `RECOVERY` | Info/Warn/Error | Recovery | Restore DB/MMF, rollback partial và resync | `[RECOVERY][INFO] Recovered tickets from previous session: ticketA=101 ticketB=202` | Theo sự kiện recovery | Có |
| System / Execution Logs | `WATCHDOG` | Info/Warn/Error | Recovery | Invariant drift, pause và self-heal | `[WATCHDOG][WARN] Invariant violation detected: ... state=PAUSED` | Khi trạng thái watchdog đổi | Có |
| System / Execution Logs | `PERSIST` | Info/Warn | Recovery | Lưu `current_slots` và quota state | `[PERSIST][INFO] Saved current_slots (open-confirmed): [...]` | Khi state cần persist | Có |
| System / Execution Logs | `MARKET` | Info/Warn | Market | Latency spike, stale tick và tick resumed | `[MARKET][WARN] Latency spike: exchange=A latencyMs=620 thresholdMs=500` | Spike throttle 10 giây theo exchange | Có đầy đủ |
| System / Execution Logs | `MMF_TRADES` | Info/Warn/Error | Market | Availability/parse/read trạng thái trade map | `[MMF_TRADES][WARN] Map availability changed: map=... True -> False` | Chủ yếu khi trạng thái đổi | Có |
| System / Execution Logs | `MMF_HISTORY` | Info/Warn/Error | Market | Availability/parse/read trạng thái history map | `[MMF_HISTORY][INFO] Map parse status changed: map=... False -> True` | Chủ yếu khi trạng thái đổi | Có |
| System / Execution Logs | `CONN` | Info/Warn/Skip | Connection | Connection kill-switch và thao tác bị skip | `[CONN][WARN] Mất kết nối — chặn thao tác lệnh: ...` | `SKIP` throttle 30 giây | Có đầy đủ |
| System / Execution Logs | `HWND` | Info/Warn/Error/Skip | Connection | Health-check HWND và thao tác bị skip | `[HWND][SKIP] Bỏ qua auto-open: HWND không hợp lệ` | `SKIP` throttle 30 giây | Có đầy đủ |
| System / Execution Logs | `CONFIG` | Info/Warn/Error | Application | Load/apply/save cấu hình runtime | `[CONFIG][ERROR] Failed to load runtime config: ...` | Theo thao tác hoặc lỗi config | Có |
| System / Execution Logs | `UI` | Info/Warn | Application | Toggle, mở log/file/folder và lỗi thao tác UI | `[UI][INFO] Toggle changed: IsOpenGapBuyEnabled=True` | Theo thao tác người dùng | Có |
| System / Execution Logs | `VM` | Info/Warn/Error | Application | Lỗi orchestration hoặc exception đã suppress | `[VM][ERROR] Auto trade error: ...` | Chỉ khi có sự kiện/lỗi | Có |
| System / Execution Logs | `NOTIFY` | Info/Warn/Error | Application | Gửi Telegram notification | `[NOTIFY][WARN] Telegram send failed: ...` | Theo notification | Có |
| System / Execution Logs | `LOGGER` | Warn | Application | File queue đầy và số dòng bị drop | `[LOGGER][WARN] Dropped 120 log lines because the bounded queue was full.` | Chỉ khi queue file quá tải | Có |
| System / Execution Logs | `GENERAL` | Info | Application | Message chưa có category chuẩn hóa | `Trading logic start confirmed by user` | Nên giảm dần khi chuẩn hóa log | Có |

Lưu ý tài nguyên UI:

- `Debug` không được thêm vào collection System mặc định.
- System queue tối đa 5.000 dòng, collection tối đa 2.000 dòng, flush tối đa 150 dòng mỗi 200 ms.
- Signal collection tối đa 500 dòng.
- Throttle/filter/chặn queue chỉ tác động UI; trừ `LOG_LEVEL` và file queue policy, file log vẫn là nguồn đầy đủ.

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

Refactor từ single-slot `TradingFlowEngine` (Section 5) → multi-slot
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
| **A — Random quota Open** | `max_total_opens` là trần tổng cố định từ DB. Mỗi chu kỳ random `effectiveBuy = random(min_buy_opens..max_buy_opens)`, `effectiveSell = random(min_sell_opens..max_sell_opens)` và `X = random(2..5)`. Hai cận dưới mặc định là `1`. Chỉ pair Open confirmed đủ A/B tăng tiến độ; đủ X thì tạo chu kỳ mới. Đếm cả `PendingOpen + Live + PendingClose`. |
| **B — Auto transition gate** | Gate atomic theo action trước/action kế tiếp. Open cùng chiều, Open→Close và Close→Close random theo same-action range DB; Close→Open random theo post-close range. Open→Close đồng thời phải hết post-open riêng của slot đích. Manual/recovery không mutate state Auto. |
| **C — Opposite-side lock** | `opposite_side_lock_seconds` (default 300s): sau OPEN block OPEN opposite-side; same-side OPEN refresh timer. Đây là lớp bổ sung ngoài Auto transition gate. |
| **D — Priority close (extended)** | Khi nhiều slot trigger close cùng tick: (1) nếu `max_life_time_by_second > 0`, lọc ra các slot có tuổi `(now - OpenConfirmedAtUtc) > max_life_time_by_second` (overtime tier) → chọn profit cao nhất trong tier đó; (2) nếu không có slot nào overtime, chọn profit cao nhất trong tất cả eligible (Rule D gốc). Losers giữ window. `max_life_time_by_second = 0` (default) = disable tier, hành vi giống Rule D gốc. |
| **E — Minimum A-movement close gate** | Nếu `min_profit_to_close > 0` và tuổi slot còn dưới `max_life_time_by_second`, Auto Close chỉ được vào danh sách eligible khi trị tuyệt đối dịch chuyển chân A từ Open Price `abs(profitA) >= min_profit_to_close` point. A Buy dùng Bid hiện tại; A Sell dùng Ask hiện tại. Tại `age >= max_life_time_by_second`, gate hết hiệu lực. Nếu max lifetime bằng `0`, gate không hết hạn. Manual/recovery không đi qua gate này. |

Rule A + C check trong `CanOpenNewSlot(side, out reason)`. Rule B được pre-check trong
`ProcessSnapshot`/`CanCloseNow`, nhưng lớp bảo vệ cuối và atomic nằm trong
`TradeExecutionRouter` → `PortfolioCoordinator.TryAcquireTradeAction`.
Rule D pick trong `ProcessSnapshot` close path: overtime tier nếu có, fallback toàn bộ eligible — cả 2 đều `OrderByDescending(LastProfitSnapshot ?? double.MinValue).First()`.

#### Random quota Open

Quota Buy/Sell hiệu lực được giữ ổn định trong một chu kỳ:

```text
effective_max_buy  = random(min_buy_opens..max_buy_opens)
effective_max_sell = random(min_sell_opens..max_sell_opens)
max_total           = max_total_opens       // cố định, không random
X                   = random(2..5)           // 2, 3, 4 hoặc 5
```

- Một pair chỉ được tính vào X khi cả hai chân A/B đã Open confirmed. Signal bị chặn, dispatch thất bại, partial Open rồi rollback, Close và recovery không tăng bộ đếm.
- Quota mới thấp hơn số slot hiện tại chỉ chặn Open mới; không đóng hoặc thay đổi slot đang tồn tại. Close không chịu ảnh hưởng của quota Open.
- Đủ X thì quota Buy/Sell và X được random lại; Open thứ X đã được cấp phép theo chu kỳ cũ.
- Trạng thái quota trước đó (`previousBuy/Sell`), quota hiện tại (`effectiveBuy/Sell`), X, tiến độ và số chu kỳ được lưu cùng `current_slots`; restart tiếp tục chu kỳ cũ. Snapshot mảng legacy vẫn đọc được.
- Trading Signal hiển thị cố định `Old`, `Current`, `Total Max`, `Cycle`, `Progress` và số slot Active. Nếu Active lớn hơn Current sau reroll, UI báo `OVER TARGET`; lệnh cũ vẫn giữ nguyên và chỉ Open mới bị chặn.
- Reload DB không reroll: nếu cận trên Buy/Sell giảm thì quota hiệu lực được clamp; `max_total_opens` mới áp dụng ngay.

Ma trận Auto transition:

| # | Lệnh vừa thực hiện | Hành động tiếp theo | Thời gian chờ | Kết quả |
|---:|---|---|---|---|
| 1 | Open Buy | Open Buy | Random same-action range DB | Được xét mở thêm Buy |
| 2 | Open Buy | Open Sell | Opposite-side lock hiện tại | Được xét mở Sell sau khi hết khóa ngược chiều |
| 3 | Open Buy | Close Buy | Random same-action từ Open gần nhất + post-open của slot cần đóng | Được xét Close Buy khi cả hai khóa đã hết |
| 4 | Open Buy | Close Sell | Random same-action từ Open gần nhất + post-open của slot cần đóng | Được xét Close Sell khi cả hai khóa đã hết |
| 5 | Open Sell | Open Sell | Random same-action range DB | Được xét mở thêm Sell |
| 6 | Open Sell | Open Buy | Opposite-side lock hiện tại | Được xét mở Buy sau khi hết khóa ngược chiều |
| 7 | Open Sell | Close Sell | Random same-action từ Open gần nhất + post-open của slot cần đóng | Được xét Close Sell khi cả hai khóa đã hết |
| 8 | Open Sell | Close Buy | Random same-action từ Open gần nhất + post-open của slot cần đóng | Được xét Close Buy khi cả hai khóa đã hết |
| 9 | Close Buy | Close Buy | Random same-action range DB | Được xét đóng slot Buy tiếp theo |
| 10 | Close Buy | Close Sell | Random same-action range DB | Được xét đóng slot Sell tiếp theo |
| 11 | Close Buy | Open Sell | Random post-close của Auto Close | Được xét Open Sell sau cooldown |
| 12 | Close Buy | Open Buy | Random post-close của Auto Close | Được xét Open Buy sau cooldown |
| 13 | Close Sell | Close Sell | Random same-action range DB | Được xét đóng slot Sell tiếp theo |
| 14 | Close Sell | Close Buy | Random same-action range DB | Được xét đóng slot Buy tiếp theo |
| 15 | Close Sell | Open Buy | Random post-close của Auto Close | Được xét Open Buy sau cooldown |
| 16 | Close Sell | Open Sell | Random post-close của Auto Close | Được xét Open Sell sau cooldown |
| 17 | Manual Close | Auto Open Buy/Sell | Đến khi MMF xác nhận hoàn tất | Auto pause; không tạo cooldown |
| 18 | Manual Close | Auto Close Buy/Sell | Đến khi MMF xác nhận hoàn tất | Auto pause; không tạo cooldown |
| 19 | Recovery Close | Auto Open Buy/Sell | Đến khi MMF xác nhận hoàn tất | Auto pause; không tạo cooldown |
| 20 | Recovery Close | Auto Close Buy/Sell | Đến khi MMF xác nhận hoàn tất | Auto pause; không tạo cooldown |

#### Phạm vi và cách kết hợp các lock

Các lock không phải những khoảng thời gian nối tiếp để cộng tổng. Một action chỉ được dispatch khi
**tất cả** guard áp dụng cho action đó đều pass; vì vậy thời điểm được phép là deadline muộn nhất
(`max` các deadline), không phải tổng số giây.

| Action đang xét | Guard có thể cùng áp dụng | Phạm vi/semantics thực tế |
|---|---|---|
| Open cùng chiều với Auto Open gần nhất | Same-action lock | Chặn transition Open→Open cùng side |
| Open ngược chiều | Opposite-side lock + opposite-open price guard | Phải hết time lock **và** đạt khoảng cách giá; hai điều kiện độc lập |
| Auto Close slot X sau Auto Open gần nhất | Same-action lock toàn cục + holding floor + post-open riêng của X | Phải pass tất cả; Open slot Y tạo lại same-action deadline nhưng không refresh post-open của X |
| Auto Close sau một Auto Close khác | Same-action lock + post-open riêng của slot sắp đóng | Phải pass cả hai; không cộng duration |
| Auto Open sau Auto Close | Post-close từ dispatch + post-close từ confirm | Cùng một duration; confirm anchor thường muộn hơn nên chi phối |
| SOS Close trong global startup/recovery cooldown | Bypass global cooldown cũ + post-open/transition/close guards | Chỉ bypass global timer; khi dispatch sẽ trở thành Auto Close mới và tạo lại same-action/post-close |
| Manual/Recovery Close | Non-auto barrier + physical dispatch mutex | Bypass Auto timers; tạm pause Auto đến khi MMF xác nhận hoàn tất |

Post-open được kiểm tra ở nhiều lớp nhưng dùng đúng một deadline
`slot.OpenConfirmedAtUtc + slot.SelectedPostOpenLockSeconds`:

1. `IsHoldingElapsedOrFloorReached` dùng `max(HoldingSeconds, SelectedPostOpenLockSeconds)`.
2. `IsPostOpenCloseLockElapsed` loại slot khỏi close scan khi chưa hết post-open.
3. `EvaluateAutoTransition` re-check ngay trước dispatch sau physical mutex.

Ba bước trên là defense-in-depth để tránh signal/race dispatch, không tạo ba khoảng chờ. Post-open chỉ
chặn **Auto Close của chính slot**; khóa Open→Close bằng `rd_same` chặn thêm mọi Auto Close sau Auto
Open gần nhất. Các khóa này không áp dụng cho Manual Close, Recovery Close hoặc rollback leg mở dở.

Post-close được chọn một lần tại Auto Close dispatch. Transition gate bảo vệ Close→Open từ mốc dispatch;
sau khi MMF confirm Close, `CanOpenNewSlot` dùng lại cùng duration từ `LastCloseConfirmedAtUtc`. Hai mốc
không cộng nhau: Auto Open được phép khi cả hai đã hết, thông thường tương đương
`LastCloseConfirmedAtUtc + selectedPostCloseLockSeconds`.

### Opposite-open price guard trên sàn A

DB column `opposite_open_min_distance_pts` là khoảng cách tối thiểu để Auto Open đảo chiều;
`0` nghĩa là tắt guard. Guard chỉ dùng các ticket A thuộc slot Auto đang `Live` hoặc
`PendingClose`, không tính external/manual trade và không tính `PendingOpen` chưa có giá MMF.

- Lệnh A gần nhất Buy, chuẩn bị Open Sell:
  `Abs((Current A.Bid - Average Buy Open Price A) × Point) >= configured points`.
- Lệnh A gần nhất Sell, chuẩn bị Open Buy:
  `Abs((Average Sell Open Price A - Current A.Ask) × Point) >= configured points`.
- Giá trung bình là trung bình cộng giá mở MMF của các lệnh A còn hoạt động cùng chiều với
  lệnh gần nhất. Guard dùng trị tuyệt đối nên giá di chuyển đủ khoảng cách theo một trong hai
  hướng đều đạt điều kiện; `distancePts` trong log vẫn dùng giá trị có dấu và cách làm tròn gốc
  để thể hiện hướng biến động.
- Lệnh đầu tiên và Open cùng chiều bỏ qua guard. Thiếu giá MMF/Bid/Ask thì block fail-safe.
- Kiểm tra lần đầu tại ViewModel và re-check sau physical dispatch mutex trong router. Nếu
  re-check fail, pending request và `PendingOpen` slot được rollback, không giữ quota.
- Log `[OPPOSITE_OPEN_GUARD]` có `reasonCode` và mô tả tiếng Việt. `BLOCK`/`SKIP` chỉ log khi
  signature thay đổi hoặc sau 30 giây; `PASS` chỉ log khi chuẩn bị dispatch.

DB comment đề xuất:

```sql
comment on column public.configs.opposite_open_min_distance_pts is
'Khoảng cách giá tối thiểu trên sàn A để cho phép Auto Open đảo chiều.';
```

Phương án B: giá trị random trong post-open range tính từ `OpenConfirmedAtUtc` của chính slot đang
được xét close. Open slot mới không kéo dài thời gian chờ của slot cũ. Random được sinh đúng
một lần khi MMF xác nhận Open và lưu trên slot, không random lại mỗi snapshot.

Post-close được random một lần khi Auto Close được dispatch và lưu trên slot đóng. Transition
Close→Open dùng deadline từ dispatch; sau khi MMF xác nhận Close, re-entry guard tiếp tục dùng
cùng số giây đó tính từ `CloseConfirmedAtUtc`. Manual/Recovery Close không random, không cập
nhật anchor và không tạo Auto cooldown.

Quy tắc chuẩn hóa range:

- Nếu `start > end`, ứng dụng tự đổi thứ tự trước khi random.
- Post-open cho phép `0..0` để không tạo post-open lock.
- Mỗi đầu post-close `<= 0` được thay bằng fallback an toàn `300` giây.
- Random bao gồm cả giá trị `start` và `end`.

Mô tả ngắn dùng cho comment cột DB:

| Cột | Mô tả |
|---|---|
| `rd_start_post_open_lock_seconds` | Số giây nhỏ nhất chờ sau Auto Open trước khi được Auto Close. |
| `rd_end_post_open_lock_seconds` | Số giây lớn nhất chờ sau Auto Open trước khi được Auto Close. |
| `rd_start_post_close_lock_seconds` | Số giây nhỏ nhất chờ sau Auto Close trước khi được Auto Open. |
| `rd_end_post_close_lock_seconds` | Số giây lớn nhất chờ sau Auto Close trước khi được Auto Open. |

Transition gate được acquire sau physical mutex và policy revalidation, trước khi router gửi leg.
Vì vậy hai thao tác trên hai slot khác nhau không thể cùng commit transition/native dispatch.

### 13.4 Quota và config runtime

Fallback runtime hiện tại là `MaxTotalOpens=5, MaxBuy=3, MaxSell=3`. `PortfolioState` có giá trị
khởi tạo nội bộ `1/1/1`, nhưng `DashboardViewModel.SyncPortfolioCoordinatorConfig()` đẩy runtime
fallback/DB config xuống coordinator ngay khi khởi tạo và sau mỗi lần reload config.

ViewModel push config xuống coordinator qua `SyncPortfolioCoordinatorConfig()`:
- Gọi từ constructor + sau mỗi `ApplyRuntimeConfig()`.
- Map `RuntimeConfigState.CurrentMaxTotal/Buy/SellOpens` → `coordinator.UpdateQuotaConfig`.
- `start_wait_time/end_wait_time` đã được xóa; coordinator không nhận hai cấu hình legacy này.
- Map hai đầu post-open range → `coordinator.UpdatePostOpenLockConfig(start, end)`.
- Map hai đầu post-close range → `coordinator.UpdatePostCloseLockConfig(start, end)`.
- Map hai đầu same-action range → `coordinator.UpdateSameActionLockConfig(start, end)`.
- Map `CurrentMaxLifeTimeBySecond` (từ DB column `max_life_time_by_second`) → `coordinator.UpdateMaxLifeTimeConfig`.

### 13.5 ProcessSnapshot flow (mỗi tick 50ms)

```
1. Cache lastSeen* hold range cho close-gate fallback.
2. Resolve effectiveNow với wall-clock tolerance.
3. Check non-auto barrier → return Empty nếu active. Nếu startup/recovery cooldown active thì chỉ tiếp tục close scan cho slot đang thỏa SOS; close thường và Open vẫn bị chặn.
4. OPEN path:
   - Quota A allow → openSignalEngine.ProcessSnapshot → trigger.
   - Loop triggers, skip nếu CanOpenNewSlot fail (logs skip reason).
   - Return PortfolioSnapshotResult(OpenTrigger: trigger).
5. CLOSE path (nếu open path không return):
   - Loop Live slots, skip slot IsCloseExecutionPending hoặc holding chưa elapsed.
   - Per-slot slot.CloseSignalEngine.ProcessSnapshot → eligibleCloses.
   - Pick winner (Rule D extended):
     - Nếu `MaxLifeTimeBySecond > 0`: lọc overtime slots (`now - OpenConfirmedAtUtc > maxLifeTimeSec`) → nếu có, chọn slot già nhất; profit chỉ tie-break khi cùng tuổi.
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
- **Layer 5 — Physical mutex + transition gate**: mọi `OpenPairAsync`/`ClosePairAsync` qua
  `_physicalDispatchGate`, revalidate policy/MMF rồi acquire `TryAcquireTradeAction` atomically.
  Hai chân A/B trong một pair là một action.

### 13.7 Persistence và recovery

Đã triển khai:
- `SlotPersistence.Serialize(liveSlots) : string` — JSON cho DB JSONB column.
- `SlotPersistence.Deserialize(json) : IReadOnlyList<RecoveredSlotData>`.
- `coordinator.RecoverSlotsFromPersisted(slots)`: restore slots, set next SlotId, restore LastOpenSide và áp startup cooldown nếu legacy global cooldown range > 0.
- `SupabaseConfigRepository` load/save `current_slots`.
- ViewModel persist khi Open confirm, Close finalize, manual resync và watchdog self-heal.
- Startup recovery verify ticket A/B với MMF, loại snapshot stale và persist lại; fallback legacy
  `current_tick_a/current_tick_b` vẫn được hỗ trợ.

### 13.8 Close routing (Phase 4)

`SelectCloseCandidateForTicket(targetTicket, exchange, mapName, hwnd)` — ticket-precise
RowIndex lookup. KHÔNG fallback row 0 nếu ticket missing. Đảm bảo close đúng slot trong
multi-slot mode (cap>1). `AutoCloseOrderAsync(trigger, targetSlot)` nhận slot từ
`DispatchCloseTriggerAsync` → ticket-precise selection.

Legacy `SelectCloseCandidateForExchange` (first-tool-opened) còn `[Obsolete]` cho một số fallback
manual/recovery; strategic multi-slot close bắt buộc lookup đúng ticket và không fallback row 0.

#### 13.8.1 HWND profile theo slot

Mỗi phần tử trong `manualHwndColumns` là một profile nguyên tử:

```text
profile N = chartA(N) + chartB(N) + tradeA(N) + tradeB(N)
```

Khi Open, ứng dụng random đúng một profile và chụp toàn bộ bốn HWND vào `PositionSlot`/mapping
`pairId`. Hai chart HWND của profile được dùng để Open; hai trade HWND của chính profile đó được
dùng cho mọi đường Close của slot. Invariant bắt buộc:

```text
c1 -> t1
c2 -> t2
c3 -> t3
```

Không được ghép chéo như `c1 -> t2`. Auto Close, Manual Close theo pair, legacy Close đã resolve
được ticket, pending-close retry, partial-open rollback và external-partial recovery đều phải resolve
Trade HWND từ slot/profile mở. Việc reload hoặc sửa config không đổi profile của slot đang chạy.

`current_slots` persist thêm `hwndProfileIndex`, `chartHwndA/B` và `tradeHwndA/B`; vì vậy slot mới
giữ đúng mapping qua restart. Snapshot legacy tạo trước khi có các field này vẫn deserialize được,
nhưng không thể suy ra profile từng dùng để Open. Với slot legacy, runtime fallback về Trade HWND
config hiện tại. Nếu mọi dòng có cùng `tradeA/tradeB` thì fallback này tương đương cho tất cả profile.

### 13.9 Monitoring (Phase 7)

`coordinator.GetMetrics() : PortfolioMetrics`:
- Current live/pending counts (Buy/Sell/total/pendingOpen/pendingClose).
- Monotonic counters: `TotalOpensAllTime`, `TotalClosesAllTime`.
- Skip counters: `QuotaSkipCount`, `OppositeLockSkipCount`, `CooldownSkipCount`.

### 13.10 Trade execution authorization

Mọi request gửi đến `TradeExecutionRouter` bắt buộc có `TradeExecutionContext` với
`RequestId`, `TradeExecutionReason`, `Source`, `PairId/SlotId` tương ứng.

Các reason hiện hành:

| Nhóm | Reason | Policy |
|------|--------|--------|
| Strategic | `StrategicOpen` | Bắt buộc signal OPEN còn hạn, chiều A/B khớp signal và latest gap vẫn đạt ngưỡng. |
| Strategic | `StrategicClose` | Bắt buộc signal CLOSE còn hạn, đúng pair/slot/ticket; latest gap hoặc TP vẫn hợp lệ. |
| Manual legacy | `ManualOpen`, `ManualClose` | Bị chặn bởi policy với `MANUAL_WITHOUT_SIGNAL_DISABLED`. |
| Manual per-pair | `ManualPairClose` | Không cần signal; bắt buộc source per-pair, manual ownership, đúng pair/slot và đủ hai ticket khớp slot. |
| Recovery | `OpenPartialRollback` | Miễn signal; bắt buộc pair/slot, đúng một ticket và evidence rollback. |
| Recovery | `ExternalPartialCloseRecovery` | Miễn signal; bắt buộc ticket chân còn lại và evidence partial state. |
| Recovery | `PendingCloseRetry` | Miễn signal; bắt buộc ticket pending và evidence retry. |

#### 13.10.1 Close ownership contract

OPEN pair chỉ do auto signal khởi phát. Ba nguồn CLOSE hợp lệ là:

1. Recovery khi OPEN lệch một leg hoặc external partial close.
2. Auto close khi `CloseSignalEngine` phát tín hiệu hợp lệ.
3. Manual per-pair khi người dùng nhấn nút Close.

Mỗi slot lưu `CloseExecutionOwner` (`None`, `Auto`, `Manual`, `Recovery`). Một `pairId`
chỉ được một flow sở hữu tại một thời điểm. Manual/recovery trên một pair không được reset
close engine hoặc active auto cycle của pair khác; auto và manual cũng không được dispatch
trùng cùng một pair.

Partial OPEN luôn ở `PendingOpen` nên không thuộc tập auto-close. Auto/manual claim thành công
chuyển slot sang `PendingClose`. Nếu request bị block trước dispatch thì release claim và trả
slot về `Live`; nếu dispatch đã bắt đầu hoặc còn một leg chưa đóng thì giữ pending/retry cho tới
khi MMF xác nhận ticket đã biến mất. Chỉ lúc đó mới confirm và remove slot.

Các flow tách biệt ở tầng quyết định nhưng dùng chung execution safety: mutex close vật lý,
ticket/row validation và pending retry. Manual/recovery bypass và không mutate auto cooldown;
chúng tạo non-auto barrier để tạm dừng auto và disable các nút Close khác cho đến khi MMF confirm,
sau đó auto resume ngay với latest signal/market condition.

Router dùng một physical dispatch mutex chung cho cả OPEN và CLOSE. Sau khi lấy mutex,
router bắt buộc kiểm tra lại policy/signal và tìm lại `RowIndex` từ MMF theo đúng `Ticket`
ngay trước native click. Nếu map lỗi hoặc ticket đã biến mất thì hủy close; tuyệt đối không
fallback về row 0 hay dùng row index cũ đã chọn trước lúc chờ mutex.

Contract này được khóa bằng `ManualPairClosePolicyTests` và các ownership/race tests trong
`TradeDesktop.Tests/Portfolio/PortfolioCoordinatorTests.cs`. Mọi thay đổi execution policy hoặc
slot lifecycle phải chạy lại các test này.

Strategic authorization:

- Signal max age hiện tại: **1.000 ms** (`SignalDispatchMaxAgeMs`, code constant).
- Signal chỉ được consume một lần; nếu global gate đang khóa thì reservation được release,
  tick sau phải tạo/đánh giá signal lại.
- Router tái kiểm tra snapshot mới nhất: connected, không stale quá 10 giây, latency/spread
  không vượt config, gap/TP vẫn hợp lệ.
- OPEN kiểm tra cặp leg: `GapBuy = A Buy + B Sell`, `GapSell = A Sell + B Buy`.
- CLOSE kiểm tra ticket request khớp ticket của đúng `PositionSlot`.

Thứ tự enforcement:

```
Validate execution reason/context
→ validate current signal hoặc recovery evidence
→ reserve signal (strategic)
→ acquire physical dispatch mutex
→ revalidate policy/signal/MMF target
→ atomic acquire Auto transition gate hoặc non-auto bypass
→ dispatch A/B
```

Policy/transition-gate block trả `ManualTradeResult.IsDispatchBlocked = true`. ViewModel phải
rollback slot state và xóa pending MMF/history match request; không queue request cũ để chạy
sau khi signal đã hết hiệu lực.

Log audit:

- `[TRADE_POLICY][ALLOWED]`: request/reason/source/pair/slot/signal age/recovery ticket.
- `[TRADE_POLICY][BLOCKED]`: cùng context và block code cụ thể.
- `[TRADE_GATE][ACQUIRED|BLOCKED]`: trạng thái serialization toàn cục.

### 13.11 Acceptance invariants

Hệ thống luôn phải thỏa các invariants (verified qua integration tests):

1. **Quota**: `LiveBuyCount ≤ MaxBuyOpens ∧ LiveSellCount ≤ MaxSellOpens ∧ LiveAndPendingTotalCount ≤ MaxTotalOpens`.
2. **Auto transition gate**: Open cùng chiều, Open→Close và Close→Close chờ random theo same-action range DB;
   Close→Open chờ giá trị post-close đã random; mỗi slot chỉ được Close khi
   `now ≥ slot.OpenConfirmedAtUtc + selectedPostOpenLockSeconds`. Opposite Open dùng opposite lock.
   Manual/recovery không mutate transition state.
3. **Opposite-lock**: nếu OPEN side X tại `T`, không OPEN side ¬X trong `[T, T+300s]` (CLOSE bypass).
4. **Close priority**: slot được close phải có `LastProfitSnapshot ≥` mọi slot khác trong eligibleCloses.
5. **Slot isolation**: mỗi slot có CloseSignalEngine riêng, không share window state.
6. **Recovery/manual**: bypass auto cooldown nhưng tạo non-auto barrier; auto bị chặn và các nút
   Close bị disable đến khi MMF xác nhận pair cân bằng/flat, sau đó resume ngay.
7. **No quota leak**: abort path (timeout / failure) phải release slot khỏi coordinator.
8. **Strategic authorization**: auto OPEN/CLOSE không được dispatch nếu signal hết hạn,
   latest condition không còn hợp lệ hoặc pair/slot/ticket không khớp.
9. **Recovery authorization**: close không signal chỉ hợp lệ với recovery reason + evidence
   + đúng một ticket.

### 13.12 Trạng thái hiện tại

Multi-slot, DB quota, `current_slots` persistence/recovery, transition matrix, manual/recovery
barrier, ticket-precise close và watchdog self-heal đều đã được triển khai. Fallback runtime là
`5/3/3`; giá trị production thực tế do DB config quyết định.

Audit 2026-08-07 phát hiện Gap Close đang chọn nhầm collection khi `gapBuy` và `gapSell` khác nhau;
đây là finding code chưa sửa. Xem `docs/audits/SYSTEM-AUDIT-2026-08-07.md`.
