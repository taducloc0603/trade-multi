# Phase 2 — Cấu hình UI + persistence, chưa kết nối

> **Vẫn không có một dòng code FIX nào.** Phase này chỉ làm chỗ để nhập và lưu thông số kết nối.

[← Phase 1](phase-1-platform-enum.md) · [Index](README.md) · Phase sau: [Phase 3](phase-3-fix-core-offline.md)

---

## Mục tiêu

Người dùng nhập được đầy đủ thông số FIX trong ConfigWindow, Save, restart app, thông số còn nguyên.
Đồng thời gỡ ràng buộc HWND ở chân B — vốn vô nghĩa với cTrader.

---

## Phụ thuộc phase trước

- Phase 1 đã merge: `"ctrader"` là giá trị hợp lệ cho `platform_b` ở cả 3 normalizer.

---

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | Mật khẩu FIX nằm ở đâu? | **ĐÃ QUYẾT (chủ dự án, 2026-09-16): ô nhập masked trong ConfigWindow, lưu `ctraderFix.password` trong `configs.sans_json`.** Hệ quả bắt buộc — xem mục "Ba quy tắc bảo vệ mật khẩu" bên dưới. Lựa chọn `.env` bị loại vì đổi mật khẩu phải sửa file + restart. |
| 2 | Section cTrader trong `ConfigWindow.xaml` đặt ở đâu? | Ngay **dưới khối sàn B** (sau `:182-183` nơi có radio platform B), enable/disable theo `IsPlatformBCTrader`. |
| 3 | Khi `platform_b = ctrader`, ô `map_name_2` còn ý nghĩa gì? | **Còn** — nó vẫn là khoá định tuyến cho `ICTraderRouting` và là nhãn hiển thị trên panel. Đổi nhãn thành "Tên kênh B (logic)" và bỏ nút kiểm tra map tồn tại. |
| **4** | **Đổi `platform_b` giữa phiên khi đang có position B mở thì sao?** | **ĐÃ QUYẾT (chủ dự án, 2026-09-16): từ chối Save.** Điều kiện chặn — **cả ba** cùng đúng: (a) `platform_b` **thay đổi** so với giá trị đang chạy, (b) giá trị cũ **hoặc** mới là `ctrader`, (c) `current_slots` không rỗng. Thông báo ở ConfigWindow: *"Đang có N slot mở — đóng hết trước khi đổi nền tảng sàn B."* Đổi `mt4 ↔ mt5` **giữ nguyên hành vi hiện tại** (không chặn) để không chạm đường MT-MT đang chạy production. Lý do chặn: ticket cTrader được mã hoá namespace (R4), sau khi đổi sẽ không khớp bất kỳ MMF nào → recovery discard → position B thật thành mồ côi. Kiểm ở `ConfigViewModel.CanSave`/`SaveAsync` bằng `_runtimeConfigState` + số slot từ coordinator; **không** sửa coordinator. |

---

## Việc làm

### Persistence — `sans_json`, không thêm cột DB

`TradeDesktop.Application/Helpers/SansJsonHelper.cs` (`TryParseSans` ~:20, `BuildSans` ~:57-100):
thêm khối `ctraderFix` cạnh `mapNames` và `manualHwndColumns`.

```json
"ctraderFix": {
  "quote": { "host": "demo-uk-eqx-01.p.c-trader.com", "portSsl": 5211, "portPlain": 5201 },
  "trade": { "host": "demo-uk-eqx-01.p.c-trader.com", "portSsl": 5212, "portPlain": 5202 },
  "useSsl": true,
  "senderCompId": "demo.fxpro.10649643",
  "targetCompId": "cServer",
  "password": "<plaintext — xem quy tắc bảo vệ>",
  "username": "",
  "symbolId": 41,
  "symbolName": "",
  "volumeBUnits": 1,
  "contractSizeB": 100,
  "volumeALots": 0.01
}
```

### Ba quy tắc bảo vệ mật khẩu (hệ quả của quyết định lưu trong `sans_json`)

Mật khẩu nằm **plaintext** trong `configs.sans_json` trên Supabase. Đây là lựa chọn có chủ đích của chủ
dự án (tiện đổi, không restart). Đổi lại, ba quy tắc sau là **bắt buộc**, có test, và ghi vào CLAUDE.md
ở Phase 8:

