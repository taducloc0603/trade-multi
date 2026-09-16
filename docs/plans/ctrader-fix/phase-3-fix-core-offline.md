# Phase 3 — Lõi FIX offline, không wire vào đường live

> **Toàn bộ phase này là dead code.** Không có gì được đăng ký vào DI đường live. Rủi ro runtime = 0.
> Đổi lại, **toàn bộ phần logic nguy hiểm được đưa vào diện test trên macOS**.

[← Phase 2](phase-2-config.md) · [Index](README.md) · Phase sau: [Phase 4](phase-4-quote-feed.md)

---

## Mục tiêu

Xây và test kỹ mọi thứ có thể test mà không cần kết nối, không cần Windows, không cần broker.

Đây là phase quan trọng nhất về mặt chất lượng: `TradeDesktop.Tests` **không reference project App**,
nên bất cứ thứ gì đặt sai tầng sẽ **không bao giờ được test trên bất kỳ OS nào**. Mọi logic rủi ro
phải nằm ở Application/Infrastructure, và phase này là lúc quyết định điều đó.

---

## Phụ thuộc phase trước

- Phase 2 đã merge: `CTraderFixConfig`, `ICTraderRouting`, hai checker đã tồn tại.
- **Kết luận layering từ Phase 0**: `QuickFIXn` restore/compile được trên `darwin`/`net8.0` hay không.

---

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | Lõi FIX đặt ở `TradeDesktop.Infrastructure/CTrader/` hay project mới `TradeDesktop.CTrader`? | Theo kết quả Phase 0. Nếu QuickFIXn sạch trên macOS → **Infrastructure**, ít xáo trộn hơn. Nếu kéo phụ thuộc Windows-only → **project riêng**, để Tests vẫn reference được phần thuần. |
| 2 | Ranh giới QuickFIX/n đặt ở đâu? | **Ba tầng, rõ ràng:** (a) **Application** — zero `using QuickFix`, chỉ DTO thuần (`TradeSharedRecord`, `CTraderSessionHealth`, codec, checker). (b) **Infrastructure/CTrader** — được dùng **kiểu message** của QuickFIX/n (`QuickFix.FIX44.NewOrderSingle`, `MarketDataRequest`, group `NoMDEntryTypesGroup`…) để dựng/parse, như SDK Spotware làm — đó là lý do test build message có giá trị. (c) **`QuickFixCTraderTransport`** — lớp **duy nhất** chạm `SocketInitiator` / `Session` / `IApplication` / `SessionSettings`. `FakeCTraderTransport` thay lớp (c), còn (b) test được thật với message thật. |
| 4 | Một hay hai `SocketInitiator`? | **Hai — một cho QUOTE, một cho TRADE**, mỗi cái `IApplication` + `SessionSettings` riêng. Đây là topology SDK Spotware dùng trong `AspNetCoreSample/Services/FixClient.cs:16-47` (`_quoteInitiator`, `_tradeInitiator`, `_quoteApp`, `_tradeApp`). Lợi trực tiếp: Phase 4 start riêng QUOTE, Phase 5 start thêm TRADE, không đụng nhau; `OnCreate` của mỗi app chỉ giữ một `Session`. |
| 3 | `CTraderQuoteBook` giữ full book hay chỉ top-of-book? | Subscribe spot (`264=1`) nên thực tế chỉ có 2 entry. Vẫn **cài theo book keyed by `278 MDEntryID`** vì `279` chỉ có New/Delete, không có Change — không giữ book thì không xử lý đúng Delete. |

---

## Việc làm

### Ranh giới quan trọng nhất

```
                 ┌──────────────────────────────┐
QuickFIX/n  ───► │  ICTraderFixTransport        │  ◄── lớp DUY NHẤT chạm thư viện
                 └──────────────┬───────────────┘
                                │  Message thô
      ┌─────────────────────────┼─────────────────────────┐
      ▼                         ▼                         ▼
 CTraderQuoteBook      CTraderPositionCache      CTraderSecurityCatalog
      │                         │                         │
      └────────────► CTraderSessionHealth ◄───────────────┘
```

```csharp
public interface ICTraderFixTransport
{
    event Action<Message> MessageReceived;
    event Action<CTraderSessionRole> LoggedOn;
    event Action<CTraderSessionRole> LoggedOut;
    bool IsLoggedOn(CTraderSessionRole role);
    void Send(CTraderSessionRole role, Message message);
}
```

### Danh sách file

