# Phase 0 memo — go/no-go

> **Trạng thái: ĐANG DỞ — tạm dừng trên macOS, sẽ chạy tiếp trên máy Windows có MT5 + cTrader.**
>
> Phần offline (baseline, kiểm R10, đọc DB) đã xong trên macOS. Phần spike FIX và toàn bộ kiểm chứng
> cần môi trường thật chưa chạy. **Xem §6 để biết chính xác còn phải làm gì.**

Ngày: 2026-09-16 · Branch: `dev-5-release-111-ctrader` · Máy đã chạy: macOS (darwin 24.5.0)

### Quyết định đã chốt với chủ dự án

| Câu | Đáp án |
|---|---|
| Cổng spike (Phase 0 chốt #1) | **SSL — 5211 (QUOTE) / 5212 (TRADE)**, giống production 100% |
| Mật khẩu (chốt #2) | Plaintext trong `Config-dev.cfg`, **ngoài repo**, `chmod 600`, xoá cùng `store/` + `log/` sau spike |
| Đặt lệnh thật trên demo (chốt #3) | **Được**, chạy khi thị trường mở. Luôn kết thúc bằng `7\|spike-pos-final` → `728=2` |
| 11 test fail (P4) | **Điều tra trước, hoãn cTrader.** Chỉ báo cáo phân loại, **không sửa logic** |

---

## 1. Chuẩn bị

| Mục | Kết quả |
|---|---|
| Branch xuất phát từ `dev-5-gap-on-dinh` | ✅ `git merge-base --is-ancestor dev-5-gap-on-dinh HEAD` = true. Đi trước **19 commit** (không phải nhánh mới tinh) |
| `git status` sạch | ❌ 9 mục untracked: `docs/plans/`, `ea-monitor/`, `*.zip`, vài file docs. Không chặn Phase 0 |

---

## 2. Baseline test — ĐÃ SỬA MỘT LỖI BUILD ĐỂ ĐO ĐƯỢC

### 2.1 Vấn đề chặn đã xử lý (được chủ dự án duyệt)

Test project **không compile được** trước khi sửa:

```
TradeDesktop.Tests/Config/GapStabilityConfigMappingTests.cs(170,9): error CS7036
  There is no argument given that corresponds to the required parameter 'HoldConfirmMs'
```

**Nguyên nhân:** commit `4165a45` ("Chuyen signal confirmation tu tick sang time tren nhanh nay") thêm
4 tham số bắt buộc vào `ConfigRecord` nhưng không cập nhật helper `BuildRecord` của test:
`HoldConfirmMs`, `CloseHoldConfirmMs`, `OpenMaxTimesTick`, `CloseMaxTimesTick`.
`git show --stat 4165a45` xác nhận commit đó không đụng file test này.

Lỗi phát sinh **trên nhánh này**, không thừa kế từ `dev-5-gap-on-dinh` (nhánh gốc có `HoldConfirmMs`
trong `ConfigRecord` nhưng test khi đó vẫn khớp). Test project đã không build được suốt **7 commit**.

**Đã sửa:** thêm 4 named arg với giá trị trung tính `0` (= tắt) vào `BuildRecord`, đúng vị trí thứ tự
tham số. Chỉ chạm **một hàm helper trong một file test**, không chạm production.
Sau khi sửa: `Build succeeded`, **không lộ thêm lỗi compile nào**.
`GapStabilityConfigMappingTests` → **14/14 pass**.

### 2.2 Baseline chính thức

```
DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
→ Failed: 11, Passed: 642, Skipped: 0, Total: 653
```

**Dùng con số 11 làm gate cho mọi phase sau.** Nhưng gate bằng *con số* là chưa đủ — phải so cả
*danh sách*, vì đổi một fail này lấy một fail khác sẽ vô hình nếu chỉ đếm:

| # | Test fail | Nhóm |
|---|---|---|
| 1 | `CloseSignalEngineTests.ProcessSnapshot_CloseMaxTimesTick_IsIgnoredByFixedSizeTpCycle` | TP cycle |
| 2 | `CloseSignalEngineTests.ProcessSnapshot_CloseMaxTpProfit_RejectsCompletedCycleAndStartsFreshCycle` | TP cycle |
| 3 | `CloseSignalEngineTests.ProcessSnapshot_CompletedTpCycleBelowTarget_StartsFreshCycle` | TP cycle |
| 4 | `CloseSignalEngineTests.ProcessSnapshot_CycleSizeChange_ResetsTpCycle` | TP cycle |
| 5 | `CloseSignalEngineTests.ProcessSnapshot_DuplicateSnapshot_DoesNotIncreaseTpCycleCount` | TP cycle |
| 6 | `CloseSignalEngineTests.ProcessSnapshot_FixedSizeOne_TriggersTpFromFirstProfitAtTarget` | TP cycle |
| 7 | `CloseSignalEngineTests.ProcessSnapshot_FixedSizeTen_TriggersTpOnlyOnTenthProfitAndIgnoresHoldTime` | TP cycle |
| 8 | `CloseSignalEngineTests.ResetGapState_PreservesTpCycle_ButResetClearsIt` | TP cycle |
| 9 | `Portfolio.PortfolioBlockedSignalTests.SuccessfulOpenTrigger_CarriesConcurrentBlockedSignalOfOtherSide` | Blocked signal |
| 10 | `Portfolio.SosCloseConfigResolverTests.LatestGap_BelowEffectiveThreshold_IsRejected(CloseByGapBuy, 7, null)` | SOS close |
| 11 | `Portfolio.SosCloseConfigResolverTests.LatestGap_BelowEffectiveThreshold_IsRejected(CloseByGapSell, null, -7)` | SOS close |

### 2.3 ⚠️ Baseline KHÔNG khớp CLAUDE.md §6 — cần chủ dự án quyết

`CLAUDE.md:535` ghi: *"Pre-existing baseline: 19 fails (9 TradingFlowEngineTests + 4 CloseSignalEngineTests
+ 6 adapter mirrors)"*. Thực tế **không còn một fail nào** ở `TradingFlowEngineTests` hay adapter mirror;
`CloseSignalEngineTests` từ 4 → 8; xuất hiện 2 nhóm mới chưa từng được ghi nhận.

Đặc tả 3 fail đại diện cho thấy đây **không phải** kiểu "test quên cập nhật chữ ký" mà là **khẳng định
về hành vi giao dịch đang khác đi**:

| Test | Kỳ vọng | Thực tế |
|---|---|---|
| `SosCloseConfigResolverTests` (2 ca) | chặn với lý do `LATEST_CLOSE_CONDITION_INVALID` | `null` — **guard không chặn nữa** |
| `CloseSignalEngineTests.ResetGapState_PreservesTpCycle_ButResetClearsIt` | `null` (không trigger) | trigger Close thật với `CloseReason = Tp` |
| `PortfolioBlockedSignalTests` | signal `sig-ok` | signal `sig-blocked` — mang nhầm signal |

Vì test project đã không build suốt 7 commit, **chưa ai từng nhìn thấy 11 fail này**. Không phân biệt
được đâu là "đổi hành vi có chủ đích, test chưa cập nhật" và đâu là **regression thật** trong logic
TP / SOS close nếu không điều tra sâu — mà điều tra đó chạm thẳng Rule D và CLAUDE.md §0.2, **ngoài
phạm vi Phase 0**.

---

## 3. Kiểm tra offline khác

| Mục | Kết quả |
|---|---|
| Logic rẽ theo `TradeSharedRecord.Sl/Tp`? | ✅ **Không.** Chỉ 2 call site, đều format để hiển thị: `DashboardViewModel.cs:6607-6608` (`FormatPrice`), `:6660-6661` (`FormatRawDouble`). Các hit `.Tp` khác là enum `CloseSignalReason.Tp` / `SignalCycleKind.Tp` — khác hẳn. Xác nhận giả định **R10** |
| Logic rẽ theo `HistorySharedRecord.Commission`? | ✅ **Không.** 7 hit, tất cả chảy vào ViewModel dưới dạng chuỗi đã format. Xác nhận **R10** |
| `CurrentOpenPendingTimeMs` trong DB | ✅ **30000 ms** trên cả **20** máy. Ngưỡng Phase 7 đòi ≥ 2000 ms → **dư 15 lần**, không cần chỉnh config |
| `point` trong DB *(kiểm thêm)* | `100` trên cả 20 máy → digits = 2 → **khớp kỳ vọng XAUUSD** của câu 3. Chưa thay được việc đọc `1008 SymbolDigits` thật |
| `max_total_opens` *(kiểm thêm)* | Dao động 1–12 tuỳ máy. Phase 7 phải đặt máy dùng để test về **1** |
| QuickFIXn restore trên darwin/net8.0 | ⏳ **CHƯA KIỂM** — sẽ biết khi `dotnet run` ConsoleSample |
| `QuickFixNApp.ToAdmin` tự gắn 553/554 | ✅ Đã đọc mã nguồn SDK ở lượt lập kế hoạch (`Common/QuickFixNApp.cs:57-75`): bỏ qua `35` ∈ {0,1,3}, còn lại set `49/56/50/52/553/554`. Ghi nhận là **pitfall #0 của Phase 3** |

---

## 4. Điều tra 11 test fail (P4) — ĐANG DỞ: 2/11 đã phân loại

Chủ dự án chốt: điều tra trước, **chỉ báo cáo, không sửa logic** (chạm Rule D / TP / SOS = CLAUDE.md §0.2).

### 4.1 Nhóm SOS close — 2 fail · kết luận sơ bộ: **ĐỔI HÀNH VI CÓ CHỦ ĐÍCH, test chưa cập nhật**

Test: `SosCloseConfigResolverTests.LatestGap_BelowEffectiveThreshold_IsRejected`
(2 ca: `CloseByGapSell/-7` và `CloseByGapBuy/+7`)

Lần vết số học, ca `CloseByGapSell, gapSell = -7, confirmGapPts = -12, closeGapPts = -8`:

| | Công thức | Kết quả | directionValid | Trả về |
|---|---|---|---|---|
| **Hiện tại** ([SosCloseConfigResolver.cs:34](../../../TradeDesktop.Application/Services/SosCloseConfigResolver.cs#L34)) | `threshold = Math.Max(-12, -8)` **có dấu** | `-8` | `-7 <= -(-8)` = `-7 <= 8` → **true** | `null` (cho qua) |
| **Test kỳ vọng** (ngữ nghĩa cũ) | `threshold = Math.Max(|−12|, |−8|)` | `12`… hoặc `8` | `-7 <= -8` → **false** | `LATEST_CLOSE_CONDITION_INVALID` |

Commit đổi: **`626cefb` "Cho phep gap am o nguong Open/Close thuong"**. Comment ngay trong code nói rõ
chủ đích:

> *"Ngưỡng mang dấu, khớp với CloseSignalEngine: ngưỡng dương giữ nguyên hành vi cũ
> (max(a,b) == max(|a|,|b|) khi cả hai >= 0), ngưỡng âm nới về phía trong."*

`git log` cho thấy `626cefb` **có** đụng file test này — tức tác giả đã cập nhật một phần test nhưng
**bỏ sót đúng 2 ca này**.

⚠️ **Vẫn cần tác giả xác nhận**, vì test này mã hoá một ngữ nghĩa guard thật: với ngưỡng âm, một gap
"chưa đủ sâu" (`-7` so với ngưỡng `-8`) giờ **được cho qua** thay vì bị chặn. Nếu đó đúng là ý đồ của
"nới về phía trong" thì chỉ cần sửa test. Nếu không, đây là guard bị nới lỏng ngoài ý muốn.

### 4.2 Nhóm TP cycle — 8 fail · **CHƯA ĐIỀU TRA**

`CloseSignalEngineTests`: 7 test `ProcessSnapshot_*` + `ResetGapState_PreservesTpCycle_ButResetClearsIt`.

Dấu hiệu đã thấy: `ResetGapState_PreservesTpCycle_ButResetClearsIt` kỳ vọng `null` nhưng nhận **một
trigger Close thật** với `CloseReason = Tp`, `CloseTpProfit = 15`, `CloseTpTarget = 15`.

Nghi can: `2ce1bb1` ("Chuyen signal sang chu ky so luong va go 4 cot config deprecated") và `4165a45`
("Chuyen signal confirmation tu tick sang time") — hai commit refactor thẳng vào chu kỳ TP.

**Việc cần làm:** với từng test, so ngữ nghĩa test viết ra với ngữ nghĩa `CloseSignalEngine` hiện tại;
phân loại "test cũ" vs "regression". Nhóm này **rủi ro cao nhất** vì chạm Rule D (priority close theo
profit) và đường TP.

### 4.3 Nhóm blocked signal — 1 fail · **CHƯA ĐIỀU TRA**

`PortfolioBlockedSignalTests.SuccessfulOpenTrigger_CarriesConcurrentBlockedSignalOfOtherSide`
kỳ vọng `sig-ok`, nhận `sig-blocked` — mang nhầm signal khi có hai chiều cùng lúc.

---

## 5. Spike FIX — 10 câu hỏi · **CHƯA CHẠY**

Chặn bởi: chưa có mật khẩu; cần máy có cTrader desktop để đối chiếu volume/symbol; cần giờ thị trường mở.

| Câu | Session | Lệnh gõ | Kết quả | Log raw | Kết luận |
|---|---|---|---|---|---|
| 9 | TRADE | *(tự động khi start)* | ⏳ | | |
| 8 | TRADE | *(tự động)* | ⏳ | | |
| 3 | TRADE | `8\|spike-sec\|0` | ⏳ | | |
| 10 | QUOTE | `8\|spike-sec-q\|0` | ⏳ | | |
| 2 | QUOTE | `4\|41\|n` → `5\|41\|n` → `4\|41\|y` | ⏳ | | |
| 4 | TRADE | `1\|spike-open-1\|41\|buy\|market\|1` | ⏳ | | |
| 5 | TRADE | *(quan sát câu 4)* | ⏳ | | |
| **1** | TRADE | `1\|spike-close-1\|41\|sell\|market\|1\|<posId>` → `7\|spike-pos-1` | ⏳ | | **SỐNG CÒN** |
| 6 | TRADE | `1\|spike-rej\|41\|buy\|market\|999999999` | ⏳ | | |
| 7 | TRADE | kill socket → `g` | ⏳ | | |

---

## 6. CÒN PHẢI LÀM — checklist cho phiên chạy trên Windows

### 6.1 Đo lại baseline trên Windows · **BẮT BUỘC, làm đầu tiên**

> Con số **11** ở §2.2 đo trên **macOS**. Baseline macOS và Windows **đã từng khác nhau** trong dự án
> này (ghi chú cũ: macOS 26 vs CLAUDE.md 19). Vì vậy **không được** dùng 11 làm gate cho các phase chạy
> trên Windows mà chưa đo lại.

- [ ] Lấy bản sửa `GapStabilityConfigMappingTests.BuildRecord` (4 named arg) — đã có trong working tree,
      **chưa commit**
- [ ] `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj`
- [ ] Ghi **cả con số lẫn danh sách tên test fail** vào đây; so với danh sách 11 của macOS ở §2.2
- [ ] Nếu lệch: ghi rõ test nào chỉ fail ở một OS — đó là thông tin cần cho mọi phase sau

### 6.2 Hoàn tất điều tra 11 fail (§4)

- [ ] Nhóm TP cycle — 8 test, đọc `CloseSignalEngine` vs ý đồ từng test (§4.2)
- [ ] Nhóm blocked signal — 1 test (§4.3)
- [ ] Xác nhận kết luận nhóm SOS ở §4.1 với tác giả `626cefb`
- [ ] Kết luận cuối: mỗi fail là "test cũ" hay "regression". **Không sửa logic** khi chưa có quyết định riêng
- [ ] Sau khi chốt: cập nhật `CLAUDE.md:535` (đang ghi sai "19 fails (9 TradingFlowEngineTests + 4
      CloseSignalEngineTests + 6 adapter mirrors)") bằng con số và danh sách thật

### 6.3 Chạy spike FIX (§5) — cần cTrader desktop + demo + giờ thị trường mở

- [ ] `git clone https://github.com/spotware/quickfixnsamples.net` ra **ngoài repo**
- [ ] Tạo 2 file `Config-dev.cfg` (TRADE và QUOTE) theo bảng ở
      [phase-0-spike.md](phase-0-spike.md); **cổng SSL 5212 / 5211**; `chmod 600`
- [ ] Chạy đúng thứ tự: **9, 8 → 3 → 4 → 1**. Nếu câu 1 không trả `728=2` → **DỪNG TOÀN BỘ**,
      không làm câu còn lại
- [ ] Chỉ sau khi câu 1 GO mới làm 2, 5, 6, 7, 10
- [ ] Ghi log raw từng câu vào §5, **che tag 554**
- [ ] Đối chiếu trên cTrader desktop: volume sau `38=1` phải hiện **0.01** (câu 4); "FIX symbol ID"
      trong cửa sổ thông tin symbol phải là **41** (câu 3)

### 6.4 Xác nhận môi trường mà macOS không kiểm được

- [ ] QuickFIXn.Core + QuickFIXn.FIX4.4 restore/compile trên Windows (macOS chưa kiểm vì chưa chạy
      ConsoleSample) → quyết layering Phase 3
- [ ] Kiểm SSL (5211/5212) hoạt động — quyết định đã chốt dùng SSL nên phải xác nhận TLS của
      QuickFIXn chạy được

### 6.5 Dọn dẹp cuối phiên spike

- [ ] `7\|spike-pos-final` → `728=2`, không để position mồ côi trên demo
- [ ] Xoá `Config-dev.cfg` · `store/` · `log/`

---

## 7. Kết luận hiện tại

**CHƯA KẾT LUẬN ĐƯỢC GO/NO-GO.** Câu quyết định (câu 1 — đóng position bằng tag 721) chưa chạy.

Đã xong: baseline macOS = 11 (kèm danh sách), xác nhận R10 (Sl/Tp và Commission chỉ để hiển thị),
`open_pending_time_ms = 30000` dư 15 lần ngưỡng Phase 7, `point = 100` khớp kỳ vọng XAUUSD.

Chưa xong: toàn bộ §6.

### Thay đổi trong working tree (chưa commit)

| File | Thay đổi |
|---|---|
| `TradeDesktop.Tests/Config/GapStabilityConfigMappingTests.cs` | +4 named arg vào `BuildRecord` (`HoldConfirmMs`, `CloseHoldConfirmMs`, `OpenMaxTimesTick`, `CloseMaxTimesTick` = `0`). **Sửa lỗi build, không chạm production** |
| `docs/plans/ctrader-fix/phase-0-memo.md` | File này |

Commit message đề xuất (CLAUDE.md §7):

```
phase 0: sua loi build test project va do baseline cho ke hoach cTrader

Bo sung 4 named arg thieu trong GapStabilityConfigMappingTests.BuildRecord
(HoldConfirmMs, CloseHoldConfirmMs, OpenMaxTimesTick, CloseMaxTimesTick) —
ConfigRecord them 4 tham so bat buoc o commit 4165a45 nhung test khong duoc
cap nhat, khien test project khong build duoc suot 7 commit.

Baseline macOS sau khi sua: 11 fail / 642 pass / 653 total.
Danh sach 11 test fail va phan tich ghi trong docs/plans/ctrader-fix/phase-0-memo.md.
```