| # | Quy tắc | Nơi thực thi |
|---|---|---|
| 1 | **Không bao giờ log `sans_json` thô.** Mọi chỗ đang log chuỗi `sans_json` (grep `SansJson` trong `[VM]`, `[CONFIG]`, Supabase repo) phải đi qua `SansJsonHelper.Redact(json)` — thay giá trị `password` bằng `***` trước khi ghi. | `SansJsonHelper.Redact` (mới) + audit call site ở Phase 2 |
| 2 | **Không bao giờ log tag 554.** Transport che `554=***` trong mọi log raw FIX của app; QuickFIX/n `FileLogPath` **không bật** (nó ghi Logon nguyên văn ra đĩa). | `QuickFixCTraderTransport` (Phase 3/4) — đã có test ở Phase 3 |
| 3 | **`CTraderFixConfig.ToString()` / record equality không lộ mật khẩu.** Record C# mặc định in mọi property → override `ToString()` hoặc đánh dấu để `password` in ra `***`. `RuntimeSummary` (`DashboardViewModel` ~:3800) **không** được nối `CurrentCTraderFixConfig` vào. | `CTraderFixConfig` (Phase 2) |

UI: `PasswordBox` của WPF không bind hai chiều được như `TextBox` — dùng `PasswordChanged` → set vào
ViewModel; khi load config **không** đổ mật khẩu ngược vào `PasswordBox` (chỉ hiện nhãn "đã lưu");
ô trống khi Save = **giữ mật khẩu cũ**, không xoá.

### Bảng ánh xạ: panel FIX API của cTrader → ô nhập → nơi lưu

Cột "Panel" là **nguyên văn** những gì cTrader desktop hiển thị (Cog → FIX API). UI ConfigWindow phải
**bố cục y hệt** hai khối QUOTE/TRADE để người dùng chép sang, không phải suy nghĩ.

| Panel cTrader hiển thị | Ô nhập trong ConfigWindow | Lưu ở | Ghi chú |
|---|---|---|---|
| **QUOTE · Host name** | Quote → Host | `ctraderFix.quote.host` | |
| **QUOTE · Port: 5211 (SSL), 5201 (Plain)** | Quote → Port SSL / Port Plain | `quote.portSsl` / `quote.portPlain` | Nhập cả hai; app chọn theo `useSsl` |
| **TRADE · Host name** | Trade → Host | `ctraderFix.trade.host` | Thường trùng QUOTE nhưng **không giả định** — SDK `ApiCredentials` cũng tách `QuoteHost`/`TradeHost` |
| **TRADE · Port: 5212 (SSL), 5202 (Plain)** | Trade → Port SSL / Port Plain | `trade.portSsl` / `trade.portPlain` | |
| *(không có trên panel)* | Checkbox **Dùng SSL** | `useSsl` | Mặc định `true` (production). Bỏ tick chỉ để debug |
| **Password** | Password — `PasswordBox` (masked) | `ctraderFix.password` | Cùng một mật khẩu cho cả QUOTE và TRADE → **một** ô, **một** giá trị. Plaintext trong Supabase — xem "Ba quy tắc bảo vệ mật khẩu" |
| **SenderCompID: demo.fxpro.10649643** | SenderCompID | `senderCompId` | Dùng chung hai session |
| **TargetCompID: cServer** | TargetCompID | `targetCompId` | Mặc định `cServer`; để sửa được phòng broker khác |
| **SenderSubID: QUOTE / TRADE** | *(không có ô)* | *(không lưu)* | **Cố định theo session**: QUOTE session gửi `50=QUOTE`/`57=QUOTE`, TRADE session gửi `TRADE`. Không cho người dùng sửa — sai là logon fail im lặng |
| *(không có trên panel — nằm trong SenderCompID)* | Username (tag 553) — **read-only, tự suy** | `username` | Tự tách đoạn cuối của `senderCompId` theo `<env>.<broker>.<login>` → `10649643`. Lưu rỗng = tự suy; chỉ ghi giá trị khi người dùng bấm "Ghi đè" |
| **FIX symbol ID 41** | Symbol ID | `symbolId` | Số nguyên. Đọc từ cửa sổ thông tin symbol của cTrader |
| *(runtime)* | Symbol name — **read-only** | `symbolName` | Điền từ `SecurityList` tag 1007 sau khi logon; lưu lại để hiển thị khi offline |
| **Volume: 1 = 0.01** | Volume B (units) | `volumeBUnits` | Tag 38. Cạnh ô nhập hiện **gợi ý sống** `= volumeBUnits / contractSizeB lot` → gõ `1` thấy `= 0.01 lot` |
| *(không có trên panel)* | Contract size B | `contractSizeB` | Mặc định `100`; Phase 0 xác minh. Là mẫu số của gợi ý trên |
| *(không có trên panel)* | Volume A (lot) — khai báo | `volumeALots` | Chỉ để `HedgeVolumeConsistencyChecker` cảnh báo; **không** cưỡng chế lên MT |

