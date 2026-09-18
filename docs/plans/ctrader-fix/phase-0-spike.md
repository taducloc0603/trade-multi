# Phase 0 — Spike khử rủi ro

> **Code vứt đi. Không đụng vào solution.** Nằm ngoài repo hoặc trong scratchpad.

[← Index](README.md) · Phase sau: [Phase 1](phase-1-platform-enum.md)

---

## Mục tiêu

Chứng minh bằng log raw FIX rằng thiết kế ở [README §2](README.md) là khả thi, **trước khi viết bất
kỳ dòng production nào**.

Lý do phase này tồn tại là **R1**: spec cTrader không nói thẳng cách đóng position. Bằng chứng hiện có
là mô tả tag 721 trong spec cộng **mã nguồn SDK chính thức** của Spotware (`quickfixnsamples.net/
ConsoleSample/Program.cs` set 721 ở nhánh market order; `FIX-API-Sample/MessageConstructor.cs:346`
comment *"Position ID, where this order should be placed. If not set, new position will be created"*).
Cái chưa được chứng minh là **order ngược chiều mang 721 có net về 0 trên tài khoản hedged không**.
Nếu sai thì không có đường nào đóng lệnh B và toàn bộ kế hoạch phải làm lại — không phải vá.

---

## Phụ thuộc phase trước

Không có. Đây là phase đầu tiên.

---

## Chốt trước khi code

| # | Câu hỏi | Vì sao cần quyết trước |
|---|---|---|
| 1 | Spike dùng cổng SSL (5211/5212) hay plain (5201/5202)? | Plain dễ đọc log raw hơn nhiều; SSL mới là thứ production dùng. Đề xuất: **plain cho spike, SSL cho production** — nhưng phải xác nhận broker cho phép plain. |
| 2 | Mật khẩu tài khoản 10649643 đưa vào spike bằng cách nào? | Nằm **plaintext** trong `Config-dev.cfg` của ConsoleSample (sample không đọc biến môi trường). Vì vậy: file phải ở **ngoài repo** (`/tmp/ctrader-spike/...`), `chmod 600`, **không** gõ mật khẩu vào lệnh shell (lưu history), **không** commit, và **xoá cùng `store/` + `log/`** ngay khi spike xong — `log/` của QuickFIX/n ghi Logon nguyên văn. |
| 3 | Được phép đặt lệnh thật trên demo trong giờ thị trường mở không? | Câu hỏi 1 của spike bắt buộc phải mở rồi đóng một position thật trên demo. |
| 4 | **Tài khoản demo có phải loại HEDGING không?** | R1 chỉ có nghĩa trên tài khoản hedged (tag 721 *"can be specified only for hedged accounts"*). Netting thì cách đóng khác hẳn. **ĐÃ XÁC NHẬN 2026-09-16 qua cTrader Web: `FxPro · Demo · 10649643 · Hedging`.** |

### Kiểm chéo bằng cTrader Web (thay cho cTrader desktop)

Máy dev không chắc có cTrader desktop. **cTrader Web** (`https://ct.fxpro.com/`) hiển thị đủ những gì
plan này cần đối chiếu, đọc được qua Playwright MCP ở chế độ **chỉ đọc** (ràng buộc #2 README §1 vẫn
giữ: không automate UI cTrader để đặt/đóng lệnh).

| Cần đối chiếu | Ở đâu trên cTrader Web | Đã đọc 2026-09-16 |
|---|---|---|
| FIX symbol ID, tên chuẩn, digits, lot size | Panel phải → **Symbol info** | ID **41**, **XAUUSD**, pip position **2**, min change **0.01**, lot size **100 Oz**, min qty **0.01 lot** |
| Loại tài khoản | Menu tài khoản (góc trên phải) | **Hedging** |
| Position còn mở / lịch sử | Tab **Positions** / **History** (cột Position ID, Quantity, Entry) | Positions 0, Orders 0. History **đã có 4 lệnh XAUUSD cũ** (lỗ tổng $-562.21) — không phải của spike |
| Giờ giao dịch XAUUSD | Symbol info → **Market hours** | Mỗi ngày **05:00 → 03:59:45 hôm sau (UTC+7)**; nghỉ ~1 giờ/ngày. Câu 1 và 4 phải chạy ngoài khung nghỉ |
| Panel FIX API (host/port/CompID) | **Không có trên web** — nút "FIX API" chỉ mở tài liệu | Lấy từ cTrader desktop (Cog → FIX API) hoặc email FxPro cấp |

> Cùng một đăng nhập web còn thấy **2 tài khoản Live** (8220816, 8225904). Mọi cfg spike chỉ dùng
> `demo.fxpro.10649643`; không bao giờ thử SenderCompID có tiền tố `live.`.

---

## Việc làm

### Không viết console app — dùng `ConsoleSample` chính thức của Spotware

