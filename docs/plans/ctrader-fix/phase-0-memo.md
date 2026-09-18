# Phase 0 memo — go/no-go

> **Trạng thái: GO-READ ĐẠT (2026-09-17).** Câu 2/3/7/8/9/10 có log raw trên live 8220816; baseline 11
> (= macOS, 0 regression); layering `Infrastructure/CTrader`. Câu 1/4/5/6 (đặt lệnh tiền thật) → **Phase 7
> Bước A**. Mật khẩu live 8220816 từng hiện trên console sample (§3) — chủ dự án **chấp nhận rủi ro**
> (tài khoản dev, kiểm soát số tiền), không yêu cầu đổi.

Ngày: 2026-09-16 · Branch: `dev-5-release-111-ctrader` · Máy đã chạy: macOS (offline) → Windows 11 (spike)

### Quyết định đã chốt với chủ dự án

| Câu | Đáp án |
|---|---|
| Cổng spike (Phase 0 chốt #1) | **SSL — 5211 (QUOTE) / 5212 (TRADE)**, giống production 100% |
| Mật khẩu (chốt #2) | Plaintext trong `Config-dev.cfg`, **ngoài repo**, `chmod 600`, xoá cùng `store/` + `log/` sau spike |
| Đặt lệnh thật trên demo (chốt #3) | **Được**, chạy khi thị trường mở. Luôn kết thúc bằng `7\|spike-pos-final` → `728=2` |
| 11 test fail (P4) | **Điều tra trước, hoãn cTrader.** Chỉ báo cáo phân loại, **không sửa logic** |
| Tài khoản hedged (chốt #4) | **Hedging** — đọc từ cTrader Web 2026-09-16 (`FxPro · Demo · 10649643 · Hedging`); live 8220816 cũng hiện `Hedging` |
| **Tài khoản dùng cho spike (đổi 2026-09-16 tối)** | **LIVE 8220816** (`live.cfixapi.com`, `live.fxpro.8220816`) — chủ dự án quyết vì FxPro tắt FIX cho demo (`RET_ACCOUNT_DISABLED`). Hệ quả: câu 4/1 đặt lệnh **tiền thật**; balance lúc quyết là **USD 0.01** → phải nạp ≥ ~$20 trước câu 4 (margin 1 oz XAUUSD @1:500 ≈ $8.7). README §1 credentials phải cập nhật sang live ở Phase 2 |

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

### 2.2b Baseline WINDOWS (2026-09-16, máy dev Windows 11, SDK 8.0.425 vừa cài qua winget)

```
DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
→ Failed: 11, Passed: 642, Skipped: 0, Total: 653
```

**Danh sách 11 test fail TRÙNG 100% với macOS §2.2** (đối chiếu từng tên, thứ tự 1–11 như bảng trên).
Không có test nào chỉ fail ở một OS. → **Gate cho mọi phase sau: 11 fail, đúng danh sách §2.2.**

Ghi chú build: 3 warning `CA1416` có sẵn ở `TradeDesktop.Infrastructure` (`HistorySharedMemoryReader.cs:28`,
`TradesSharedMemoryReader.cs:27`, `SharedMemoryMarketDataReader.cs:201` — `MemoryMappedFile.OpenExisting`
chỉ có trên Windows). Đây là **baseline warning**, không phải warning mới; "build sạch, không warning
mới" ở các phase sau so với con số 3 này.

Máy dev **trước đó không có .NET SDK** (chỉ còn sentinel `~/.dotnet/8.0.424.*`), khớp với commit
message của `4165a45`: *"CHUA BUILD VA CHUA CHAY TEST: may phat trien khong co .NET SDK"*.

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
| QuickFIXn restore/build (**kết luận layering**) | ✅ **Windows, SDK 8.0.425**: `dotnet restore` + `dotnet build` `ConsoleSample` (ref `QuickFIXn.FIX4.4 1.10.0`, kéo `QuickFIXn.Core`) **0 error**, chỉ warning `NETSDK1138` vì sample target `net5.0` hết hạn. Output có `QuickFix.dll`, `QuickFix.FIX44.dll`, `FIX44-CSERVER.xml`. Không có phụ thuộc Windows-only → **Phase 3 đặt ở `Infrastructure/CTrader`**. darwin chưa kiểm — chỉ cần nếu còn dev trên macOS, không chặn |
| `QuickFixNApp.ToAdmin` (đọc mã nguồn bản clone) | ✅ Xác nhận lại: bỏ qua `35 ∈ {0,1,3}`, còn lại `SetField` 49/56/50/52/**553/554** với `overwrite=true`. ⚠️ **Bộ lọc này SAI với cServer** — gắn cả vào Logout và bị reject (§5 câu 7b). Sample dùng `FileStoreFactory` → `store/*.body` lưu message đã gửi **kèm Logon có mật khẩu** — phải xoá. `FileLogPath=log` **không có tác dụng** vì sample không truyền `ILogFactory` → không có `log/` |
| ⚠️ **Sự cố lộ mật khẩu (2026-09-17)** | Console của sample in mọi admin message trừ `0/1/A` → **Logout mang `554` nguyên văn được in ra** và bị dán vào chat. Đã: xoá `store/`; vá `ConsoleSample/Program.cs` che `554` trước khi in; vá `ToAdmin` chỉ gắn credentials vào Logon. Chủ dự án **chấp nhận rủi ro, không đổi mật khẩu** (2026-09-17): 8220816 là tài khoản dev, số tiền được kiểm soát. Bài học cho Phase 3: che `554` ở **mọi** đường in/log, không chỉ Logon |
| README ConsoleSample về SSL | ⚠️ Ghi *"You can only use plain text, not SSL"*. Mâu thuẫn với chốt #1 (SSL). Template spike đặt `SSLEnable=Y`/`SSLServerName` (QuickFIX/n hỗ trợ); nếu logon không lên → lùi plain **5202/5201** chỉ cho spike; production Phase 4 phải kiểm SSL riêng |
| **Kiểm chéo qua cTrader Web** (Windows, 2026-09-16, Playwright MCP chỉ đọc) | ✅ Symbol info XAUUSD: **FIX symbol ID 41**, pip position **2**, min change **0.01**, **lot size 100 Oz**, min qty **0.01 lot**. Tài khoản **Hedging**. Positions/Orders = 0. History có **4 lệnh XAUUSD cũ** (lỗ tổng $-562.21, không phải của spike). Market hours: 05:00 → 03:59:45 hôm sau (UTC+7). **Panel FIX API không có trên web** — nút chỉ mở `help.ctrader.com/fix/`. Cùng login còn 2 tài khoản Live 8220816/8225904 — cfg spike chỉ dùng `demo.fxpro.10649643` |
| Digits XAUUSD ở **chân A (MT5)** | ✅ **Digits 2, Contract size 100** — đọc từ Market Watch → Specification của terminal MT5 sàn A (2026-09-16). Khớp cTrader (pip position 2, lot 100 Oz) và `point=100` trong DB → **R6 loại trừ trên máy này**; contract size hai sàn bằng nhau nên R5 chỉ còn phụ thuộc `volumeBUnits`/`volumeALots` |
| **Panel FIX API của FxPro** (chủ dự án chép tay, không kèm mật khẩu) | ✅ **Khớp README §1 từng trường**: host `demo-uk-eqx-01.p.c-trader.com` cho cả QUOTE/TRADE; port 5211/5201 (QUOTE) và 5212/5202 (TRADE); SenderCompID `demo.fxpro.10649643`; TargetCompID `cServer`; SenderSubID `QUOTE`/`TRADE`. Panel ghi mật khẩu là *"a/c 10649643 password"* → **mật khẩu FIX = mật khẩu đăng nhập tài khoản**, không phải mật khẩu riêng |
| `QuickFixNApp.ToAdmin` tự gắn 553/554 | ✅ Đã đọc mã nguồn SDK ở lượt lập kế hoạch (`Common/QuickFixNApp.cs:57-75`): bỏ qua `35` ∈ {0,1,3}, còn lại set `49/56/50/52/553/554`. Ghi nhận là **pitfall #0 của Phase 3** — **đã hiệu chỉnh 2026-09-17**: chỉ gắn khi `35=A` (§5 câu 7b) |

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

### 4.2 Nhóm TP cycle — 8 fail · kết luận: **TEST CŨ (FIXED_SIZE) CHƯA CẬP NHẬT — không phải regression**

**Bằng chứng lịch sử (git):** `CloseSignalEngineTests.cs` được sửa lần cuối ở `2ce1bb1` (chu kỳ số lượng,
FIXED_SIZE) và `e62452b`. Commit `4165a45` ("Chuyen signal confirmation tu tick sang time") sửa
`CloseSignalEngine.cs` **274 dòng** nhưng **không đụng** file test này, và commit message tự ghi:
*"CHUA BUILD VA CHUA CHAY TEST: may phat trien khong co .NET SDK"*. Tức 8 test này mã hoá ngữ nghĩa
FIXED_SIZE đã bị thay có chủ đích — đúng như CLAUDE.md §5 mô tả nhánh TIME: *"`signal_cycle_size`…
không tham gia quyết định signal"*, *"Normal Close, SOS Close và TP dùng CHUNG `close_hold_confirm_ms`"*.

**Ngữ nghĩa `ProcessTp` hiện tại** ([CloseSignalEngine.cs:448-615](../../../TradeDesktop.Application/Services/CloseSignalEngine.cs#L448)):
cửa sổ mở ở mẫu đầu có `profit ≥ CloseConfirmTpProfit`; mỗi tick `Add`; **chỉ đánh giá khi
`elapsed ≥ CloseHoldConfirmMs`** (default `0` ở `GapSignalModels.cs:133`); khi đủ hold: `lastProfit <
target` → reset `TARGET_NOT_REACHED`; `> CloseMaxTpProfit` → bỏ qua tick, **không reset**; `TickCount >
CloseMaxTimesTick` → reset; còn lại → **trigger ngay**, `CloseTpProfits` = mẫu đã gom. `SignalCycleSize`
không xuất hiện trong nhánh nào.

| # | Test | Test kỳ vọng (FIXED_SIZE) | Engine TIME làm gì | Phân loại |
|---|---|---|---|---|
| 1 | `CloseMaxTimesTick_IsIgnoredByFixedSizeTpCycle` | trigger ở mẫu thứ 3, `CloseMaxTimesTick=1` bị bỏ qua | `CloseHoldConfirmMs=100`, 3 mẫu cách 1 ms → `elapsed < 100` → `null` mãi | Test cũ. Tên test nói thẳng "FixedSize" |
| 2 | `CloseMaxTpProfit_RejectsCompletedCycleAndStartsFreshCycle` | 3×25 bị reject, rồi 3×18 trigger | hold 100 ms, mẫu cách 1 ms → `null`; thêm nữa TIME cố ý **không reset** khi vượt MaxTp (comment dòng 553) | Test cũ |
| 3 | `FixedSizeTen_TriggersTpOnlyOnTenthProfitAndIgnoresHoldTime` | mẫu thứ 10 trigger, **bỏ qua hold** `999_999` | hold 999 999 ms → `null` | Test cũ. Tên test nói thẳng "IgnoresHoldTime" |
| 4 | `FixedSizeOne_TriggersTpFromFirstProfitAtTarget` | 1 mẫu trigger, bỏ qua hold | hold 999 999 ms → `null` | Test cũ |
| 5 | `CompletedTpCycleBelowTarget_StartsFreshCycle` | 10,12,14 → cycle 1 fail; 15,16,17 → trigger `[15,16,17]` | hold **0** → mỗi tick đánh giá ngay: 10/12/14 reset `TARGET_NOT_REACHED`; **15 trigger ngay** với `[15]` | Test cũ |
| 6 | `DuplicateSnapshot_DoesNotIncreaseTpCycleCount` | `[10,12,15]` | 10 và 12 reset (dưới target); 15 trigger với `[15]`. TIME **không khử trùng lặp fingerprint** (CLAUDE.md §5) | Test cũ |
| 7 | `CycleSizeChange_ResetsTpCycle` | đổi `SignalCycleSize` 3→2 reset cycle, trigger `[15,16]` | `SignalCycleSize` không được đọc; 15 trigger ngay | Test cũ |
| 8 | `ResetGapState_PreservesTpCycle_ButResetClearsIt` | `ResetGapState` giữ TP, `Reset` xoá; trigger `[15,16,17]` | phần ngữ nghĩa Reset vẫn đúng ([`:157-161`](../../../TradeDesktop.Application/Services/CloseSignalEngine.cs#L157)); nhưng hold 0 → **15 trigger ngay** (đúng như memo §2.3 ghi nhận: `CloseTpProfit=15`, `Target=15`) | Test cũ |

**Không có dấu hiệu regression.** Cả 8 sai lệch đều quy về đúng hai điểm đổi có chủ đích của TIME mode
(hold-time thay cho số mẫu; không dedupe) — không có ca nào engine trigger sai ngưỡng, sai chiều, hay
bỏ qua guard. Hai tính chất **đáng giữ** khi viết lại test: (a) `Reset()` xoá TP còn `ResetGapState()`
không (`:157-161`); (b) `CLOSE_MAX_TP_EXCEEDED` không reset cửa sổ (`:553`).

⚠️ Vẫn cần tác giả `4165a45` xác nhận. **Không sửa test trong Phase 0** (chạm Rule D / TP = §0.2) —
viết lại 8 test theo TIME là một task riêng, phải có quyết định của chủ dự án.

### 4.3 Nhóm blocked signal — 1 fail · kết luận: **TEST PHỤ THUỘC ĐỒNG HỒ THẬT — không phải regression**

`PortfolioBlockedSignalTests.SuccessfulOpenTrigger_CarriesConcurrentBlockedSignalOfOtherSide`
kỳ vọng Sell bị `OPPOSITE_SIDE_LOCK` chặn (còn `sig-ok` Buy thắng), thực tế Sell **không bị chặn** nên
trigger đầu tiên trong list (`sig-blocked`, Sell) được trả về.

**Nguyên nhân:** [PortfolioCoordinator.cs:1107](../../../TradeDesktop.Application/Services/Portfolio/PortfolioCoordinator.cs#L1107)
tính `elapsedSec = (DateTime.UtcNow - LastOpenConfirmedAtUtc)` bằng **đồng hồ tường**, không dùng
`snapshot.TimestampUtc`. Dòng này có từ `12cc0e6a` (2026-05-21), **trước** khi test được viết
(`e62452b`). Test dùng mốc cố định `OpenTime = 2026-09-02 10:00 UTC` và confirm slot ở `OpenTime-10s`
→ hôm nay elapsed ≈ 14 ngày ≫ 300 s → lock đã hết. Test chỉ pass được trong ~5 phút quanh thời điểm
tác giả viết nó. Toàn bộ `OppositeSideLockTests` (cùng Rule C) đều dùng `DateTime.UtcNow.AddSeconds(-n)`
— đó là quy ước đúng; test này lệch quy ước.

**Phân loại: test viết sai (time-bomb), không phải regression.** Sửa đúng là đổi `OpenLiveSlot` sang
`DateTime.UtcNow.AddSeconds(-10)` như `OppositeSideLockTests`. Không sửa trong Phase 0. Ghi nhận thêm:
việc `CanOpenNewSlot` dùng `UtcNow` thay cho thời gian snapshot là hành vi có từ lâu, ngoài phạm vi.

### 4.4 Tổng kết 11/11

| Nhóm | Số | Kết luận | Cần gì trước Phase 1 |
|---|---|---|---|
| SOS close (§4.1) | 2 | Đổi hành vi có chủ đích ở `626cefb`, test bỏ sót | Tác giả xác nhận "nới về phía trong" là ý đồ → sửa test (task riêng) |
| TP cycle (§4.2) | 8 | Test FIXED_SIZE cũ, engine TIME có chủ đích ở `4165a45` | Tác giả xác nhận → viết lại test theo TIME (task riêng) |
| Blocked signal (§4.3) | 1 | Test time-bomb dùng mốc cố định | Sửa test dùng `UtcNow` (task riêng) |

**Không phát hiện regression thật.** Baseline 11 giữ nguyên làm gate; mọi phase sau không được đổi
danh sách này theo bất kỳ hướng nào nếu chưa có quyết định riêng.

---

## 5. Spike FIX — 10 câu hỏi · **CHƯA CHẠY**

Chặn bởi: chưa có mật khẩu; cần máy có cTrader desktop để đối chiếu volume/symbol; cần giờ thị trường mở.

| Câu | Session | Lệnh gõ | Kết quả | Log raw | Kết luận |
|---|---|---|---|---|---|
| 9 | TRADE | *(tự động khi start)* | ✅ cấu trúc Logon đúng | `Outgoing 35=5(=cùng path ToAdmin với Logon) ... 49=demo.fxpro.10649643\|50=TRADE\|56=cServer\|57=TRADE\|553=10649643\|554=***` | Username ở **553**, password ở **554**, tag 50 = `TRADE` (không chứa mật khẩu) → đóng vĩnh viễn điểm sai của tài liệu ngoài (README Phụ lục A). Quirk sample: `ToAdmin` `SetField` 49/50/52/56 vào **body** nên các tag này xuất hiện 2 lần; server vẫn parse |
| 8 | TRADE | *(tự động)* | **DEMO 10649643: ❌ `RET_ACCOUNT_DISABLED`** · **LIVE 8220816: ✅ logon OK** (2026-09-16) | Demo, lần 1 (placeholder): `35=5\|58=RET_INVALID_DATA`. Demo, lần 2 (mật khẩu thật): `35=5\|34=1\|49=cServer\|58=RET_ACCOUNT_DISABLED` lặp mỗi 2 s. **Live** (`live.cfixapi.com:5212`, `49=live.fxpro.8220816`, SSL): **không có Logout nào**, `35=x` gửi → `35=y` trả về ngay (xem câu 3) | **FxPro chỉ tắt FIX cho tài khoản DEMO, không tắt toàn bộ.** `cServer` + SSL 5212 + `SSLEnable=Y` đều hoạt động → chốt #1 (SSL) giữ; README sample nói "chỉ plain" là lạc hậu. Đường đi tiếp cho câu 4/1 (đặt lệnh): (a) xin FxPro bật FIX demo, hoặc (b) chủ dự án quyết định riêng về việc chạy trên live. **Phiên live chỉ chạy logon + SecurityList theo phạm vi thu hẹp, không gửi lệnh** |
| 3 | TRADE (LIVE 8220816) | `8\|spike-sec\|0` | ✅ | `35=y\|34=2\|49=cServer\|50=TRADE\|56=live.fxpro.8220816\|320=spike-sec\|322=responce:spike-sec\|560=0\|146=316\|...\|55=41\|1007=XAUUSD\|1008=2\|...` (cũng có `55=1108\|1007=XAUEUR\|1008=2`, `55=1110\|1007=XAUUSDgr\|1008=3`) | **`55=41` = `XAUUSD`, `1008 SymbolDigits = 2`** → khớp cTrader Web (pip position 2), khớp MT5 chân A (Digits 2), khớp `point=100` DB. **R6 đóng.** 316 symbol trong catalog; `322` server đánh vần "responce" — đọc tag 320 để khớp request, đừng parse chuỗi 322. Lưu ý: catalog này là của **live**; symbol ID ở demo có thể khác về lý thuyết nhưng cTrader Web demo cũng hiện FIX symbol ID 41 → nhất quán |
| 10 | QUOTE (LIVE) | `8\|spike-sec-q\|0` | ✅ **QUOTE trả lời SecurityList** | `35=y\|34=2\|49=cServer\|50=QUOTE\|56=live.fxpro.8220816\|57=QUOTE\|320=spike-sec-q\|322=responce:spike-sec-q\|560=0\|146=316` | **Phase 4 KHÔNG cần mở TRADE initiator chỉ để lấy SecurityList** — gửi qua QUOTE là đủ. Catalog giống hệt TRADE (316 symbol) |
| 2 | QUOTE (LIVE) | `4\|41\|n` → `5\|41\|n` → `4\|41\|y` → `5\|41\|y` | ✅ `264=1` = SPOT, `264=0` = DEPTH (ngược FIX chuẩn, đúng R11) | Spot: `35=V\|263=1\|264=1\|267=2\|269=0\|269=1\|146=1\|55=41` → `35=W\|55=41\|262=MARKETDATAID\|268=2\|269=0\|270=4350.77\|269=1\|270=4350.93` (**không có 278**), lặp **40 W trong ~8 s**, **không có X nào**. Depth: `35=V\|264=0` → `35=W\|268=19\|269=1\|270=4353.05\|271=18750\|278=6906450509\|...` rồi `35=X\|268=37\|279=0\|269=1\|278=...\|55=41\|270=...\|271=...` liên tục; `279` chỉ có `0` (New, 770 lần) và `2` (Delete, 769 lần), **không có `1` (Change)**. Unsubscribe `263=2` với cùng `262` được chấp nhận im lặng | **PHÁT HIỆN CHO PHASE 3:** ở chế độ **SPOT**, server **không gửi X incremental** — mỗi tick là một **W snapshot đầy đủ 2 entry, không có 278**. Vậy `CTraderQuoteBook` cho spot chỉ cần "thay toàn bộ top-of-book mỗi W"; book keyed-by-278 + xử lý X/279 chỉ cần nếu subscribe depth. Plan Phase 3 câu chốt #3 ("cài theo book keyed by 278 vì 279 chỉ có New/Delete") **đúng cho depth nhưng thừa cho spot** — giữ đơn giản: xử lý W, và fail-closed (log + bỏ qua) nếu nhận X khi đang spot. Ngoài ra: **không có `273 MDEntryTime`** → Phase 4 `Time` phải lấy từ tag `52 SendingTime`. Tần suất ~5 W/s trên XAUUSD live lúc 21:58 UTC+7 |
| 4 | TRADE | `1\|spike-open-1\|41\|buy\|market\|1` | ↪ **Phase 7 Bước A** (tái cấu trúc 2026-09-16) | | **Kiểm chéo web**: lot size 100 Oz → kỳ vọng `38=1` hiện Quantity 0.01 trên tab Positions |
| 5 | TRADE | *(quan sát câu 4)* | ↪ **Phase 7 Bước A** (tái cấu trúc 2026-09-16) | | |
| **1** | TRADE | `1\|spike-close-1\|41\|sell\|market\|1\|<posId>` → `7\|spike-pos-1` | ↪ **Phase 7 Bước A** (tái cấu trúc 2026-09-16) | | **SỐNG CÒN** |
| 6 | TRADE | `1\|spike-rej\|41\|buy\|market\|999999999` | ↪ **Phase 7 Bước A** (tái cấu trúc 2026-09-16) | | |
| 7 | TRADE (LIVE) | `x` (stop initiator) → `g` (start lại) → `q` · chủ dự án chạy 2026-09-17 | ✅ **Reconnect sạch, seqnum reset** | `x`: `Outgoing 35=5\|34=2\|...\|553=8220816\|554=***` → `Incoming 35=3\|34=2\|45=2\|58=Tag not defined for this message type, field=553\|371=553\|372=5\|373=2`. `g`: `Restarting initiator...`, **không có** `35=5`, `35=3` hay `35=2` nào sau đó. File store sau phiên: `*.seqnums = 0000000002 : 0000000002`, `*.session` tạo lúc `20260917-04:21:11` (= thời điểm `g`) | Logon sau `g` mang **`34=1`** (outgoing next = 2) và server trả Logon **`34=1`** (incoming next = 2) → `ResetOnLogon=Y` / `141=Y` reset đúng, **không ResendRequest** → `MemoryStoreFactory` + reset mỗi logon là đủ (README §4.6 đúng). Không đọc được dòng `35=A` nguyên văn vì sample **không nối log factory** (`new SocketInitiator(app, storeFactory, settings)`) → `FileLogPath` bị bỏ qua, thư mục `log/` **không bao giờ được tạo** (sửa ghi chú §3 cũ). **PHÁT HIỆN QUAN TRỌNG — xem dòng dưới** |
| 7b | TRADE (LIVE) | *(cùng phiên)* | ⚠️ **cServer TỪ CHỐI 553/554 trên Logout** | `35=3\|58=Tag not defined for this message type, field=553\|371=553\|372=5` | `QuickFixNApp.ToAdmin` của Spotware gắn 553/554 cho **mọi** admin message trừ `0/1/3` — tức cả **Logout `35=5`** và ResendRequest/SequenceReset. Server coi đó là lỗi session-level. Hệ quả cho Phase 3: **chỉ gắn 553/554 khi `35=A`**, không chép nguyên bộ lọc của sample. Đã vá bản clone ngoài repo (`Common/QuickFixNApp.cs`) cho Phase 7 Bước A |

---

## 6. CÒN PHẢI LÀM — checklist cho phiên chạy trên Windows

### 6.1 Đo lại baseline trên Windows · **BẮT BUỘC, làm đầu tiên**

> Con số **11** ở §2.2 đo trên **macOS**. Baseline macOS và Windows **đã từng khác nhau** trong dự án
> này (ghi chú cũ: macOS 26 vs CLAUDE.md 19). Vì vậy **không được** dùng 11 làm gate cho các phase chạy
> trên Windows mà chưa đo lại.

- [x] Bản sửa `GapStabilityConfigMappingTests.BuildRecord` đã commit ở `632b0a4`
- [x] Cài .NET SDK 8.0.425 (winget, 2026-09-16 — máy trước đó không có SDK) và chạy test
- [x] Kết quả: **11 fail / 642 pass**, danh sách trùng 100% macOS — xem §2.2b
- [x] Không có test nào chỉ fail ở một OS

### 6.2 Hoàn tất điều tra 11 fail (§4)

- [x] Nhóm TP cycle — 8 test: **test FIXED_SIZE cũ**, không regression (§4.2)
- [x] Nhóm blocked signal — 1 test: **time-bomb dùng mốc cố định**, không regression (§4.3)
- [ ] Xác nhận kết luận nhóm SOS ở §4.1 và nhóm TP ở §4.2 với tác giả `626cefb` / `4165a45`
- [x] Kết luận cuối (§4.4): 11/11 là "test cũ / test viết sai", **0 regression**. Không sửa logic, không sửa test
- [ ] Sau khi chốt: cập nhật `CLAUDE.md:535` (đang ghi sai "19 fails (9 TradingFlowEngineTests + 4
      CloseSignalEngineTests + 6 adapter mirrors)") bằng con số và danh sách thật

### 6.3 Chạy spike FIX (§5) — **TÁI CẤU TRÚC 2026-09-16: chỉ còn câu 7 ở Phase 0**

> Câu **4, 1, 5, 6** (đặt lệnh tiền thật) **chuyển sang Phase 7 Bước A** để gom một phiên tiền thật.
> Phase 0 kết thúc bằng cổng **GO-READ** (câu 2/3/7/8/9/10). Xem [phase-0-spike.md § Tái cấu trúc].

- [x] **Câu 7** (xong 2026-09-17, §5 câu 7/7b) — chủ dự án chạy (policy chặn Claude kết nối tài khoản thật): `Config-dev.LIVE-TRADE.cfg`
      → `Config-dev.cfg`, `dotnet run`, gõ `x` → `g` → `q`; lấy 2 dòng `35=A` từ `log\*messages*.log`
      (che 554). Kỳ vọng Logon thứ hai `34=1`, `141=Y`, không `35=2`.
- [x] Giữ `C:\tmp\ctrader-spike` (đã build) + `Config-dev.LIVE-TRADE.cfg` / `LIVE-QUOTE.cfg` **có chủ
      đích** cho Phase 7 Bước A. `store\` đã xoá; `log\` không tồn tại. Sample đã vá che 554 + chỉ gắn credentials vào Logon.

*(Các mục cũ bên dưới giữ để tham chiếu; phần đặt lệnh nay thuộc Phase 7.)*

- [x] Clone vào `C:\tmp\ctrader-spike` (ngoài repo); `dotnet restore` + `build` OK trên SDK 8 (§3)
- [x] Template `ConsoleSample\Config-dev.TRADE.template.cfg` và `.QUOTE.template.cfg` đã tạo (SSL
      5212/5211, `SSLEnable=Y`, placeholder mật khẩu); `FIX44-CSERVER.xml` đã copy vào `ConsoleSample\`
- [x] Chủ dự án copy template → `Config-dev.cfg`, **tự gõ mật khẩu**, chạy
      `set DOTNET_ROLL_FORWARD=Major && dotnet run` trong `ConsoleSample\` (sample target net5.0)
- ~~Chạy đúng thứ tự: **9, 8 → 3 → 4 → 1**.~~ *Không áp dụng — câu 1/4 chuyển Phase 7 Bước A.* Nếu câu 1 không trả `728=2` → **DỪNG TOÀN BỘ**,
      không làm câu còn lại
- ~~Chỉ sau khi câu 1 GO mới làm 2, 5, 6, 7, 10~~ *Không áp dụng — 2/7/10 đã làm độc lập (chỉ đọc).*
- [x] Ghi log raw từng câu vào §5, **che tag 554** (câu 2/3/7/7b/8/9/10)
- ~~Đối chiếu trên **cTrader Web** (tab Positions): Quantity sau `38=1` phải hiện **0.01** và Position
      ID phải bằng `721` trong log (câu 4).~~ *→ Phase 7 Bước A.* Câu 3 phần web đã xong (ID 41, digits 2)
- ~~Chạy trong khung **05:00 → 03:59** (UTC+7)~~ *→ điều kiện của Phase 7 Bước A.*
- [x] Kiểm digits XAUUSD ở chân A (MT5) = 2 — xong, xem §3

### 6.4 Xác nhận môi trường mà macOS không kiểm được

- [x] QuickFIXn.Core + QuickFIXn.FIX4.4 restore/compile trên Windows → **Infrastructure/CTrader** (§3)
- [x] SSL 5212 hoạt động với QuickFIXn (`SSLEnable=Y`, `SSLServerName`, `SSLValidateCertificates=Y`) —
      TLS handshake OK, server trả Logout có nội dung (§5 câu 8)
- ~~**BẬT FIX API cho tài khoản demo 10649643**~~ *Không áp dụng — chủ dự án bỏ demo, dùng live 8220816 (2026-09-16).* — server trả `RET_ACCOUNT_DISABLED`. Chặn toàn bộ
      §5 cho tới khi bật. Diễn đàn cTrader (`community.ctrader.com/forum/fix-api/42276/`) có đúng ca
      "fxpro via fix api: RET_ACCOUNT_DISABLED" — trả lời cộng đồng: *"I don't think FxPro allows you to
      connect via FIX API"*; không có phản hồi chính thức, không nói demo/live
- [x] **Thử chẩn đoán trên LIVE 8220816 (yêu cầu chủ dự án 2026-09-16)** — host `live.cfixapi.com:5212`,
      SenderCompID `live.fxpro.8220816`. **Phạm vi thu hẹp: CHỈ logon + SecurityList (câu 8/9/3); KHÔNG
      NewOrderSingle.** Mục đích duy nhất: phân biệt "FxPro tắt FIX cho demo" với "tắt toàn bộ". Chủ dự
      án cho phép tường minh; Claude tạo template (không mật khẩu), chủ dự án điền mật khẩu, Claude chạy
      với stdin theo nhịp và che 554. **Kết quả: LOGON OK, SecurityList OK (§5 câu 8, 3).** → FxPro tắt
      FIX chỉ ở demo. File demo cất ở `C:\tmp\ctrader-spike\backup-demo\Config-dev.DEMO.cfg`

### 6.5 Dọn dẹp cuối phiên spike

- ~~`7\|spike-pos-final` → `728=2`~~ *→ Phase 7 Bước A (live, không phải demo).*
- [x] `store/` đã xoá · `log/` không tồn tại · `Config-dev*.cfg` giữ có chủ đích cho Phase 7

---

## 7. Kết luận hiện tại

**Cổng Phase 0 nay là GO-READ** (tái cấu trúc 2026-09-16): câu 1 (R1) chuyển sang Phase 7 Bước A.

**Trạng thái GO-READ:** demo 10649643 bị FxPro tắt FIX (`RET_ACCOUNT_DISABLED`); live 8220816 logon
được (SSL 5212/5211, `cServer`), SecurityList trên cả TRADE và QUOTE (`55=41 → XAUUSD, digits 2`),
`264=1` = spot (không X/278), `264=0` = depth. **Câu 2, 3, 7, 7b, 8, 9, 10 xong.** Baseline Windows 11 fail =
macOS, 0 regression. Layering: `Infrastructure/CTrader`.
→ **GO-READ ĐẠT (2026-09-17) → sang Phase 1.** Câu 1/4/5/6 (đặt lệnh tiền thật) chạy ở **Phase 7 Bước A**
trên live 8220816 sau khi nạp ≥ $30–50.

Đã xong: baseline macOS = 11 (kèm danh sách), xác nhận R10 (Sl/Tp và Commission chỉ để hiển thị),
`open_pending_time_ms = 30000` dư 15 lần ngưỡng Phase 7, `point = 100` khớp kỳ vọng XAUUSD.
Qua cTrader Web (2026-09-16): tài khoản **Hedging** (tiền đề R1), symbol 41 = XAUUSD digits 2,
lot size 100 Oz, demo không còn position mở. Chân A (MT5): XAUUSD **Digits 2, Contract size 100** →
hai sàn cùng digits và cùng contract size, R6 loại trừ. Panel FIX API của FxPro **khớp README §1 từng
trường**; mật khẩu FIX = mật khẩu tài khoản.

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