### Đường LƯU và ĐỌC — phải sửa đủ 6 điểm, thiếu một là giá trị "bốc hơi" sau restart

Đường hiện tại (đã lần theo code):

```
LƯU   ConfigWindow.xaml ──► ConfigViewModel.SaveAsync (:518)
        └─► ConfigService.SaveByMachineHostNameAsync(mapName1, mapName2, platformA, platformB, columns)  (:171)
              └─► SansJsonHelper.BuildSans(mapName1, mapName2, columns)                                  (:57)
                    └─► IConfigRepository.UpdateSansAndHostNameByHostNameAsync(host, sansJson, pA, pB)   → configs.sans_json
        └─► _runtimeConfigState.Update(...) (:534) + UpdateManualTradeHwnd(columns) (:536)               ← cập nhật runtime NGAY, không chờ reload

ĐỌC   SupabaseConfigRepository ──► ConfigRecord.SansJson
        └─► ConfigService.LoadByMachineHostNameAsync ──► SansJsonHelper.TryParseSans(sansJson, out m1, out m2, out columns)  (:20)
              └─► ConfigViewModel (:393) _runtimeConfigState.Update(...) + (:449) UpdateManualTradeHwnd(...)
```

Sáu điểm phải chạm, theo đúng thứ tự dữ liệu đi:

| # | File | Sửa gì |
|---|---|---|
| 1 | `TradeDesktop.Application/Models/CTraderFixConfig.cs` (mới) | `sealed record` lồng `CTraderEndpoint(Host, PortSsl, PortPlain)` ×2 + các field trên; `Empty`; `Normalize()` (trim, `Math.Max(0, port)`, `symbolId >= 0`); `ResolveUsername()` tự tách từ `senderCompId` khi `username` rỗng; `ActivePort(role)` chọn theo `useSsl` |
| 2 | `TradeDesktop.Application/Helpers/SansJsonHelper.cs` | `BuildSans(..., CTraderFixConfig? ctraderFix = null)` ghi khối `ctraderFix` **chỉ khi khác `Empty`** (để `sans_json` của máy MT-MT không phình); `TryParseSans` thêm overload với `out CTraderFixConfig ctraderFix` — **hai overload cũ giữ nguyên chữ ký** |
| 3 | `TradeDesktop.Application/Services/ConfigService.cs:171` | `SaveByMachineHostNameAsync(..., IReadOnlyList<ManualHwndColumnConfig>? manualHwndColumns = null, CTraderFixConfig? ctraderFix = null, ...)` — tham số mới **có default** để call site cũ không đổi; `LoadByMachineHostNameAsync` trả thêm `CTraderFix` trong `ConfigLoadResult` |
| 4 | `TradeDesktop.App/State/RuntimeConfigState.cs` | `CurrentCTraderFixConfig`; method mới `UpdateCTraderFix(CTraderFixConfig)` **tách riêng** theo khuôn `UpdateManualTradeHwnd` — **không** nhét thêm tham số vào `Update(...)` vốn đã 30+ tham số và có hai overload (R7) |
| 5 | `TradeDesktop.App/ViewModels/ConfigViewModel.cs` | `:393` + `:449` (load): gọi `UpdateCTraderFix`; `:518` (save): truyền `ctraderFix` vào `SaveByMachineHostNameAsync`; `:534-536` (sau save): gọi `UpdateCTraderFix` để runtime nhận ngay; `CanSave` (~:555): khi `IsPlatformBCTrader` thì đòi `quote.host`, `trade.host`, cổng đang chọn theo `useSsl` `> 0`, `senderCompId`, `symbolId > 0`, `volumeBUnits > 0`, `contractSizeB > 0`, và **có mật khẩu** (đã lưu hoặc vừa nhập) |
| 6 | `TradeDesktop.App/ConfigWindow.xaml` | Section theo bảng ánh xạ; enable theo `IsPlatformBCTrader` |

