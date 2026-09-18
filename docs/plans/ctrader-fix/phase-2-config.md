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
| 2 | Section cTrader trong `ConfigWindow.xaml` đặt ở đâu? | **ĐÃ QUYẾT (chủ dự án, 2026-09-17): UI 2 mode cho khối Sàn B.** Chọn MT4/MT5 → hiện **đúng UI cũ**; chọn cTrader → **ẩn** khối MT của B, **hiện** form cTrader riêng. Không để hai bộ ô cùng hiện (tránh nhập nhầm). Khối Sàn A giữ nguyên 100%. Chi tiết: mục "UI 2 mode cho Sàn B" bên dưới. |
| 3 | Khi `platform_b = ctrader`, ô `map_name_2` còn ý nghĩa gì? | **ĐÃ QUYẾT (chủ dự án, 2026-09-17): app tự dùng tên kênh cố định `CTRADER_B`, hiển thị read-only, người dùng không nhập.** Giá trị `mapNames[1]` MT đã lưu **không bị ghi đè**. `RuntimeConfigState.CurrentMapName2` trả `CTraderFixConfig.ChannelMapName` khi `platform_b == ctrader`. Hệ quả an toàn: không thể đọc nhầm EA MT sàn B còn đang chạy. |
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
| **Password** | Password — `PasswordBox` (masked) | `ctraderFix.password` | Cùng một mật khẩu cho cả QUOTE và TRADE → **một** ô, **một** giá trị. Panel FxPro ghi *"a/c 10649643 password"* → đây là **mật khẩu đăng nhập tài khoản cTrader**, không phải mật khẩu riêng cho FIX; đổi mật khẩu tài khoản là phải cập nhật ô này. Plaintext trong Supabase — xem "Ba quy tắc bảo vệ mật khẩu" |
| **SenderCompID: demo.fxpro.10649643** | SenderCompID | `senderCompId` | Dùng chung hai session. **Cảnh báo UI (không chặn):** nếu không bắt đầu bằng `demo.` thì hiện nhãn đỏ "Tài khoản LIVE" cạnh ô — cùng login FxPro có cả tài khoản live (8220816, 8225904), gõ nhầm tiền tố là đặt lệnh thật |
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
| 1 | `TradeDesktop.Application/Models/CTraderFixConfig.cs` (mới) | `sealed record` lồng `CTraderEndpoint(Host, PortSsl, PortPlain)` ×2 + các field trên; `Empty`; `Normalize()` (trim, `Math.Max(0, port)`, `symbolId >= 0`); `ResolveUsername()` tự tách từ `senderCompId` khi `username` rỗng; `ActivePort(role)` chọn theo `useSsl`; hằng `public const string ChannelMapName = "CTRADER_B"` (câu 3) |
| 2 | `TradeDesktop.Application/Helpers/SansJsonHelper.cs` | `BuildSans(..., CTraderFixConfig? ctraderFix = null)` ghi khối `ctraderFix` **chỉ khi khác `Empty`** (để `sans_json` của máy MT-MT không phình); `TryParseSans` thêm overload với `out CTraderFixConfig ctraderFix` — **hai overload cũ giữ nguyên chữ ký** |
| 3 | `TradeDesktop.Application/Services/ConfigService.cs:171` | `SaveByMachineHostNameAsync(..., IReadOnlyList<ManualHwndColumnConfig>? manualHwndColumns = null, CTraderFixConfig? ctraderFix = null, ...)` — tham số mới **có default** để call site cũ không đổi; `LoadByMachineHostNameAsync` trả thêm `CTraderFix` trong `ConfigLoadResult` |
| 4 | `TradeDesktop.App/State/RuntimeConfigState.cs` | `CurrentCTraderFixConfig`; method mới `UpdateCTraderFix(CTraderFixConfig)` **tách riêng** theo khuôn `UpdateManualTradeHwnd` — **không** nhét thêm tham số vào `Update(...)` vốn đã 30+ tham số và có hai overload (R7) |
| 5 | `TradeDesktop.App/ViewModels/ConfigViewModel.cs` | `:393` + `:449` (load): gọi `UpdateCTraderFix`; `:518` (save): truyền `ctraderFix` vào `SaveByMachineHostNameAsync`; `:534-536` (sau save): gọi `UpdateCTraderFix` để runtime nhận ngay; `CanSave` (~:555): khi `IsPlatformBCTrader` thì đòi `quote.host`, `trade.host`, cổng đang chọn theo `useSsl` `> 0`, `senderCompId`, `symbolId > 0`, `volumeBUnits > 0`, `contractSizeB > 0`, và **có mật khẩu** (đã lưu hoặc vừa nhập) |
| 6 | `TradeDesktop.App/ConfigWindow.xaml` | UI 2 mode cho khối Sàn B (mục "UI 2 mode cho Sàn B") |

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
| `TradeDesktop.App/State/RuntimeConfigState.cs` | Expose `CurrentCTraderFixConfig`; cập nhật qua method riêng `UpdateCTraderFix(...)` (điểm 4 của bảng "6 điểm" ở trên) — **không** nhét thêm tham số vào `Update(...)`. `CurrentMapName2`: trả `CTraderFixConfig.ChannelMapName` khi `CurrentPlatformB == "ctrader"`, còn lại trả map đã lưu (câu 3) |
| `TradeDesktop.App/ViewModels/ConfigViewModel.cs` (~:226-280) | Thêm `IsPlatformBCTrader` cạnh `IsPlatformBMt4/Mt5`; các property nhập liệu; `CanSaveCommand` (`:323`) theo mode — MT: giữ nguyên `MapName1` + `MapName2` không rỗng; cTrader: `MapName1` + trường cTrader bắt buộc (điểm 5), **không** đòi `MapName2` |
| `TradeDesktop.App/ConfigWindow.xaml` | UI 2 mode — xem mục dưới |