| File | Trách nhiệm | Rủi ro |
|---|---|---|
| `ICTraderFixTransport.cs` | Trừu tượng hoá QuickFIX/n | — |
| `CTraderMessageFactory.cs` | Dựng V / x / D / AN bằng **kiểu typed** của QuickFIX/n (không ghép chuỗi tay — QuickFIX/n tự lo 8/9/35/10 và thứ tự header). Mẫu lấy từ `ConsoleSample/Program.cs`: `MarketDataRequest(mdReqID, SubscriptionRequestType('1'), MarketDepth(1))` + `AddGroup(NoMDEntryTypesGroup{'0'})` + `AddGroup(NoMDEntryTypesGroup{'1'})` + `AddGroup(NoRelatedSymGroup{Symbol("41")})`; `NewOrderSingle(ClOrdID, Symbol, Side, TransactTime, OrdType)` + `Set(OrderQty)` + `Set(TimeInForce('3'))` + `SetField(new StringField(721, …))`; `SecurityListRequest(SecurityReqID, SecurityListRequestType(0))`; `RequestForPositions{PosReqID}` | **R11** |
| `CTraderQuoteBook.cs` | W snapshot → X incremental, `279 ∈ {0=New, 2=Delete}`, key theo `278 MDEntryID` | R11 |
| `CTraderSecurityCatalog.cs` | `SymbolName` ↔ (`55` symbolId, `1008` digits). Parse group `NoRelatedSym` như `MessageExtensions.GetSymbols`: **đọc tag 1007/1008 bằng số nguyên thô** (`new StringField(1007)`, `new IntField(1008)`) — lớp `Tags` sinh sẵn của QuickFIX/n đặt tên hai tag này là `SideReasonCd`/`SideTrdSubTyp` (FIX 5.0), rất dễ đọc nhầm | R6, R11 |
| `CTraderPositionCache.cs` | ExecutionReport + PositionReport → `IReadOnlyList<TradeSharedRecord>` + **content-version counter** | **R3**, R4, §4.4 |
| `CTraderHistoryProjector.cs` | Fill của order đóng → `HistorySharedRecord` | R10, §4.4 |
| `CTraderTicketCodec.cs` | `positionId ↔ ulong ticket` có namespace | **R4** |
| `CTraderSessionHealth.cs` | Bảng sự thật `IsConnected` / `IsMapAvailable` | **R2** |
| `CTraderPositionReportParser.cs` | `PositionReport(AP)` → position: theo `MessageExtensions.GetPosition` — group `NoPositions`(1) → `704 LongQty`/`705 ShortQty` (bên lớn hơn là side), `730 SettlPrice` = giá mở TB, `721` = id, `1000`/`1002` = TP/SL. **`728=2` = "không có position" — vẫn phải kết thúc batch và set `PositionsSynced=true`**; `727 TotNumPosReports` cho biết đủ batch chưa | R2, R10 |

### Bốn điểm dễ làm sai nhất

**0. QuickFIX/n KHÔNG tự gắn `553`/`554` vào Logon.** `Username`/`Password` trong cfg chỉ là chuỗi
cấu hình; thư viện không đọc chúng. SDK Spotware tự set trong `IApplication.ToAdmin`
(`Common/QuickFixNApp.cs:57-75`):

```csharp
public void ToAdmin(Message message, SessionID sessionID)
{
    var messageType = message.Header.GetString(35);
    if (messageType is "0" or "1" or "3") return;          // heartbeat / test / reject: bỏ qua
    message.SetField(new StringField(553, _username), true);
    message.SetField(new StringField(554, _password), true);
}
```

`QuickFixCTraderTransport` **phải** làm y hệt. Quên là logon **bị server bỏ qua im lặng** (FAQ: message
không hợp lệ → không có phản hồi) — không Logout, không Reject, chỉ thấy reconnect loop mãi.

**1. Content-version counter (R3)** — `CTraderPositionCache` phải phơi ra một `ulong Version` chỉ tăng
khi tập record **thực sự đổi** (ticket vào/ra, volume, open price). Không phải `UtcNow`. Không phải
hằng số. Đây là thứ `ShouldApplyTradeResult` dùng để quyết định có xử lý kết quả hay không, và **cả
hai cách làm sai đều hỏng nặng** — xem bảng ở [README §3 R3](README.md).

**2. Mã hoá ticket (R4)**

```csharp
const ulong Namespace = 0x4000_0000_0000_0000UL;   // ticket MT không bao giờ chạm 2^62
static ulong Encode(long positionId);
static bool TryDecode(ulong ticket, out long positionId);
```

**3. Bảng sự thật sức khoẻ (R2)** — `CTraderSessionHealth` là bề mặt an toàn của cả dự án:

| `IsMapAvailable` (trades + history) | `TradeLoggedOn && SymbolResolved && PositionsSynced` |
|---|---|
| `IsConnected` (quote) | `QuoteLoggedOn && SymbolResolved && hasTopOfBook` |

**Không wire gì vào DI đường live ở phase này.**

---

## Rủi ro liên quan

**R2** · **R3** · **R4** · R6 · R10 · **R11**

---

## Nghiệm thu

Toàn bộ test chạy trên macOS với `FakeCTraderTransport` replay chuỗi message đóng hộp.

### `CTraderSessionHealth` — test kỹ nhất, đây là bề mặt an toàn