> **`UpdateSansAndHostNameByHostNameAsync` và bảng `configs` không đổi** — `sans_json` vẫn là một cột
> text. Đây là lý do chọn gom vào `sans_json` thay vì thêm ~14 cột.

> **Tương thích ngược là bắt buộc.** `sans_json` hiện có trên production **không có** key này.
> Thiếu key ⇒ trả giá trị mặc định an toàn, **không** ném exception, **không** làm hỏng việc parse
> `mapNames`/`manualHwndColumns`.

Model mới: `TradeDesktop.Application/Models/CTraderFixConfig.cs` — `sealed record` với `Empty` và
`Normalize()`, theo đúng khuôn `ManualHwndColumnConfig`.

### Runtime + UI

| File | Việc |
|---|---|
| `TradeDesktop.App/State/RuntimeConfigState.cs` | Expose `CurrentCTraderFixConfig`; cập nhật qua method riêng `UpdateCTraderFix(...)` (điểm 4 của bảng "6 điểm" ở trên) — **không** nhét thêm tham số vào `Update(...)` |
| `TradeDesktop.App/ViewModels/ConfigViewModel.cs` (~:226-280) | Thêm `IsPlatformBCTrader` cạnh `IsPlatformBMt4/Mt5`; các property nhập liệu; validate ở `CanSave` (~:555) |
| `TradeDesktop.App/ConfigWindow.xaml` | Radio thứ ba cho sàn B; section bố cục **soi gương panel cTrader** (khối QUOTE, khối TRADE, rồi phần chung) theo bảng ánh xạ ở trên. Ô `volumeBUnits` có gợi ý sống `= … lot`; ô Username read-only tự suy từ SenderCompID |

### Gỡ ràng buộc HWND ở chân B · ⛔ BLOCKER, không phải tinh chỉnh UX

`ManualHwndColumnConfig.IsComplete` (`:11`) hiện đòi đủ **cả 4** HWND. Khi B là cTrader thì
`ChartHwndB` và `TradeHwndB` vô nghĩa và sẽ để trống.

**Nếu không làm việc này, app bị khoá hoàn toàn khi `platform_b = ctrader`** — kể cả chân A đang
hoàn toàn khoẻ. Hai đường độc lập cùng dẫn đến chỗ chết:

**Đường 1 — health check khoá toàn bộ lệnh (G2)**

```
RunHwndHealthCheck (DashboardViewModel.cs:826)
  → issues.Count > 0
    → _isHwndInvalid = true  (:842)
      → popup "Đã TẠM DỪNG mọi thao tác mở/đóng lệnh"
      → SkipIfHwndInvalid (:873) chặn MỌI thao tác lệnh
```

Chạy ở 3 nơi: `:1078` lúc Start, `:499` khi save config, `:4008` định kỳ.

**Đường 2 — gate lệnh thủ công (G1)**

```
DashboardViewModel.cs:3807
  HasManualTradeHwndConfig = CurrentManualHwndColumns.Any(x => x.IsComplete);
    → CanManualOpen()  (:776) = false
    → CanManualClose() (:782) = false
    → IsManualTradeWarningVisible (:608) = true
```

**Việc phải làm:**