### UI 2 mode cho Sàn B (quyết 2026-09-17)

Khối Sàn B hiện tại (`ConfigWindow.xaml`): radio MT4/MT5 `:182-183`, Map Name + Check `:195-211`, CHART HWND B
`:221-235`, TRADE HWND B `:250-255`. Nút add/delete cột HWND nằm ở khối Sàn A (`:139-144`); **mỗi cột là một
profile nguyên tử A+B** (Rule G).

| # | Quy tắc |
|---|---|
| 1 | **Chỉ khối Sàn B có 2 mode.** Khối Sàn A không đổi một dòng XAML (cTrader chỉ ở B — ràng buộc #1). |
| 2 | Thêm radio **"cTrader"** cạnh MT4/MT5 của Sàn B, bind `IsPlatformBCTrader`. Mode **suy ra từ `PlatformB`** — không lưu cờ riêng. |
| 3 | **Mode MT4/MT5:** bọc nguyên các control Map Name B / CHART HWND B / TRADE HWND B hiện có vào một panel `Visibility` = không phải cTrader. **Không sửa nội dung bên trong** (binding, `ItemsControl` theo `ManualHwndColumns`) — chống hồi quy MT-MT. |
| 4 | **Mode cTrader:** panel MT của B bị ẩn (Collapsed); hiện panel cTrader soi gương panel FIX API theo bảng ánh xạ: khối QUOTE (host, port SSL, port plain), khối TRADE (host, port SSL, port plain), SenderCompID + nhãn cảnh báo khi không bắt đầu `demo.`, TargetCompID, Password (`PasswordBox`, tooltip "= mật khẩu tài khoản cTrader"), Username read-only tự suy, Symbol ID, Volume B (units) + gợi ý sống `= … lot`, Contract size, Volume A (lot) khai báo, dòng read-only **"Kênh B: CTRADER_B"**. |
| 5 | **Giữ dữ liệu mode đang ẩn — không xoá.** Chuyển MT ↔ cTrader không đụng `MapName2`, HWND B, hay các trường cTrader trong ViewModel. Save ghi **cả** `mapNames[1]` MT **và** khối `ctraderFix`. Ở mode cTrader, HWND B được bỏ qua khi kiểm tra (`IsCompleteFor(requiresExchangeBHwnd:false)`). |
| 6 | Bấm radio **không** bị chặn; quy tắc câu 4 (còn slot) chỉ chặn ở **Save**. |
| 7 | Không tách `ManualHwndColumns` theo mode; add/delete cột ở khối A vẫn tạo/xoá cả profile A+B như cũ. |

#### Mốc UI trước Phase 2 (chủ dự án chụp 2026-09-17, máy `laptop-eoj2n95d`, mode MT5)

Ảnh gốc nằm trong lịch sử trao đổi; mô tả để so sánh sau khi code — **mode MT4/MT5 phải trông y hệt**:

| Vùng | Nội dung quan sát |
|---|---|
| Tiêu đề | "Config" |
| **Sàn A** | Radio `MT4` / `MT5` (đang chọn MT5) cùng hàng với nhãn "Sàn A" |
| Map Name (A) | ô `Local\MT_A_Tick` · nút **Check** · nhãn "Chưa kiểm tra" |
| CHART HWND A | số thứ tự cột `1` phía trên · ô `0x002C0C64` (ô hẹp) · nút **delete** (mờ, vì chỉ 1 cột) và **add** bên dưới |
| TRADE HWND A | ô rộng `0x0002076C` |
| **Sàn B** | Radio `MT4` / `MT5` (đang chọn MT5) cùng hàng với nhãn "Sàn B" |
| Map Name (B) | ô `Local\MT_B_Tick` · nút **Check** · nhãn "Chưa kiểm tra" |
| CHART HWND B | ô hẹp `0x003C0F62` (không có số thứ tự, không có nút add/delete) |
| TRADE HWND B | ô rộng `0x00020AEC` |
| Chân trang | "✔ Đã tải config theo host name" bên trái · nút **Save** góc phải dưới |

Sau Phase 2, ở mode MT5: chỉ được phép **thêm radio "cTrader"** thứ ba trên hàng Sàn B; mọi thứ khác trong bảng
trên (vị trí, nhãn, giá trị, trạng thái nút) phải giữ nguyên.

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

### Unit test — kết quả 2026-09-17 (91 test mới, tất cả xanh)

File: `TradeDesktop.Tests/Config/CTraderFixConfigTests.cs`, `SansJsonCTraderFixTests.cs`, `CTraderPhase2RulesTests.cs`,
`ConfigServiceCTraderFixTests.cs`.

- [x] `sans_json` round-trip **có** `ctraderFix` → đọc lại đúng từng field — `RoundTrip_WithCTraderFix_ReadsBackEveryFieldIncludingPassword`.
- [x] Round-trip **không có** `ctraderFix` → `Empty`, không exception — `TryParseSans_ProductionJsonWithoutCTraderFix_…`, `Load_OldSansJson_ReturnsEmptyCTraderFix`.
- [x] **`sans_json` production hiện tại** (máy `laptop-eoj2n95d`, chỉ HWND công khai) parse đúng `mapNames` + `manualHwndColumns`.
- [x] `IsCompleteFor(false)` bỏ qua HWND B; `IsComplete` cũ không đổi — `IsCompleteFor_PlatformMatrix`, `IsComplete_LegacyPropertyUnchanged`.
- [x] `HwndHealthChecker` bỏ qua HWND B đúng lúc và chỉ đúng lúc — `HwndHealthChecker_DefaultStillRequiresB`,
      `…_CTraderSkipsBOnly`, `…_CTraderColumnWithOnlyStaleBValues_IsSkippedAsEmpty`; `HwndHealthCheckerTests` cũ vẫn xanh.
- [x] `PointDigitsConsistencyChecker`: khớp, lệch một bậc, lệch nhiều bậc, digits = 0, point = 0/âm, digits âm/quá lớn.
- [x] `HedgeVolumeConsistencyChecker`: ratio = 1, Warn/Alert theo `[0.95,1.05]` và `[0.8,1.2]`, số 0, `NaN`/`Infinity`,
      `contractSizeB = 0`/âm → `Invalid`, không chia cho 0.
- [x] Validation `volumeBUnits` (`> 0`, bội `0.01`) và `contractSizeB` (`> 0`).
- [x] `ResolveUsername()`: `demo.fxpro.10649643` → `10649643`; `live.x.y.z` → `z`; không dấu chấm → rỗng; ghi đè → giữ.
- [x] `ActivePort` SSL/plain cho Quote 5211/5201, Trade 5212/5202.
- [x] `BuildSans` với `Empty`/`null` → **không** có key `ctraderFix` và **byte-identical** output cũ — `BuildSans_EmptyCTraderFix_WritesNoKeyAndMatchesLegacyOutput`.
- [x] Round-trip bộ FxPro kể cả `password` (có ký tự `"` và `\`).
- [x] `Redact`: `password` → `***`, key khác nguyên; JSON không có password → trả nguyên; `null`/rỗng; JSON hỏng → không throw và vẫn che.
- [x] `ToString()` không chứa mật khẩu.
- [x] Luật định tuyến (`CTraderRoutingRules`, dùng bởi `CTraderRouting` ở App): `CTRADER_B_Trades`/`_History` khi ctrader → true;
      map A, map MT `Local\MT_B_*`, `platform_b = mt5`, `null`/rỗng → false.
- [x] `ChannelMapName == "CTRADER_B"`; hậu tố khớp `OrderMapNameResolver`.
- [x] Round-trip giữ **đồng thời** `mapNames[1] = Local\MT_B_Tick` và khối `ctraderFix`.
- [x] `IsDemoSender`: `demo.` → true (không phân biệt hoa thường); `live.` / rỗng → false.
- [x] `ConfigService.SaveByMachineHostNameAsync(..., ctraderFix:)` ghi khối vào `sans_json`; gọi kiểu cũ không ghi key;
      `LoadByMachineHostNameAsync` trả `CTraderFix`.
- [x] Khối `ctraderFix` hỏng kiểu dữ liệu **không** làm hỏng `mapNames`/`manualHwndColumns`.

> Ghi chú triển khai khác mô tả plan (đã báo trong Bước 3):
> - Tham số `ctraderFix` đặt **sau** `cancellationToken` (vẫn có default) để không đổi vị trí tham số có sẵn.
> - `IHwndHealthChecker` ở Application nên `Check(columns, requiresExchangeBHwnd = true)` test được trực tiếp.
> - Luật so khớp của `CTraderRouting` tách ra `CTraderRoutingRules` (Application) để test được.
> - Thêm `_runtimeConfigState.UpdateCTraderFix(result.CTraderFix)` ở đường nạp config lúc khởi động
>   (`DashboardViewModel`, cạnh `UpdateManualTradeHwnd`) — thiếu dòng này thì thông số cTrader "bốc hơi" sau restart.

### Ma trận platform

| A | B | HWND B bắt buộc? | `IsCompleteFor` | Kết quả |
|---|---|---|---|---|
| mt4 | mt4 | ✅ có | `false` khi thiếu B | ✅ `IsCompleteFor_PlatformMatrix` |
| mt4 | mt5 | ✅ có | `false` khi thiếu B | ✅ |
| mt5 | mt4 | ✅ có | `false` khi thiếu B | ✅ |
| mt5 | mt5 | ✅ có | `false` khi thiếu B | ✅ |
| mt4 | ctrader | ❌ không | `true` dù thiếu B | ✅ |
| mt5 | ctrader | ❌ không | `true` dù thiếu B | ✅ |

### Smoke test (Windows)

#### G1 + G2 — test blocker, quan trọng nhất phase này

- [x] (chủ dự án xác nhận 2026-09-17) `platform_b = ctrader`, **HWND B để trống hoàn toàn**:
  - [x] `_isHwndInvalid = false` — **không có popup** "Đã TẠM DỪNG mọi thao tác mở/đóng lệnh"
  - [x] (suy ra: không popup, không cảnh báo HWND) `HasManualTradeHwndConfig = true` → `IsManualTradeWarningVisible = false`
  - [ ] CHƯA KIỂM (log phiên chỉ có khi bấm Start → kiểm ở Phase 4) Log **không có** `[HWND][SKIP]` hay `[HWND][WARN] SKIP bật`
  - ↪ Lệnh chân A dispatch khi B là cTrader: **Phase 7 Bước B** (đặt lệnh thật — nguyên tắc gom tiền thật)
- [x] (2026-09-17, chủ dự án) Mode MT5 xoá một ô HWND B rồi Save → **bị chặn** với báo lỗi HWND B trống. Đổi ngược về `platform_b = mt5` với HWND B vẫn trống → **popup PHẢI xuất hiện trở lại**.
      Đây là kiểm tra cờ không bị kẹt ở `false`.

#### Phần còn lại

- [x] (chủ dự án xác nhận) **Mode MT5 y hệt trước Phase 2** — chụp lại cửa sổ Config sau khi code, so với bảng "Mốc UI trước Phase 2"
      (khác biệt duy nhất được phép: thêm radio "cTrader" trên hàng Sàn B).
- [x] (chủ dự án xác nhận, kèm nhãn ⚠ Tài khoản LIVE và gợi ý "= 0.01 lot") Chọn radio cTrader → khối Map Name B / HWND B **biến mất**, form cTrader **hiện**; khối Sàn A không đổi.
- [x] Nhập đủ thông số, Save, **restart app**, thông số còn nguyên — DB đọc lại: `ctraderFix` quote/trade `live.cfixapi.com` 5211/5201 · 5212/5202, SSL, `live.fxpro.8220816`, `cServer`, symbol `41`, volume `1`/`100`/`0.01`; `startup.log` 2 lần mở 15:04 và 15:08, 0 error.
- [x] (DB 2026-09-17: sau Save ở mode MT5 → `platform_b = mt5`, `mapNames[1] = Local\MT_B_Tick`, HWND B `0x003C0F62/0x00020AEC` còn nguyên, khối `ctraderFix` còn nguyên) Đổi về `mt5` → form cTrader ẩn; Map Name B (`Local\MT_B_Tick`) + HWND B cũ **còn nguyên**; đổi lại cTrader →
      thông số cTrader **còn nguyên** (quy tắc 5).
- [x] (chủ dự án xác nhận: giá B đứng yên) Ở mode cTrader, dashboard **không** hiện giá của EA `Local\MT_B_Tick` đang chạy (B disconnected tới Phase 4) —
      chứng minh `CurrentMapName2 = CTRADER_B`.
- [x] `sans_json` **cũ** (chưa có `ctraderFix`) parse không lỗi — unit test với bản production thật; mở app thật: CHƯA KIỂM (chưa có `ctraderFix`) → không lỗi.
- [x] (2026-09-17) Quét toàn bộ log hiện có (`startup.log`; chưa có log phiên vì smoke không bấm Start) bằng chính mật khẩu lưu trong DB, chỉ đếm: **0 file chứa mật khẩu**. Log phiên `[DB] sans=` sẽ kiểm lại ở Phase 4 khi có phiên chạy. Code đã: `[DB] sans=` qua `SansJsonHelper.Redact`; `RuntimeSummary` không nối `CurrentCTraderFixConfig` (grep 0); không có chỗ log `sans_json` nào khác (grep). Cần chạy app rồi grep log: **password không xuất hiện ở bất kỳ đâu** — kể cả khi bật log level thấp nhất, kể cả
      trong `[VM]` lúc load config và trong `RuntimeSummary`.
- [x] (DB: mật khẩu vẫn còn sau các lần Save ô trống — chỉ kiểm có/không) Save với ô mật khẩu **để trống** → mật khẩu cũ **còn nguyên** trong `sans_json`.
- [x] **Câu 4 (chặn đổi platform khi còn slot)** — luật tách thành hàm thuần `PlatformBSwitchGuard.Evaluate` (Application, rà soát Phase 3 ngày 2026-09-17), `ConfigViewModel` gọi nó; **`PlatformBSwitchGuardTests` 15/15 xanh** gồm cả hai ca có-slot. Smoke UI ca không-slot pass:
  - ↪ có 1 slot mở, đổi `platform_b` `mt5 → ctrader` → Save **bị từ chối** — cần slot thật → **Phase 7 Bước B** (code: `ConfigViewModel.SaveAsync` đọc `IPortfolioCoordinator.LiveAndPendingTotalCount`, gồm PendingOpen+Live+PendingClose)
  - ↪ có 1 slot mở, đổi `mt4 → mt5` → Save OK như trước — cần slot thật → **Phase 7 Bước B**
  - [x] không có slot (`current_slots.slots = []`), đổi `mt5 → ctrader` và `ctrader → mt5` → Save OK, cửa sổ tự đóng, DB đổi đúng.
- [x] (chủ dự án xác nhận) Reload config → `PasswordBox` trống, nhãn "đã lưu" hiện; app vẫn dùng được mật khẩu đã lưu.

### Gate chung

- [x] `dotnet test`: **766 tổng, 755 pass, 11 fail** — 11 fail trùng từng tên baseline memo §2.2b; +91 test Phase 2 xanh.
- [x] Build App: **0 error**; warning chỉ 3 `CA1416` baseline của Infrastructure (build ra `C:\tmp\p2build` vì app đang chạy khoá DLL).

---

## Rollback

Revert commit. `sans_json` đã ghi sẽ có thêm key `ctraderFix` thừa — **parser cũ bỏ qua key lạ nên vô
hại**, không cần dọn.

Kiểm tra lại điều này ở bước nghiệm thu: mở app bản cũ trên một config đã có `ctraderFix` xem có lỗi
không. Nếu parser cũ **không** dung thứ key lạ thì phải dọn `sans_json` khi rollback — ghi rõ vào
commit message.

---

## Cổng sang Phase 3

- [x] Toàn bộ checklist nghiệm thu pass — trừ log `[HWND][SKIP]` (kiểm ở Phase 4) và hai ca còn-slot (Phase 7 Bước B).
- [x] **Kết luận layering từ Phase 0 đã có** (`Infrastructure/CTrader`, memo §3): `QuickFIXn` restore được trên macOS hay phải tách project
      `TradeDesktop.CTrader` riêng. Phase 3 không bắt đầu được nếu chưa biết.