- [ ] Bảng sự thật đầy đủ: mọi tổ hợp 4 cờ → `IsConnected` và `IsMapAvailable` đúng.
- [ ] Chưa sync positions → `IsMapAvailable = false`. **Không được là `true, Count=0`** (R2).
- [ ] Logout **giữa lúc đang có position** → `IsMapAvailable` chuyển `false` **ngay**, không trễ.
- [ ] Logon lại nhưng chưa sync xong → vẫn `false`.

### `CTraderQuoteBook`

- [ ] W snapshot 2 entry → top-of-book đúng.
- [ ] Delete rồi New cùng `MDEntryID`.
- [ ] X đến trước W (out-of-order) → không crash.
- [ ] Book rỗng → `hasTopOfBook = false`, **không** trả bid/ask cũ (R9).
- [ ] Delete một `MDEntryID` không tồn tại → bỏ qua, không throw.

### `CTraderPositionCache`

- [ ] Fill về **trước** khi pending request kịp đăng ký → vẫn vào cache đúng (§4.2).
- [ ] ExecutionReport **trùng lặp** (nhiều connection cùng credentials) → không nhân bản position (R11).
- [ ] `150=0` rồi `150=F` → **không** coi cái đầu là khớp (R11).
- [ ] `150=8` reject → không tạo position.
- [ ] Partial fill nhiều `150=F` → cộng dồn đúng (theo kết quả Phase 0 câu 5).
- [ ] `PositionReport(AP)` batch → khớp với state từ ExecutionReport, không xung đột.
- [ ] `OpenEaTimeLocal` được stamp bằng `Environment.TickCount64`, **không phải 0** (§4.4).
- [ ] **Content-version chỉ tăng khi tập record đổi thật** — gọi 10 lần liên tiếp không có message mới
      → version không đổi (R3).

### `CTraderTicketCodec`

- [ ] Round-trip `positionId → ticket → positionId` cho biên: 0, 1, `long.MaxValue >> 2`.
- [ ] `TryDecode` trên một ticket MT điển hình (8–10 chữ số) → `false`.
- [ ] Không có positionId hợp lệ nào sinh ra ticket trùng dải MT.

### `CTraderMessageFactory`

- [ ] MarketDataRequest dựng đúng: `263=1`, **`264=1`**, `265=1`, `267=2` với `269=0` và `269=1` (R11).
- [ ] SecurityListRequest `559=0`.
- [ ] NewOrderSingle market: `40=1`, `38`, `54`, `55` **là số**, `60` UTC đúng format.
- [ ] NewOrderSingle đóng: có `721`, side ngược.
- [ ] Message dựng bằng typed class **validate qua `FIX44-CSERVER.xml`** không lỗi (QuickFIX/n lo
      thứ tự 8/9/35/10; test này bắt sai group/thiếu tag bắt buộc — lỗi câm nguy hiểm nhất của cTrader, R11).
- [ ] `RequestForPositions`: có `710`; không có `721` khi hỏi tất cả.

### `CTraderPositionReportParser`

- [ ] AP với `728=0`, `704>0`, `705=0` → Buy, volume = 704, price = 730.
- [ ] AP với `728=0`, `705>0` → Sell.
- [ ] **AP với `728=2` (không có position) → batch kết thúc, `PositionsSynced=true`, danh sách rỗng**
      — thiếu ca này thì tài khoản trống **không bao giờ** mở được R2 gate.
- [ ] Batch 3 AP với `727=3` → chỉ `PositionsSynced=true` sau AP thứ 3.

### `QuickFixCTraderTransport` (phần duy nhất chạm thư viện — test tối thiểu)

- [ ] `ToAdmin` với `35=A` → có `553` và `554`; với `35=0`/`1`/`3` → **không** thêm gì.
- [ ] `ToAdmin` với `35=A` **không** ghi mật khẩu ra log của app (kiểm tra logger được gọi với chuỗi đã che `554`).

### Gate chung

- [ ] Test suite không tăng so với baseline Phase 0. Build sạch.
- [ ] **Không file nào trong `TradeDesktop.Application/` có `using QuickFix`** — grep để chắc.
- [ ] **Không file nào ngoài `QuickFixCTraderTransport.cs` reference `SocketInitiator`, `Session`,
      `IApplication`, `SessionSettings`** — grep để chắc.

---

## Rollback

Revert commit. Không có gì được wire nên **không ảnh hưởng runtime dù có quên revert**.

---

## Cổng sang Phase 4

- [ ] Toàn bộ checklist nghiệm thu pass.
- [ ] **Đã quyết R8** (xem [Phase 4](phase-4-quote-feed.md) "Chốt trước khi code"): chấp nhận skip
      `LATENCY` ở chân B, hay tách ngưỡng riêng cho B thành task khác. Phase 4 là phase đầu tiên chạm
      vào đường live nên câu này phải xong trước.