| File | Việc |
|---|---|
| `TradeDesktop.Application/Models/ManualHwndColumnConfig.cs` (`:11`) | Thêm overload `IsCompleteFor(bool requiresExchangeBHwnd)`. **Giữ nguyên `IsComplete` cũ** để không đổi hành vi đường MT |
| `TradeDesktop.Application/Services/HwndHealthChecker.cs` | Nhận cờ `requiresExchangeBHwnd`; bỏ qua `ChartHwndB`/`TradeHwndB` khi `false` |
| `TradeDesktop.App/ViewModels/DashboardViewModel.cs:3807` | **Call site dễ bị bỏ sót nhất** — chuyển sang `IsCompleteFor(...)` với cờ từ `platform_b` |
| `TradeDesktop.App/ViewModels/DashboardViewModel.cs:831` | Truyền cờ vào `_hwndHealthChecker.Check(...)` |

> **Nguyên tắc:** cờ đi từ ngoài vào. Một service ở Application tự đi đọc `platform_b` là thêm phụ
> thuộc ẩn và phá tính test được của nó.

### Định tuyến + checker (chưa wire)

| File mới | Nội dung |
|---|---|
| `TradeDesktop.Application/Abstractions/ICTraderRouting.cs` | 3 thành viên như [README §2](README.md) |
| `TradeDesktop.App/Services/CTraderRouting.cs` | Impl đọc `IRuntimeConfigProvider`; so khớp **chính xác** với `OrderMapNameResolver.BuildTradeMapName(CurrentMapName2)` |
| `TradeDesktop.Application/Services/CTrader/PointDigitsConsistencyChecker.cs` | `Check(int configuredPoint, int ctraderDigits)` — kỳ vọng `point == 10^digits` (R6) |
| `TradeDesktop.Application/Services/CTrader/HedgeVolumeConsistencyChecker.cs` | `(volumeALots, volumeBUnits, contractSizeB) → ratio = (volumeBUnits / contractSizeB) / volumeALots` (R5). `contractSizeB` đến từ config, **không hardcode 100** |

Hai checker **chưa được wire vào đâu cả** ở phase này — chỉ tồn tại và có test.

---

## Rủi ro liên quan

**R5**, **R6** (định nghĩa checker) · **R7** (hai overload `Update` trong `RuntimeConfigState`)

---

## Nghiệm thu

### Unit test (macOS)

- [ ] `sans_json` round-trip **có** `ctraderFix` → đọc lại đúng từng field.
- [ ] `sans_json` round-trip **không có** `ctraderFix` → giá trị mặc định, không exception.
- [ ] **`sans_json` production hiện tại** (copy một bản thật, che thông tin nhạy cảm) vẫn parse đúng
      `mapNames` và `manualHwndColumns`.
- [ ] `IsCompleteFor(requiresExchangeBHwnd: false)` bỏ qua HWND B; `IsComplete` cũ **không đổi hành vi**.
- [ ] `HwndHealthChecker` bỏ qua HWND B đúng lúc **và chỉ đúng lúc**.
- [ ] `PointDigitsConsistencyChecker`: khớp, lệch một bậc, lệch nhiều bậc, digits = 0, point = 0.
- [ ] `HedgeVolumeConsistencyChecker`: ratio = 1, ngoài `[0.95, 1.05]`, ngoài `[0.8, 1.2]`, số 0,
      `NaN`/`Infinity`, `contractSizeB = 0` (phải báo lỗi, không chia cho 0).
- [ ] Validation `volumeBUnits`: `> 0`, hữu hạn, bội của `0.01`; `contractSizeB`: `> 0`, hữu hạn.
- [ ] `CTraderFixConfig.ResolveUsername()`: `demo.fxpro.10649643` → `10649643`; `live.x.y.z` → `z`;
      chuỗi không có dấu chấm → rỗng (không throw); `username` đã ghi đè → trả nguyên giá trị ghi đè.
- [ ] `ActivePort(Quote)` với `useSsl=true` → `5211`, `false` → `5201`; tương tự Trade `5212`/`5202`.
- [ ] `BuildSans` với `ctraderFix == Empty` → **không** có key `ctraderFix` trong JSON (máy MT-MT không
      phình `sans_json`).
- [ ] Round-trip **đúng bộ giá trị FxPro** ở bảng ánh xạ → đọc lại từng field bằng nhau, **kể cả `password`**.
- [ ] `SansJsonHelper.Redact(json)`: `password` → `***`; các key khác nguyên vẹn; JSON không có
      `password` → trả nguyên; JSON hỏng → không throw.