`github.com/spotware/quickfixnsamples.net/ConsoleSample` đã là công cụ spike hoàn chỉnh: menu tương
tác cho **đúng** các message cần kiểm, in raw FIX cả hai chiều với dấu `|` (`Incoming:` / `Outgoing:`),
đã kèm `FIX44-CSERVER.xml`. **Zero code.**

```bash
git clone https://github.com/spotware/quickfixnsamples.net /tmp/ctrader-spike
cd /tmp/ctrader-spike/ConsoleSample
# Program.cs:16 đọc file tên CHÍNH XÁC là "Config-dev.cfg"
cp Config.cfg Config-dev.cfg && chmod 600 Config-dev.cfg
dotnet run
```

`Config-dev.cfg` — **mỗi lần chạy chỉ một session** (`Program.cs:34` lấy `settings.GetSessions().First()`),
nên cần hai file, chạy hai lần:

| | `Config-dev.cfg` cho TRADE | `Config-dev.cfg` cho QUOTE |
|---|---|---|
| `SocketConnectHost` | `demo-uk-eqx-01.p.c-trader.com` | `demo-uk-eqx-01.p.c-trader.com` |
| `SocketConnectPort` | `5202` (plain) / `5212` (SSL) | `5201` (plain) / `5211` (SSL) |
| `SenderCompID` | `demo.fxpro.10649643` | `demo.fxpro.10649643` |
| `SenderSubID` / `TargetSubID` | `TRADE` / `TRADE` | `QUOTE` / `QUOTE` |
| `TargetCompID` | `cServer` | `cServer` |
| `Username` / `Password` | `10649643` / *(mật khẩu)* | `10649643` / *(mật khẩu)* |
| Dùng cho câu | 1, 3, 4, 5, 6, 7, 8, 9, 10 | 2 |

> Mật khẩu nằm **plaintext** trong `Config-dev.cfg` → file phải ở **ngoài repo**, `chmod 600`, **xoá
> sau khi spike xong**. Sample dùng `FileStorePath=store` → tạo thư mục `store/` và `log/` cạnh file —
> `log/` chứa raw FIX **kể cả Logon có mật khẩu**; xoá luôn.

### Các câu hỏi phải trả lời dứt điểm

| # | Câu hỏi | Rủi ro liên quan |
|---|---|---|
| **1** | **Trên tài khoản hedged, market order NGƯỢC CHIỀU mang `721=<positionId>` với đủ volume có net position về 0 (ĐÓNG) không** — hay bị từ chối, hay mở position đối ứng? Bằng chứng SDK đã cho thấy "721 + market = tác động lên position có sẵn"; câu này chỉ còn kiểm phần netting. | **R1 — sống còn** |
| 2 | `264=1` đúng là SPOT (không phải full depth)? | R11 |
| 3 | `SecurityList` cho symbolId 41: `1007 SymbolName` có đúng là **XAUUSD** không? `1008 SymbolDigits` là gì — có khớp `point` đang cấu hình và symbol ở sàn A không? **Kiểm chéo không cần FIX — ĐÃ XONG qua cTrader Web** (xem bảng trên): ID 41 = XAUUSD, digits 2. Còn phải: (a) thấy `1007`/`1008` thật trong `35=y`; (b) **kiểm digits XAUUSD ở chân A (MT5)** cũng là 2 — đọc từ `-gap-tick.log` hoặc terminal; `point=100` trong DB chỉ nói app cấu hình vậy, không nói broker A báo giá mấy chữ số. | R6 |
| 4 | **Contract size của symbol 41 tại FxPro là bao nhiêu?** Tag 38 tính bằng đơn vị cơ sở, không phải lot. cTrader Web đã cho **Lot size = 100 Oz** → kỳ vọng `38=1` = `0.01` lot. Đặt `38=1` rồi đọc lại cột Quantity ở tab Positions của web — hiện `0.01` thì chốt `contractSizeB = 100`, **không hardcode**. | R5 |
| 5 | Market order partial fill có sinh nhiều `150=F` không? | R11 |
| 6 | Format free-text tag 58 khi bị reject (vì `103` luôn = 0)? | R11 |
| 7 | Hành vi seqnum/reconnect sau khi kill socket cưỡng bức? | §4.6 |
| 8 | **Xác nhận** `TargetCompID=cServer` logon thành công. Đã có đáp án từ `Config.cfg` chính thức của SDK Spotware (dùng `cServer`, khớp panel FxPro); chỉ cần thấy Logon được chấp nhận trong log là đóng. | — |
| 9 | **Xác nhận bằng log raw**: username ở tag **553**, password ở tag **554** trong Logon; tag **50** = `QUOTE`/`TRADE`. Mục đích: đóng vĩnh viễn điểm mâu thuẫn với tài liệu ngoài (xem [README — Phụ lục A](README.md)), vốn nói sai rằng mật khẩu nằm ở tag 50. | Phụ lục A |
| 10 | **`SecurityListRequest` có trả lời trên QUOTE session không?** SDK `FixClient.cs:235` và mọi ví dụ trong spec gửi nó qua **TRADE**. Nếu QUOTE không trả lời → Phase 4 phải mở cả TRADE session (chỉ để lấy SecurityList) dù mới ở giai đoạn giá. | Phase 4 |

