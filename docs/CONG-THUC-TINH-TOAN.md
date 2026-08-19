# Công thức tính toán trong TradeDesktop

> Tài liệu liệt kê **toàn bộ công thức tính toán đang được dùng** trong dự án (auto-trading WPF .NET 8, multi-slot).
> Mỗi công thức kèm vị trí code chính xác (`file:line`) để tra cứu / debug / onboard.
> Mọi biểu thức ở đây được trích nguyên văn từ source — không diễn giải lại.
>
> Quy ước chung trong app: **`Point` (point multiplier)** = `CurrentPoint > 0 ? CurrentPoint : 1`.

---

## 1. Công thức GAP (lõi tín hiệu)

**File:** [GapCalculator.cs](../TradeDesktop.Application/Services/GapCalculator.cs#L13-L37)

```csharp
var pointMultiplier = runtimeConfigProvider.CurrentPoint > 0
    ? runtimeConfigProvider.CurrentPoint : 1;

// GapBuy  (L22-27)
var bBidPts = (int)(sanB.Bid.Value * pointMultiplier);
var aAskPts = (int)(sanA.Ask.Value * pointMultiplier);
gapBuy = bBidPts - aAskPts;

// GapSell (L29-34)
var bAskPts = (int)(sanB.Ask.Value * pointMultiplier);
var aBidPts = (int)(sanA.Bid.Value * pointMultiplier);
gapSell = bAskPts - aBidPts;
```

| Công thức | Biểu thức (đúng theo code) | Đơn vị |
|---|---|---|
| **GapBuy** | `(int)(B.Bid × Point) − (int)(A.Ask × Point)` | points (int) |
| **GapSell** | `(int)(B.Ask × Point) − (int)(A.Bid × Point)` | points (int) |

- `B` = sàn B, `A` = sàn A. `Bid`/`Ask` là giá realtime (decimal).
- GapBuy dương = cơ hội mua (mua B / bán A). GapSell âm = cơ hội bán.
- Điều kiện: cả 2 giá phải `HasValue`, nếu không gap = `null`.

> ⚠️ **Lưu ý quan trọng (khác với cách viết thông thường):** code **ép `int` từng vế TRƯỚC khi trừ**
> (`(int)(B.Bid×Point) − (int)(A.Ask×Point)`), **KHÔNG** phải `(int)((B.Bid − A.Ask) × Point)`.
> Về ý nghĩa hai cách giống nhau, nhưng cách làm tròn từng vế có thể lệch 1 point ở biên — đây là
> hành vi thực tế của hệ thống.

---

## 2. Tín hiệu OPEN (mở lệnh)

**File:** [GapSignalConfirmationEngine.cs](../TradeDesktop.Application/Services/GapSignalConfirmationEngine.cs) — `ProcessSide` (L78-173)

Một tín hiệu OPEN chỉ được trigger khi **tất cả** điều kiện sau thỏa (theo thứ tự):

| # | Điều kiện | Biểu thức | Line |
|---|---|---|---|
| 1 | Gap hiện tại đạt ngưỡng confirm | Buy: `gap ≥ ConfirmGapPts` · Sell: `gap ≤ −ConfirmGapPts` | L37-38, L61-62, L98 |
| 2 | Không vượt giới hạn gap | `LimitMaxGap = 0` HOẶC `|gap| ≤ LimitMaxGap` | L104-108 |
| 3 | Đủ thời gian hold | `elapsedMs ≥ HoldConfirmMs` | L124-128 |
| 4 | **Mọi** tick trong cửa sổ đều đạt confirm | `primaryGaps.All(isConfirmSatisfied)` | L130-135 |
| 5 | Tick cuối đạt ngưỡng open | Buy: `LastGap ≥ OpenPts` · Sell: `LastGap ≤ −OpenPts` | L137-142 |
| 6 | Không vượt số tick tối đa | `OpenMaxTimesTick = 0` HOẶC `count ≤ OpenMaxTimesTick` | L144-149 |

- `ConfirmGapPts`, `OpenPts` được chuẩn hóa `Math.Abs(...)` (L15-16).
- `HoldConfirmMs` chuẩn hóa `Math.Max(0, ...)` (L17).
- `OpenPts` thường lớn hơn `ConfirmGapPts` (confirm để vào cửa sổ, open để chốt tại tick cuối).
- Sell dùng đối xứng âm vì GapSell âm khi có cơ hội bán.

### 2.1 Random quota sau tín hiệu OPEN

Quota không tạo tín hiệu; đây là cổng cấp phép sau khi đã có Open Signal hợp lệ:

```text
EffectiveMaxBuy  = random integer [1, MaxBuyOpens]
EffectiveMaxSell = random integer [1, MaxSellOpens]
RandomAfterOpens = random integer [2, 5]
MaxTotal         = MaxTotalOpens  // cố định

CanOpenBuy  = CurrentBuy  < EffectiveMaxBuy  && CurrentTotal < MaxTotal
CanOpenSell = CurrentSell < EffectiveMaxSell && CurrentTotal < MaxTotal
```

`CurrentBuy`, `CurrentSell` và `CurrentTotal` tính `PendingOpen + Live + PendingClose`.
Bộ đếm chỉ tăng khi pair chuyển `PendingOpen → Live` sau khi đủ ticket A/B. Khi
`OpenCountSinceRandom >= RandomAfterOpens`, hệ thống random chu kỳ kế tiếp và reset
bộ đếm về 0. Quota giảm không làm đóng lệnh hiện tại. Trạng thái chu kỳ được persist
cùng `current_slots` để restart không random ngoài ý muốn.

---

## 3. Tín hiệu CLOSE (đóng lệnh)

**File:** [CloseSignalEngine.cs](../TradeDesktop.Application/Services/CloseSignalEngine.cs)

Có **2 đường đóng độc lập**, ưu tiên TP trước (L67-69):
```csharp
var tpResult = ProcessTp(...);
return tpResult ?? gapResult;   // TP close ưu tiên hơn Gap close
```

### 3.1 Close theo GAP đảo chiều (L22-65)

Dùng lại `ProcessSide` (giống mục 2) nhưng watch gap **ngược chiều** với vị thế đang giữ:

| Đang giữ | Watch gap | Confirm | Open (tick cuối) |
|---|---|---|---|
| **GapBuy** (lệnh mua) | `GapSell` | `GapSell ≤ −CloseConfirmGapPts` | `LastGapSell ≤ −ClosePts` |
| **GapSell** (lệnh bán) | `GapBuy` | `GapBuy ≥ CloseConfirmGapPts` | `LastGapBuy ≥ ClosePts` |

Vẫn áp dụng đủ hold-window + all-ticks + `CloseMaxTimesTick` + `LimitMaxGap` như OPEN.

### 3.2 Close theo TP (Take-Profit) — `ProcessTp` (L79-183)

Quyết định trên **profit của slot** (`slotProfit`), không phải gap:

| # | Điều kiện | Biểu thức | Line |
|---|---|---|---|
| 0 | Phải có openMode & profit | `openMode ≠ None && slotProfit.HasValue` | L85-89 |
| 1 | Target hợp lệ | `|CloseTpProfit| > 0` | L92-97 |
| 2 | Profit đạt ngưỡng confirm | `currentProfit ≥ CloseConfirmTpProfit` | L100-104 |
| 3 | Không vượt limit (chống spike) | `LimitMaxTp = 0` HOẶC `currentProfit ≤ LimitMaxTp` | L106-111 |
| 4 | Đủ thời gian hold | `elapsedMs ≥ CloseHoldConfirmMs` | L122-126 |
| 5 | **Mọi** mẫu profit đạt confirm | `Profits.All(v ≥ CloseConfirmTpProfit)` | L128-132 |
| 6 | Profit cuối đạt target | `LastProfit ≥ CloseTpProfit` | L134-139 |
| 7 | Profit cuối không vượt trần | `CloseMaxTpProfit = 0` HOẶC `LastProfit ≤ CloseMaxTpProfit` | L141-145 |
| 8 | Không vượt số tick tối đa | `CloseMaxTimesTick = 0` HOẶC `count ≤ CloseMaxTimesTick` | L147-152 |

- Mọi ngưỡng TP chuẩn hóa `Math.Abs(...)`: `CloseConfirmTpProfit`, `CloseTpProfit`, `CloseMaxTpProfit`, `LimitMaxTp`.

---

## 4. Bốn lớp Guard vào lệnh (lọc sau khi signal fire)

**File:** [SignalEntryGuard.cs](../TradeDesktop.Application/Services/SignalEntryGuard.cs) — `Check` (L56-85)

Thứ tự: **Latency → MaxGap → Spread → PriceFreeze → (TpFreeze)**. Gặp fail đầu tiên là reject ngay.
**Quy ước: ngưỡng = 0 (hoặc ≤ 0) ⇒ guard tắt.**

| Guard | Điều kiện REJECT | Biểu thức | Line |
|---|---|---|---|
| **Latency** | Độ trễ sàn vượt ngưỡng | `latA > ConfirmLatencyMs` hoặc `latB > ...` | L87-104 |
| **MaxGap** | Gap quá lớn | `|LastGap| > MaxGap` | L106-138 |
| **Spread** | Spread quá rộng | `(int)(Spread × Point) > MaxSpread` (mỗi sàn) | L140-166 |
| **PriceFreeze** | Feed một sàn đứng yên đủ cửa sổ | lịch sử bao phủ đủ `PriceFreezeMs`, ≥ 3 tick và Bid/Ask cùng sàn đều không đổi | `SignalEntryGuard.CheckPriceFreeze` |
| **TpFreeze** | (chỉ close TP) profit đi ngang | mọi profit làm tròn `"0.00"` bằng nhau | L209-222 |

- PriceFreeze cần ≥ 3 tick và `observedMs >= PriceFreezeMs`; chỉ một Bid hoặc Ask đứng yên không đủ
  reject. Close SOS (`CloseGapMode.Sos`) bỏ qua guard này để ưu tiên thoát vị thế.
- `freeze_last_n` của Open chỉ reject khi N gap cuối bằng nhau, hai giá nguồn tạo gap cùng đứng yên
  **và** thời gian từ mẫu đầu đến mẫu cuối đạt `PriceFreezeMs`; giá nguồn vẫn thay đổi hoặc chưa đủ
  thời gian thì cho qua dù gap point sau làm tròn bằng nhau.
- TpFreeze chỉ áp dụng khi `CloseReason == Tp` và có ≥ 2 mẫu profit; so theo chuỗi `"0.00"` (khớp giá trị log).
- Sliding window giá giữ tối đa **60.000 ms** (`PriceHistoryCapacityMs`, L15).

---

## 5. Công thức PROFIT (lời/lỗ)

### 5.1 Trade đang mở — realtime mỗi tick
**File:** [DashboardViewModel.cs:5762-5794](../TradeDesktop.App/ViewModels/DashboardViewModel.cs#L5762-L5794) — `CalculateTradeProfit`

| Loại | Biểu thức | Line |
|---|---|---|
| **BUY** (`TradeType == 0`) | `(CurrentBid − OpenPrice) × Point` | L5785 |
| **SELL** (`TradeType == 1`) | `(OpenPrice − CurrentAsk) × Point` | L5793 |

- `pointValue = Math.Max(1, point)`. Thiếu Bid/Ask ⇒ trả `0`.
- Được refresh **mỗi tick** (`RefreshSlotProfitsEveryTick`) và feed vào `coordinator.UpdateProfit` nuôi quyết định TP/priority-close — **không** throttle theo UI.

### 5.2 Trade đã đóng — lịch sử
**File:** [DashboardViewModel.cs:5796-5799](../TradeDesktop.App/ViewModels/DashboardViewModel.cs#L5796-L5799) — `CalculateHistoryProfit`

| Loại | Biểu thức |
|---|---|
| **BUY** | `(ClosePrice − OpenPrice) × 100` |
| **SELL** | `(OpenPrice − ClosePrice) × 100` |

> Lưu ý: history dùng **hằng số ×100** cố định, KHÔNG dùng `Point` hiện tại.

### 5.3 Profit của slot (gộp 2 leg)
**File:** [PositionSlot.cs:120-138](../TradeDesktop.Application/Services/Portfolio/PositionSlot.cs#L120-L138) — `UpdateProfit`

```
LastProfitSnapshot = (đủ cả 2 leg) ? LastProfitA + LastProfitB : null
```
- `HasCompleteProfitSnapshot = LastProfitA.HasValue && LastProfitB.HasValue`. Thiếu 1 leg ⇒ snapshot `null`.

---

## 6. Công thức SPREAD & SLIPPAGE

### 6.1 Spread
| Công thức | Biểu thức | File:line |
|---|---|---|
| Spread thô | `Ask − Bid` | [DashboardMetricsMapper.cs:48-49](../TradeDesktop.Application/Services/DashboardMetricsMapper.cs#L48-L49) |
| Spread (points, để hiển thị/guard) | `(int)(Spread × Point)` | DashboardViewModel ~L6527 · SignalEntryGuard L151/L159 |

### 6.2 Slippage
**File:** [DashboardViewModel.cs:5717-5760](../TradeDesktop.App/ViewModels/DashboardViewModel.cs#L5717-L5760)

| Loại | BUY (`TradeType==0`) | SELL (`TradeType==1`) | Line |
|---|---|---|---|
| **Open slippage** | `(ExpectedPrice − FillPrice) × Point` | `(FillPrice − ExpectedPrice) × Point` | L5729-5731 |
| **Close slippage** | `(ClosePrice − ExpectedPrice) × Point` | `(ExpectedPrice − ClosePrice) × Point` | L5757-5759 |

- `pointValue = Math.Max(1, point)`. Cần có `PendingOpen/CloseRequest` khớp ticket + đúng `TradeType` + `ExpectedPrice.HasValue`.
- **Alert:** nếu `|slippage| > 40 pts` → gửi Telegram (`AlertSlippageThresholdPt = 40.0`, [L106](../TradeDesktop.App/ViewModels/DashboardViewModel.cs#L106); kiểm tra tại L7012).

---

## 7. Quy tắc thời gian / coordinator

**File:** [PortfolioCoordinator.cs](../TradeDesktop.Application/Services/Portfolio/PortfolioCoordinator.cs)

### 7.1 Random trong khoảng — `NextSecondsInRange` (L714-721)
```csharp
min = Math.Max(0, minSeconds); max = Math.Max(0, maxSeconds);
if (min > max) swap;  if (min == max) return min;
return _random.Next(min, max + 1);   // uniform [min, max]
```

### 7.2 Global cooldown (Rule B) — `KickGlobalCooldown` (L272-300)
```
cooldownSec  = NextSecondsInRange(GlobalCooldownMin, GlobalCooldownMax)   // L274
newLockUntil = triggeredAtUtc + cooldownSec (giây)                        // L284
GlobalActionLockUntilUtc = max(lock cũ, newLockUntil)   // MAX semantics: chỉ extend  (L300)
```
- Kick tại **DISPATCH time** (lúc allocate open / mark close), KHÔNG phải confirm time.
- App restart luôn kick cooldown mới (`startupCooldownSec`, L659-662).

### 7.3 Opposite-side lock (Rule C) — hằng số `OppositeSideLockSeconds = 300` (L23)
```
oppositeLockUntil = LastOpenConfirmedAtUtc + 300s                 // L328
remaining         = 300 − (now − LastOpenConfirmedAtUtc).Seconds  // L509-511
```
- Chỉ block **OPEN ngược chiều** trong 300s; KHÔNG block close; same-side refresh timer.

### 7.4 Quota (Rule A) — đếm `PendingOpen + Live + PendingClose`
**File:** [PortfolioState.cs](../TradeDesktop.Application/Services/Portfolio/PortfolioState.cs#L27-L34)
```
totalNow = count(status ∈ {PendingOpen, Live, PendingClose})
buyNow   = count(Buy  ∧ trên)   ;   sellNow = count(Sell ∧ trên)
```
Block khi: `totalNow ≥ MaxTotalOpens`, `buyNow ≥ MaxBuyOpens`, `sellNow ≥ MaxSellOpens` (PortfolioCoordinator L472-500).

### 7.5 Priority close (Rule D) — chọn 1 slot khi nhiều slot cùng đủ điều kiện (L189-217)
```
Mặc định : OrderByDescending(LastProfitSnapshot ?? double.MinValue)   // profit cao nhất  (L215)
Overtime : nếu (now − OpenConfirmedAtUtc).Seconds > MaxLifeTimeBySecond
           → OrderBy(OpenConfirmedAtUtc)            // cũ nhất trước
             .ThenByDescending(LastProfitSnapshot)  // tiebreak profit
```
- `MaxLifeTimeBySecond` CHỈ dùng để **ưu tiên trong nhóm slot ĐÃ có close signal**, không bao giờ tự tạo close.

### 7.6 Holding time — `IsHoldingTimeElapsed` (PositionSlot.cs L85-93)
```
elapsed = (now − OpenConfirmedAtUtc) ≥ HoldingSeconds
HoldingSeconds = NextSecondsInRange(StartTimeHold, EndTimeHold)   // gán lúc allocate (L257)
```

---

## 8. Chuẩn hóa config (normalize)

**File:** [RuntimeConfigState.cs](../TradeDesktop.App/State/RuntimeConfigState.cs)

| Pattern | Áp dụng cho | Ý nghĩa |
|---|---|---|
| `point > 0 ? point : 1` | `CurrentPoint` | Point ≥ 1 |
| `Math.Abs(x)` | gap/TP threshold (`OpenPts`, `ConfirmGapPts`, `ClosePts`, `CloseTpProfit`, `LimitMaxTp`, ...) | bỏ dấu âm |
| `Math.Max(0, x)` | mốc thời gian (ms/s), `MaxGap`, `MaxSpread`, `MaxTimesTick`, `MaxLifeTimeBySecond` | không âm; 0 = disabled |
| `Math.Max(1, x)` | `MaxTotalOpens / MaxBuyOpens / MaxSellOpens`, `NumberOfQualifyingTimes` | tối thiểu 1 |

> Coordinator cũng tự normalize lần nữa: `UpdateQuotaConfig` dùng `Math.Max(1, ...)`, `UpdateMaxLifeTimeConfig` dùng `Math.Max(0, ...)`.

---

## 9. Bảng tổng hợp công thức

| Công thức | Biểu thức | File:line | Đơn vị |
|---|---|---|---|
| GapBuy | `(int)(B.Bid×Point) − (int)(A.Ask×Point)` | GapCalculator.cs:26 | points |
| GapSell | `(int)(B.Ask×Point) − (int)(A.Bid×Point)` | GapCalculator.cs:33 | points |
| Trade profit BUY | `(CurrentBid − OpenPrice) × Point` | DashboardViewModel.cs:5785 | double |
| Trade profit SELL | `(OpenPrice − CurrentAsk) × Point` | DashboardViewModel.cs:5793 | double |
| History profit BUY | `(ClosePrice − OpenPrice) × 100` | DashboardViewModel.cs:5798 | double |
| History profit SELL | `(OpenPrice − ClosePrice) × 100` | DashboardViewModel.cs:5799 | double |
| Slot profit | `LastProfitA + LastProfitB` | PositionSlot.cs:135-137 | double |
| Spread thô | `Ask − Bid` | DashboardMetricsMapper.cs:49 | decimal |
| Spread pts | `(int)(Spread × Point)` | SignalEntryGuard.cs:151 | points |
| Open slippage BUY | `(ExpectedPrice − FillPrice) × Point` | DashboardViewModel.cs:5730 | points |
| Close slippage BUY | `(ClosePrice − ExpectedPrice) × Point` | DashboardViewModel.cs:5758 | points |
| Cooldown lock | `dispatchTime + Random(min,max)` (MAX) | PortfolioCoordinator.cs:284 | giây |
| Opposite remaining | `300 − (now − LastOpenConfirmedAtUtc)` | PortfolioCoordinator.cs:511 | giây |
| Holding elapsed | `(now − OpenConfirmedAtUtc) ≥ HoldingSeconds` | PositionSlot.cs:92 | giây |
| Priority close | `OrderByDescending(LastProfitSnapshot)` | PortfolioCoordinator.cs:215 | — |

### Hằng số quan trọng

| Hằng số | Giá trị | File:line | Ý nghĩa |
|---|---|---|---|
| `OppositeSideLockSeconds` | 300 | PortfolioCoordinator.cs:23 | khóa OPEN ngược chiều |
| `AlertSlippageThresholdPt` | 40.0 | DashboardViewModel.cs:106 | ngưỡng alert slippage |
| `SnapshotUiRenderMinIntervalMs` | 200 | DashboardViewModel.cs:131 | throttle vẽ UI |
| `StaleTickThresholdSeconds` | 10 | DashboardViewModel.cs:100 | tick coi là cũ |
| `TpCheckLogMinIntervalSeconds` | 60 | PortfolioCoordinator.cs:53 | throttle log TP_CHECK |
| `PriceHistoryCapacityMs` | 60000 | SignalEntryGuard.cs:15 | cửa sổ lịch sử giá |

---

*Tài liệu sinh từ việc đọc trực tiếp source code (đã verify từng `file:line`). Khi sửa công thức trong code, cập nhật lại file này.*
