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
| 1 | Lõi FIX đặt ở `TradeDesktop.Infrastructure/CTrader/` hay project mới `TradeDesktop.CTrader`? | **ĐÃ QUYẾT — Infrastructure** (QuickFIXn 1.10.0 restore/build sạch `net8.0` ở Phase 0; Tests reference được). Đề xuất gốc: Theo kết quả Phase 0. Nếu QuickFIXn sạch trên macOS → **Infrastructure**, ít xáo trộn hơn. Nếu kéo phụ thuộc Windows-only → **project riêng**, để Tests vẫn reference được phần thuần. |
| 2 | Ranh giới QuickFIX/n đặt ở đâu? | **Ba tầng, rõ ràng:** (a) **Application** — zero `using QuickFix`, chỉ DTO thuần (`TradeSharedRecord`, `CTraderSessionHealth`, codec, checker). (b) **Infrastructure/CTrader** — được dùng **kiểu message** của QuickFIX/n (`QuickFix.FIX44.NewOrderSingle`, `MarketDataRequest`, group `NoMDEntryTypesGroup`…) để dựng/parse, như SDK Spotware làm — đó là lý do test build message có giá trị. (c) **`QuickFixCTraderTransport`** — lớp **duy nhất** chạm `SocketInitiator` / `Session` / `IApplication` / `SessionSettings`. `FakeCTraderTransport` thay lớp (c), còn (b) test được thật với message thật. |
| 4 | Một hay hai `SocketInitiator`? | **Hai — một cho QUOTE, một cho TRADE**, mỗi cái `IApplication` + `SessionSettings` riêng. Đây là topology SDK Spotware dùng trong `AspNetCoreSample/Services/FixClient.cs:16-47` (`_quoteInitiator`, `_tradeInitiator`, `_quoteApp`, `_tradeApp`). Lợi trực tiếp: Phase 4 start riêng QUOTE, Phase 5 start thêm TRADE, không đụng nhau; `OnCreate` của mỗi app chỉ giữ một `Session`. |
| 3 | `CTraderQuoteBook` giữ full book hay chỉ top-of-book? | Subscribe spot (`264=1`) nên thực tế chỉ có 2 entry. Vẫn **cài theo book keyed by `278 MDEntryID`** vì `279` chỉ có New/Delete, không có Change — không giữ book thì không xử lý đúng Delete. **ĐÃ QUYẾT (chủ dự án xác nhận 2026-09-17), theo bằng chứng Phase 0 câu 2:** spot KHÔNG gửi X, không có 278; mỗi W là snapshot đủ 2 entry → W thay toàn bộ book. Nhận X ở chế độ spot = bất thường → xoá book fail-closed (`LastAnomaly=INCREMENTAL_REFRESH_IN_SPOT_MODE`), không đoán; W kế tiếp khôi phục. Book keyed-by-278 chỉ cần nếu sau này chuyển sang depth (`264=0`). |

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
// Bản code thực tế: MessageReceived mang thêm role (QUOTE và TRADE đều trả 35=y),
// Send trả bool (false khi session chưa logon — không throw, không tự retry).
public interface ICTraderFixTransport
{
    event Action<CTraderSessionRole, Message> MessageReceived;
    event Action<CTraderSessionRole> LoggedOn;
    event Action<CTraderSessionRole> LoggedOut;
    bool IsLoggedOn(CTraderSessionRole role);
    bool Send(CTraderSessionRole role, Message message);
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
    // CHỈ Logon. KHÔNG chép bộ lọc "!= 0/1/3" của sample Spotware: cServer reject 553/554 trên
    // Logout bằng 35=3 "Tag not defined for this message type, field=553" (Phase 0 memo §5 câu 7b,
    // đo trên live 2026-09-17).
    if (messageType != "A") return;
    message.SetField(new StringField(553, _username), true);
    message.SetField(new StringField(554, _password), true);
}
```

`QuickFixCTraderTransport` **phải** gắn 553/554 **vào Logon và chỉ Logon**. Quên hẳn là logon **bị server
bỏ qua im lặng** (FAQ: message không hợp lệ → không có phản hồi) — chỉ thấy reconnect loop mãi. Gắn thừa
vào Logout thì server trả `35=3` Reject mỗi lần ngắt phiên (vô hại cho giao dịch nhưng làm bẩn log và dễ
bị hiểu nhầm là lỗi kết nối).

> **Mật khẩu có thể lộ qua MỌI message admin, không chỉ Logon.** Sample Spotware in Logout ra console
> kèm `554` nguyên văn (sự cố Phase 0, 2026-09-17). Mọi đường log raw FIX của app phải che `554` bằng
> một hàm dùng chung, và test phải cover cả `35=A` lẫn `35=5`.

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

> Chạy 2026-09-17 trên **Windows 11** (môi trường dev hiện tại, không phải macOS như dự kiến ban đầu), .NET SDK
> 8.0.425. Test ở `TradeDesktop.Tests/CTrader/` (6 file, 98 test); `FakeCTraderTransport` ở `FixTestSupport.cs`.
> Message test được parse bằng `Message.FromString(..., validate: true, FIX44-CSERVER.xml)`; W/X/y dùng chuỗi raw
> đo trên live ở Phase 0 (memo §5).

### `CTraderSessionHealth` — test kỹ nhất, đây là bề mặt an toàn

- [x] Bảng sự thật đầy đủ: 32 tổ hợp (4 cờ × `HasTopOfBook`) → `IsQuoteConnected`, `IsMapAvailable`, `ConnectedFlag`
      — `SessionHealth_TruthTable`.
- [x] Chưa sync positions → `IsMapAvailable = false`; `ReadAsMapResult` trả `MapNotFound` (không phải `true, Count=0`)
      — `MapUnavailable_WhenHealthNotSynced_EvenWithPositions`, `NotSynced_UntilBatchComplete_AndMapUnavailable`.
- [x] Logout giữa lúc có position → `OnLoggedOut` đặt `PositionsSynced=false` ngay → `IsMapAvailable=false`
      — `LogoutWithOpenPositions_MapUnavailableImmediately_AndStaysUntilResync`.
- [x] Logon lại chưa sync → vẫn `false`; chỉ `true` lại sau batch AP mới — cùng test trên.

### `CTraderQuoteBook`

- [x] W snapshot 2 entry (chuỗi live Phase 0) → bid 4350.77 / ask 4350.93, `QuoteSequence`, tick-count, tag 52
      — `QuoteBook_LiveSpotSnapshot_SetsTopOfBook`, `QuoteBook_EachSnapshotReplacesBookAndAdvancesSequence`.
- [x] Delete rồi New cùng `MDEntryID` → **theo câu 3 (spot)**: bất thường → xoá book, không throw, W sau khôi phục
      — `QuoteBook_DeleteNewOrUnknownDelete_FailClosedWithoutThrow`.
- [x] X đến trước W → không crash, không có top-of-book — `QuoteBook_IncrementalBeforeAnySnapshot_DoesNotCrash`.
- [x] Book rỗng → `HasTopOfBook=false`, `Bid/Ask=null` (R9) — `QuoteBook_EmptyBookNeverServesStalePrices`,
      `QuoteBook_OneSidedSnapshot_IsNotTopOfBook`, `QuoteBook_IncrementalInSpotMode_FailsClosed`.
- [x] Delete `MDEntryID` không tồn tại → không throw (fail-closed như trên) — cùng Theory.
- [x] Thêm: W symbol khác bị bỏ qua — `QuoteBook_OtherSymbol_Ignored`; SecurityList đọc 1007/1008 thô, list mới thay
      list cũ — `SecurityCatalog_*`.

### `CTraderPositionCache`

- [x] Fill về trước khi có sync/pending → vẫn vào cache (cache không phụ thuộc pending request); AP sau đó cùng nội
      dung không xung đột, version không nhảy — `NewThenFill_OnlyFillOpensPosition`,
      `FillBeforePendingSync_ThenSyncReportSameContent_DoesNotBumpVersion`.
- [x] ER trùng (cùng `17 ExecID`) → bỏ qua, volume/version giữ nguyên — `DuplicateFill_IsIgnoredAndVersionStable`.
- [x] `150=0` rồi `150=F` → chỉ `F` tạo position — `NewThenFill_OnlyFillOpensPosition`.
- [x] `150=8` → không tạo position — `RejectedReport_Ignored`.
- [x] Nhiều `150=F` cùng chiều → cộng dồn volume + giá TB; ngược chiều → giảm rồi đóng, `PositionClosed` đúng 1 lần
      — `AddReduceClose_RaisesClosedOnceWithClosePrice`. **Hình dạng partial fill thật (câu 5) → Phase 7 Bước A.**
- [x] AP batch khớp state từ ER — xem mục đầu.
- [x] `OpenEaTimeLocal` = `Environment.TickCount64` (≠ 0) — `OpenEaTimeLocal_DefaultClockIsEnvironmentTickCount_NotZero`,
      `TradeRecords_UseEncodedTicketAndLots` (ticket mã hoá, lot = units / contract size).
- [x] Version ổn định qua 10 lần đọc không có message mới, và qua reconciliation cùng nội dung (R3)
      — `Version_StableAcrossRepeatedReadsWithoutNewMessages`.
- [x] Thêm: history projector — profit tổng hợp theo point, commission 0, FIFO theo capacity, version riêng, unavailable
      khi health không cho — `HistoryProjector_ComputesSyntheticProfitAndCapsFifo`.

### `CTraderTicketCodec`

- [x] Round-trip 0, 1, 123456789, `long.MaxValue >> 2` — `TicketCodec_RoundTrip`.
- [x] Ticket MT (8–10 chữ số, 0, `0x3FFF…`, bit 63 bật) → `TryDecode=false` — `TicketCodec_MtTicketsNeverDecode`,
      `TicketCodec_HighBitSetTicket_IsNotCTrader`.
- [x] Mọi positionId hợp lệ cho ticket trong `[2^62, 2^63)` — `TicketCodec_AnyValidPositionIdIsOutsideMtRange`; ngoài dải → throw.

### `CTraderMessageFactory`

- [x] MarketDataRequest: `263=1`/`2`, **`264=1`**, `267=2` với `269=0`,`269=1`, `146=1` `55=41`, `262=MARKETDATAID`
      — `Factory_MarketDataRequest_SpotAndValid`.
      **Lệch plan: KHÔNG gửi `265=1`.** Bằng chứng: request của spike Phase 0 (ConsoleSample Spotware,
      `SendMarketDataRequest`) không có 265 và live 8220816 trả W bình thường (memo §5 câu 2); dictionary khai 265
      `required="N"`. Thêm field chưa chứng minh trên live = rủi ro bị drop im lặng (R11). **Chủ dự án xác nhận 2026-09-17.**
- [x] SecurityListRequest `559=0` — `Factory_SecurityListAndPositionsRequest_Valid`.
- [x] NewOrderSingle market: `40=1`, `38`, `54`, `55=41` là số, `60` dạng `yyyyMMdd-HH:mm:ss(.fff)`, `59=3`
      — `Factory_MarketOrder_OpenHasNo721_CloseHas721`.
- [x] NewOrderSingle đóng: có `721`, side ngược — cùng test.
- [x] Mọi message dựng bằng typed class: round-trip + `DataDictionary.Validate` với `FIX44-CSERVER.xml` không lỗi.
- [x] `RequestForPositions`: có `710`, không `721`.

### `CTraderPositionReportParser`

- [x] `728=0`, `704>0`, `705=0` → Buy, volume=704, price=730 — `Parser_ReadsLongPositionFromGroup`.
- [x] `705>0` → Sell — `Parser_ShortPosition_AndNoPositionsResult`.
- [x] `728=2` → batch kết thúc, `PositionsSynced=true`, rỗng, map available — `NoPositionsResult_CountsAsSyncedEmpty`.
- [x] Batch 3 AP `727=3` → synced chỉ sau AP thứ 3 — `BatchOfThree_SyncedOnlyAfterThird`; batch mới bỏ batch dở
      — `ReportFromNewBatchDiscardsUnfinishedBatch`.
      Ghi chú dialect: group `702` bắt đầu bằng delimiter `704` theo dictionary — AP chỉ có short phải kèm `704=0`
      (nếu cServer gửi khác, QuickFIX/n ném `GroupDelimiterTagException` → cần kiểm ở Phase 5 với tài khoản thật).

### `QuickFixCTraderTransport` (phần duy nhất chạm thư viện — test tối thiểu)

- [x] `35=A` → có `553`,`554`; `35=0/1/2/3/4/5` → không thêm gì — `LogonCredentials_AddedOnlyToLogon_AndMaskedInLog`,
      `LogonCredentials_NeverOnOtherAdminMessages`.
- [x] Che log: raw `|`, raw SOH (`35=5`), `{554: "…"}` → `***`; không đụng `1554=`/`2554=`
      — `LogMasker_MasksPasswordInEveryForm`, `LogMasker_LeavesOtherTagsUntouched`. Transport log mọi IN/OUT qua masker.
- [x] Session settings: không mật khẩu, không `FileLogPath`/`FileStorePath`, SSL/port đúng role, parse được bằng
      `SessionSettings` — `SessionSettings_ContainNoPasswordAndNoFileLog`, `SessionSettings_PlainPortWhenSslOff`.
- [ ] Kết nối thật qua `QuickFixCTraderTransport` → **CHƯA KIỂM: cần live + giờ mở, thuộc Phase 4 (QUOTE) / Phase 5 (TRADE).**

### Gate chung

- [x] Full suite: **879 total / 868 pass / 11 fail** — 11 fail trùng tên baseline (8 `CloseSignalEngineTests` TP cycle,
      1 `PortfolioBlockedSignalTests`, 2 `SosCloseConfigResolverTests`). Trước Phase 3: 781/770/11.
- [x] `dotnet build TradeDesktop.App --no-incremental`: 0 error, 3 warning = 3 `CA1416` baseline Infrastructure;
      `FIX44-CSERVER.xml` có trong output.
- [x] `grep "using QuickFix" TradeDesktop.Application` = **0**; `TradeDesktop.Application.csproj` không có QuickFIXn.
- [x] `SocketInitiator|IApplication|SessionSettings|Session.SendToTarget` ngoài test: chỉ `QuickFixCTraderTransport.cs`
      (+ 1 dòng **comment** trong `ICTraderFixTransport.cs`).
- [x] 0 tham chiếu tới kiểu Phase 3 trong `TradeDesktop.App` (không DI).

---

## Rollback

Revert commit. Không có gì được wire nên **không ảnh hưởng runtime dù có quên revert**.

---

## Cổng sang Phase 4

- [x] Toàn bộ checklist nghiệm thu offline pass (kết nối thật thuộc Phase 4/5).
- [x] Chủ dự án xác nhận 2 lệch plan (2026-09-17): câu 3 spot fail-closed; MarketDataRequest không gửi `265`.
- [x] **Đã quyết R8 (2026-09-17): tách ngưỡng B** — task riêng sau soak Phase 4, trước Phase 7. Câu gốc: chấp nhận skip
      `LATENCY` ở chân B, hay tách ngưỡng riêng cho B thành task khác. Phase 4 là phase đầu tiên chạm
      vào đường live nên câu này phải xong trước.