### Kịch bản lệnh cho từng câu hỏi

Cú pháp menu: `<số>|<tham số>|...`. Mọi output `Incoming:`/`Outgoing:` là log raw cần lưu vào memo.

| Câu | Session | Gõ vào ConsoleSample | Đọc gì trong output |
|---|---|---|---|
| 9 | TRADE | *(tự động khi start)* | `Outgoing` Logon: phải có `553=10649643`, `554=…`, `50=TRADE`, `56=cServer`. **Không** có mật khẩu ở tag 50 |
| 8 | TRADE | *(tự động)* | `Incoming` `35=A` từ server = logon thành công với `cServer` |
| 3 | TRADE | `8\|spike-sec\|0` | `35=y`: tìm group có `55=41` → đọc `1007` (tên) và `1008` (digits) |
| 10 | QUOTE | `8\|spike-sec-q\|0` | `SecurityListRequest` có được trả lời trên QUOTE session không, hay `35=j` reject? Quyết định Phase 4 có cần mở TRADE chỉ để lấy SecurityList |
| 2 | QUOTE | `4\|41\|n` rồi `5\|41\|n`; sau đó `4\|41\|y` | `n` → sample gửi `264=1`: `35=W` phải có đúng 2 entry `269=0`/`269=1`. `y` → `264=0`: nhiều entry hơn = depth. Xác nhận chiều ngược của 264 |
| 4 | TRADE | `1\|spike-open-1\|41\|buy\|market\|1` | `35=8` với `150=F`, `39=2`, `721=<posId>` — **ghi lại posId**. Mở tab Positions trên cTrader Web: Quantity phải hiện **0.01** ⇒ contract size 100; Position ID trên web phải **bằng** `721` trong log |
| 5 | TRADE | *(quan sát câu 4)* | Đếm số `35=8` có `150=F` cho cùng `11`. Một hay nhiều? `14 CumQty` cộng dồn thế nào |
| **1** | TRADE | `1\|spike-close-1\|41\|sell\|market\|1\|<posId>` rồi `7\|spike-pos-1` | **Sống còn.** `35=8` của lệnh sell: `150=F` và `721` = posId cũ? Rồi `35=AP`: `728=2` (không còn position) = **ĐÓNG THÀNH CÔNG**. Nếu `728=0` với `721` mới hoặc `35=j` → **DỪNG** |
| 6 | TRADE | `1\|spike-rej\|41\|buy\|market\|999999999` | `35=8` với `150=8`/`39=8`, đọc **nguyên văn** tag `58`; xác nhận `103=0` |
| 7 | TRADE | Đang logged on → ngắt mạng/kill socket → `g` | Sample tự reconnect (`ReconnectInterval=2`); `Outgoing` Logon mới phải có `34=1` và `141=Y`; không có `35=2` ResendRequest |

> **Câu 4 và câu 1 mở/đóng position THẬT trên demo.** Chạy trong giờ thị trường mở, và luôn kết thúc
> bằng `7\|spike-pos-final` xác nhận `728=2` trước khi thoát.

### Kiểm tra offline song song (làm được trên macOS)

- [ ] `QuickFIXn.Core` + `QuickFIXn.FIX4.4` restore/compile được trên `darwin` / `net8.0`.
      **Chính việc `dotnet run` ConsoleSample trên máy dev macOS trả lời luôn câu này** — sample
      reference đúng hai package đó. Nếu kéo theo phụ thuộc Windows-only thì Phase 3 phải tách FIX ra
      project `TradeDesktop.CTrader` riêng để `TradeDesktop.Tests` vẫn reference được phần thuần —
      **phải biết điều này TRƯỚC khi chốt layering ở Phase 3**.
- [ ] Đọc `Common/QuickFixNApp.cs` của sample và **ghi nhận**: QuickFIX/n **không tự gắn** `553`/`554`
      từ `Username`/`Password` trong cfg — sample phải tự set trong `ToAdmin` (`:57-75`). Đây là
      pitfall #1 của Phase 3.
- [ ] Đọc `CurrentOpenPendingTimeMs` thực tế trong DB. Nếu `< 2000 ms` thì Phase 7 phải chỉnh config
      trước khi bật cTrader (xem [README §4.2](README.md)).
- [ ] Xác nhận **không có nhánh logic nào** rẽ theo `TradeSharedRecord.Sl/Tp` hay
      `HistorySharedRecord.Commission` (tin là chỉ hiển thị — R10).