- [ ] `CTraderFixConfig.ToString()` không chứa giá trị mật khẩu.
- [ ] `CTraderRouting` so khớp map: đúng map B → true; map A → false; `platform_b = mt5` → luôn false;
      `null`/rỗng → false.

### Ma trận platform

Xem [README — Ma trận platform được hỗ trợ](README.md). Test ở tầng config, chạy được trên macOS:

| A | B | HWND B bắt buộc? | `IsCompleteFor` | ☐ |
|---|---|---|---|---|
| mt4 | mt4 | ✅ có | `false` khi thiếu B | ☐ |
| mt4 | mt5 | ✅ có | `false` khi thiếu B | ☐ |
| mt5 | mt4 | ✅ có | `false` khi thiếu B | ☐ |
| mt5 | mt5 | ✅ có | `false` khi thiếu B | ☐ |
| mt4 | ctrader | ❌ không | `true` dù thiếu B | ☐ |
| mt5 | ctrader | ❌ không | `true` dù thiếu B | ☐ |

### Smoke test (Windows)

#### G1 + G2 — test blocker, quan trọng nhất phase này

- [ ] `platform_b = ctrader`, **HWND B để trống hoàn toàn**:
  - [ ] `_isHwndInvalid = false` — **không có popup** "Đã TẠM DỪNG mọi thao tác mở/đóng lệnh"
  - [ ] `HasManualTradeHwndConfig = true` → `IsManualTradeWarningVisible = false`
  - [ ] Log **không có** `[HWND][SKIP]` hay `[HWND][WARN] SKIP bật`
  - [ ] Lệnh chân A dispatch bình thường (chân B fail vì null executor — đúng như kỳ vọng Phase 1)
- [ ] Đổi ngược về `platform_b = mt5` với HWND B vẫn trống → **popup PHẢI xuất hiện trở lại**.
      Đây là kiểm tra cờ không bị kẹt ở `false`.

#### Phần còn lại

- [ ] Chọn `platform_b = ctrader` → section cTrader bật, ô HWND B mờ đi.
- [ ] Nhập đủ thông số, Save, **restart app**, thông số còn nguyên.
- [ ] Đổi về `mt5` → section cTrader tắt, ô HWND B bật lại, HWND cũ còn nguyên.
- [ ] Mở config trên một máy có `sans_json` **cũ** (chưa có `ctraderFix`) → không lỗi.
- [ ] Grep log: **password không xuất hiện ở bất kỳ đâu** — kể cả khi bật log level thấp nhất, kể cả
      trong `[VM]` lúc load config và trong `RuntimeSummary`.
- [ ] Save với ô mật khẩu **để trống** → mật khẩu cũ **còn nguyên** trong `sans_json`.
- [ ] **Câu 4 (chặn đổi platform khi còn slot)** — ba ca:
  - [ ] có 1 slot mở, đổi `platform_b` `mt5 → ctrader` → Save **bị từ chối**, thông báo nêu đúng N.
  - [ ] có 1 slot mở, đổi `platform_b` `mt4 → mt5` → Save **OK như trước** (không hồi quy MT-MT).
  - [ ] không có slot, đổi `mt5 → ctrader` → Save OK.
- [ ] Reload config → `PasswordBox` trống, nhãn "đã lưu" hiện; app vẫn dùng được mật khẩu đã lưu.

### Gate chung

- [ ] Test suite không tăng so với baseline Phase 0. Build sạch.

---

## Rollback

Revert commit. `sans_json` đã ghi sẽ có thêm key `ctraderFix` thừa — **parser cũ bỏ qua key lạ nên vô
hại**, không cần dọn.

Kiểm tra lại điều này ở bước nghiệm thu: mở app bản cũ trên một config đã có `ctraderFix` xem có lỗi
không. Nếu parser cũ **không** dung thứ key lạ thì phải dọn `sans_json` khi rollback — ghi rõ vào
commit message.

---

## Cổng sang Phase 3

- [ ] Toàn bộ checklist nghiệm thu pass.
- [ ] **Kết luận layering từ Phase 0 đã có**: `QuickFIXn` restore được trên macOS hay phải tách project
      `TradeDesktop.CTrader` riêng. Phase 3 không bắt đầu được nếu chưa biết.