- [ ] Đo baseline test hiện tại và **ghi lại con số đó** làm gate cho mọi phase sau:
      ```bash
      DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
      ```

---

## Rủi ro liên quan

R1 (sống còn) · R5 · R6 · R11 · §4.2 · §4.6

---

## Nghiệm thu

- [x] Memo, **câu 2, 3, 7, 7b, 8, 9, 10 kèm log raw FIX** (che 554) — memo §5. (Câu 1, 4, 5, 6 → Phase 7 Bước A.)
- [x] Tài khoản demo là **Hedging** (chốt #4) — xác nhận 2026-09-16 qua cTrader Web; live 8220816 cũng Hedging.
- [x] `store/` đã xoá; `log/` không bao giờ tồn tại (sample không có log factory); `Config-dev*.cfg`
      **giữ có chủ đích** cho Phase 7 Bước A.
- [x] Baseline test: Windows **11 fail / 642 pass**, danh sách = macOS, 0 regression — memo §2.2b, §4.4.
- [x] Layering: **`Infrastructure/CTrader`** (QuickFIXn restore/build sạch trên SDK 8) — memo §3.

### ⚠️ Tái cấu trúc 2026-09-16 — tách phần TIỀN THẬT ra khỏi Phase 0

FxPro tắt FIX cho demo (`RET_ACCOUNT_DISABLED`); mọi kiểm chứng FIX phải chạy trên **live 8220816**.
Chủ dự án quyết **gom toàn bộ việc chạm tiền thật vào MỘT phiên duy nhất** ở
[Phase 7](phase-7-execution.md) để nạp tiền/trả spread một lần và tận dụng chung position.

| Câu | Ở đâu bây giờ | Lý do |
|---|---|---|
| 2, 3, 7, 8, 9, 10 | **Phase 0** (chỉ đọc / kết nối) | Không đặt lệnh. 2/3/8/9/10 đã xong trên live (memo §5) |
| **4, 1, 5, 6** | **Phase 7 — Bước A "Spike 721"** | Đều là `NewOrderSingle` tiền thật |

**Vì sao trì hoãn R1 (câu 1) được:** chiều ĐỌC (Phase 3–6: QUOTE/TRADE session, SecurityList,
W/X, PositionReport) đã được chứng minh hoạt động trên live và **không phụ thuộc** cách đóng lệnh.
Nếu 721 không đóng được, thứ phải đổi chỉ là **chiều đóng của `CTraderTradeExecutor`** (Open API
`ProtoOAClosePositionReq`) — một file, không phải cả thiết kế. Phase 7 vẫn mở bằng spike 721 **trước**
khi bật executor, nên không có lệnh nào của app được gửi khi R1 chưa chứng minh.

### Điều kiện DỪNG (nay áp dụng tại Phase 7 Bước A)

> **Nếu câu 1 sai → DỪNG Phase 7**, giữ nguyên Phase 1–6, làm lại **chỉ** chiều đóng của executor
> (Open API `ProtoOAClosePositionReq(positionId, volume)`), rồi mới nghiệm thu tiếp.

---

## Rollback

Không có gì để rollback — code nằm ngoài repo, không có commit nào.

Ngoại lệ duy nhất: nếu spike mở position thật trên demo thì phải đóng sạch trước khi kết thúc.

---

## Cổng sang Phase 1

Sau tái cấu trúc, cổng Phase 0 → 1 là **"GO-READ"** (chiều đọc + kết nối đã chứng minh):

- [x] Câu 8, 9 (logon SSL, cấu trúc 553/554) · câu 3 (`55=41 → XAUUSD`, digits 2) · câu 10 (SecurityList
      trên QUOTE) · câu 2 (`264=1` = spot; spot không có X/278) — memo §5.
- [x] Câu 7 (reconnect: `x` → `g`) — 2026-09-17: không `35=2`, seqnum reset về 1 (store `2 : 2` sau
      Logon mới). Phát hiện 7b: cServer reject 553/554 trên Logout → Phase 3 chỉ gắn vào `35=A`.
- [x] Baseline Windows + danh sách 11 fail, 0 regression (memo §2.2b, §4).
- [x] Layering: `Infrastructure/CTrader`.
- [x] `store/` đã xoá (chứa Logon có mật khẩu); `log/` không bao giờ được tạo (sample không có log factory).
- [x] `Config-dev.cfg` / `Config-dev.LIVE-*.cfg`: giữ cho Phase 7. Mật khẩu từng hiện trên console sample
      (memo §3); chủ dự án chấp nhận rủi ro — tài khoản dev, kiểm soát số tiền.

→ **Sang Phase 1 khi GO-READ đủ.** R1 (câu 1) chuyển thành cổng mở của Phase 7.
