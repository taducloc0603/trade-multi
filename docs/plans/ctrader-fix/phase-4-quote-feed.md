# Phase 4 — Chỉ luồng GIÁ, live, read-only

> **Phase đầu tiên chạm vào đường live.** Chỉ mở **QUOTE session**. TRADE session chưa kết nối, app
> **không có khả năng đặt lệnh** ở chân B (`NullCTraderTradeExecutor` từ Phase 1 vẫn nguyên chỗ).

[← Phase 3](phase-3-fix-core-offline.md) · [Index](README.md) · Phase sau: [Phase 5](phase-5-open-positions.md)

---

## Mục tiêu

Xem giá cTrader thật chảy qua **toàn bộ pipeline hiện có** — gap calculation, `SignalEntryGuard`,
UI — mà không có bất kỳ khả năng nào để đặt lệnh.

Đây là phase có tỉ lệ giá-trị/rủi-ro cao nhất: kiểm chứng được phần lớn giả định về dữ liệu, trong
khi hậu quả xấu nhất chỉ là "số hiển thị sai".

---

## Phụ thuộc phase trước

- Phase 3 đã merge: `CTraderQuoteBook`, `CTraderSecurityCatalog`, `CTraderSessionHealth`,
  `ICTraderFixTransport` đã có và đã test.
- Phase 2: `CTraderFixConfig` đọc được từ `sans_json`; `PointDigitsConsistencyChecker` đã có.

---

## Chốt trước khi code

| # | Câu hỏi | Bối cảnh |
|---|---|---|
| **1** | **R8 — latency chân B.** `SignalEntryGuard.CheckLatency` so **cả hai** sàn với cùng một `confirm_latency_ms`. MT đo EA→app cùng máy (dưới 1 ms); cTrader đo "bao lâu rồi broker chưa gửi tick" — trên instrument thưa thì vài giây là bình thường. → Sẽ có skip `LATENCY` ở B. | Hai lựa chọn: **(a)** chấp nhận skip và theo dõi tần suất trong lúc soak; **(b)** thêm `confirm_latency_ms_b` riêng — nhưng đó là **task tách biệt**, không làm trong phase này. **Tuyệt đối không special-case riêng B bên trong guard** (đổi hành vi service dùng chung = vi phạm §0.2). |
| 2 | Production dùng cổng SSL hay plain? | Đề xuất **SSL (5211)**. Plain chỉ để debug. |
| 3 | Telegram bắn mỗi lần logon/logout có quá ồn không? | Đề xuất: bắn, nhưng có debounce — reconnect chớp nhoáng trong 30 s không bắn. |

---

## Việc làm

### Session

| File | Việc |
|---|---|
| `Infrastructure/CTrader/QuickFixCTraderTransport.cs` (mới) | Impl `ICTraderFixTransport` — **lớp duy nhất** `using QuickFix` |
| `Infrastructure/CTrader/CTraderFixSession.cs` (mới) | Singleton điều phối; **chỉ mở QUOTE session ở phase này** |
| `Infrastructure/DependencyInjection.cs` (~:18) | Đăng ký singleton |
| Các call site của `ISharedMemoryReader.StartAsync/StopAsync` | Start/stop session ở **đúng đó** |

Cấu hình QuickFIX/n (xem [README §4.6](README.md)):
- **Một `SocketInitiator` riêng cho QUOTE** với `IApplication` + `SessionSettings` riêng — topology
  của `AspNetCoreSample/Services/FixClient.cs` (Spotware). Phase 5 sẽ thêm initiator thứ hai cho TRADE;
  hai cái không chia sẻ gì ngoài credentials.
- `QuickFixCTraderTransport.ToAdmin` **tự gắn `553`/`554` vào Logon** (Phase 3 pitfall #0). QuickFIX/n
  không làm hộ.
- **`MemoryStoreFactory`**, không phải `FileStoreFactory` — cTrader gửi `141=Y` reset seqnum mỗi lần
  logon nên file store không mua được gì mà thêm nguyên một lớp lỗi "seqnum lệch sau shutdown bẩn".
- Các khoá lấy **đúng từ `quickfixnsamples.net/ConsoleSample/Config.cfg`** của Spotware:
  ```ini
  ReconnectInterval=2
  ResetOnLogon=Y
  ResetOnDisconnect=Y
  LogoutTimeout=100
  HeartBtInt=30
  UseDataDictionary=Y
  DataDictionary=FIX44-CSERVER.xml
  TargetCompID=cServer
  ```
  **Không tự viết vòng reconnect.** `ResetOnLogon=Y` + `ResetOnDisconnect=Y` là lý do `MemoryStoreFactory`
  đủ dùng: Spotware cũng reset ở mọi ranh giới phiên nên file store không giữ được gì.
- Re-issue `SecurityListRequest` mỗi lần logon. **Kênh gửi phụ thuộc Phase 0 câu 10:** SDK Spotware
  (`FixClient.cs:235`) và mọi ví dụ spec gửi qua **TRADE**. Nếu QUOTE không trả lời thì phase này phải
  mở **cả** TRADE initiator, **chỉ** để gửi `SecurityListRequest` — không `RequestForPositions`, không
  order (những thứ đó là Phase 5). Ghi rõ trong log `[CTRADER][INFO] TRADE session opened for
  SecurityList only`.
- `MarketDataRequest`: `262 MDReqID` cố định một chuỗi (SDK dùng `"MARKETDATAID"`); **unsubscribe
  phải dùng lại đúng chuỗi đó** với `263=2`. Khi đổi `platform_b` khỏi `ctrader` giữa phiên (G4): gửi
  unsubscribe trước khi logout.
- Logon: `98=0`, `108=30`, `141=Y`, `553=<username>`, `554=<password từ CurrentCTraderFixConfig>` — hai
  tag cuối do `ToAdmin` gắn. **`FileLogPath` của QuickFIX/n không bật** (nó ghi Logon nguyên văn); log
  raw của app che `554=***`.

> **Không dùng `IHostedService`.** Lifecycle dữ liệu của app do user điều khiển; hosted service sẽ
> logon vào broker **trước khi config load xong** (config load async theo `MachineHostName`) và giữ
> session sống khi app rảnh.

#### G4 — hợp đồng lifecycle theo `platform_b`

`ICTraderFixSession` được đăng ký singleton **không điều kiện**. Nếu chỉ gắn Start/Stop vào reader mà
không xét `platform_b` thì app sẽ **logon vào broker cTrader ngay cả với cặp mt4+mt5 thuần** — giữ một
session giao dịch sống mà không dùng đến là rủi ro vận hành không cần thiết.

| Tình huống | Hành vi bắt buộc |
|---|---|
| `platform_b != ctrader` lúc Start | `StartAsync` là **no-op**, không mở socket, không logon |
| Đổi sang `ctrader` giữa phiên | Connect (qua `StateChanged` → `ApplyRuntimeConfig`) |
| Đổi khỏi `ctrader` giữa phiên | **Logout và giải phóng**, không để session treo |
| App Stop | Logout, dù đang ở platform nào |
| Đổi `platform_b` khi **đang có position B mở** | **Không xảy ra ở tầng này** — Phase 2 đã chặn ở `ConfigViewModel` (từ chối Save khi còn slot và đổi có liên quan `ctrader`). Session chỉ cần xử lý đổi platform khi `current_slots` rỗng |

> **QUOTE session không gửi heartbeat trong lúc đang stream quote** — đây là hành vi có tài liệu.
> Đừng coi vắng heartbeat là session chết khi tick vẫn chảy (R11).

### Tách nguồn giá — thay đổi tinh tế nhất của phase này

`Infrastructure/MarketData/SharedMemoryMarketDataReader.cs`: tách `IExchangeQuoteSource` cho mỗi phía.

```csharp
internal interface IExchangeQuoteSource
{
    ExchangeMetrics Read();   // luôn trả về, kể cả Disconnected
}
```

- `MmfExchangeQuoteSource` — **bê nguyên văn** code hiện tại (offset parse, `MarkAndCheckNewTick`,
  `LatencyAccumulator`, `TpsAccumulator`, `FormatTickTime`). Không sửa một dòng logic.
- `CTraderExchangeQuoteSource` — đọc top-of-book đang cache từ `CTraderQuoteBook`.

#### G3 — nguồn được chọn lại MỖI TICK, không phải một lần lúc Start

Vòng lặp hiện tại (`:100-116`) đã đọc lại `CurrentMapName1/2` từ `IRuntimeConfigProvider` **mỗi tick
50 ms** rồi `RefreshMapReaders` dispose handle cũ. Song song, `DashboardViewModel.cs:497` đăng ký
`_runtimeConfigState.StateChanged += ApplyRuntimeConfig` — **config đổi là có hiệu lực ngay, không cần
restart app**.

⇒ User bấm Save đổi `platform_b` từ `mt5` sang `ctrader` (hoặc ngược lại) **giữa phiên**.

**Nguồn cho mỗi phía phải được resolve lại trong vòng lặp poll, mỗi tick**, theo `platform_a` /
`platform_b` hiện hành — đúng khuôn `RefreshMapReaders` đang làm với map name:

```csharp
while (await timer.WaitForNextTickAsync(cancellationToken))
{
    var mapName1 = _runtimeConfigProvider.CurrentMapName1;
    var mapName2 = _runtimeConfigProvider.CurrentMapName2;

    RefreshMapReaders(mapName1, mapName2);
    RefreshQuoteSources();                       // ← mới: resolve theo platform_a/platform_b

    var sanA = _sourceA.Read();
    var sanB = _sourceB.Read();

    SnapshotReceived?.Invoke(this, new SharedMemorySnapshot(sanA, sanB, DateTime.UtcNow));
}
```

> Nếu chọn nguồn một lần lúc `StartAsync`, đổi platform giữa phiên sẽ đọc **sai nguồn** cho tới khi
> restart — **im lặng, không log, không lỗi**. Đây đúng là loại hỏng nguy hiểm nhất.

> **Vòng `PeriodicTimer` 50 ms và `SnapshotReceived?.Invoke(...)` không được đổi một dòng.**
> `SignalEntryGuard.CheckPriceFreeze` cần ≥2 entry đổi giá trong cửa sổ hold. Giữ nhịp cố định thì
> book cTrader đứng im **tự nhiên** sinh ra bid/ask lặp lại → freeze guard phát hiện đúng như thiết
> kế cũ, không cần code mới (§4.3).

Ngữ nghĩa các field phía cTrader:

| Field | Cách tính | Ghi chú |
|---|---|---|
| `LatencyMs` | `Environment.TickCount64 - tickCountLúcNhậnQuoteCuối` | Cùng họ "tuổi của tick" như MT; đồng hồ đơn điệu cùng máy, miễn nhiễm clock skew broker |
| `Tps` | Đếm quote nhận được mỗi giây | `MarkAndCheckNewTick` key theo `TimestampMs` nên nguồn cTrader phải cấp **stamp đơn điệu theo từng quote** |
| `Time` | Thời điểm quote từ tag 52 `SendingTime` của message W/X (hoặc `273 MDEntryTime` nếu broker gửi — kiểm ở Phase 0 câu 2) | Chỉ để hiển thị |
| `IsConnected` | `QuoteLoggedOn && SymbolResolved && hasTopOfBook` | §4.7 |

### Mất session → xoá book (R9)

Khi QUOTE session logout hoặc đứt socket: **xoá quote book**, không phục vụ tick cuối.

Giữ tick cuối thì freeze guard vẫn bắt được nhưng phải chờ hết `price_freeze_ms`. Xoá book thì
`IsConnected=false` ngay tick 50 ms kế tiếp → `TradeExecutionRouter` guard `!metrics.IsConnectedB`
chặn mọi open/close **lập tức**.

### Fail closed khi lệch digits (R6)

Khi nhận `SecurityList(y)`:
1. Log `[CTRADER][INFO] symbolName=… symbolId=41 digits=…`
2. `PointDigitsConsistencyChecker.Check(CurrentPoint, digits)`
3. **Lệch ⇒ fail closed**: `IsConnected=false` + `[CTRADER][ERROR]` + Telegram `CTRADER_DIGITS_MISMATCH`

> Point sai 10 lần không phải "chế độ suy giảm", nó là chế độ **mở lệnh theo nhiễu**. Guard
> `!metrics.IsConnectedB` sẵn có ở router sẽ dừng toàn bộ giao dịch mà không cần code mới.

Thêm một cross-check chỉ-log, một lần mỗi phiên: so độ lớn bid đầu tiên của B với A. Lệch > 2 lần
nghĩa là map nhầm symbol.

### UI

`SharedMemoryChecker` / `CheckMap2Command`: khi B là cTrader thì báo **trạng thái FIX session** thay
vì kiểm tra map MMF tồn tại.

---

## Rủi ro liên quan

**R6** (fail closed digits) · **R8** (quyết trước khi code) · **R9** (xoá book) · R11 (quirk FIX) ·
§4.3 (giữ nhịp 50 ms) · §4.6 (lifecycle) · §4.7 (bảng sức khoẻ)

---

## Nghiệm thu

Toàn bộ chạy trên Windows với `platform_b = ctrader`. **App không có khả năng đặt lệnh** ở phase này.

### Dữ liệu đúng

- [ ] Giá B chạy trên dashboard; `Symbol` hiển thị đúng tên từ `SecurityList`.
- [ ] Spread hợp lý, không âm, không nhảy bậc kỳ lạ.
- [ ] **Gap tính đúng**: tính tay `(B.Bid - A.Ask) * point` từ 5 tick bất kỳ trong log, so với
      `GapBuy` hiển thị. Phải khớp chính xác.
- [ ] `Tps` và `LatencyMs` là số hợp lý, không phải 0 hay số rác.

### Fail-closed

- [ ] **Kill mạng** → B `IsConnected=false` **trong vòng < 1 giây**, không phải chờ `price_freeze_ms`
      (R9).
- [ ] Nối lại mạng → tự reconnect (không cần restart app), giá chạy lại, `SecurityList` được re-issue.
- [ ] Cố tình đặt `point` sai một bậc → app fail closed, `[CTRADER][ERROR]`, Telegram bắn, **không có
      tín hiệu open nào được sinh ra** (R6).
- [ ] Kill socket cưỡng bức (không phải kill mạng) → reconnect đúng, seqnum không kẹt.

### Ma trận platform

Xem [README — Ma trận platform được hỗ trợ](README.md). Smoke trên Windows, chỉ kiểm luồng giá:

| A | B | Kỳ vọng | ☐ |
|---|---|---|---|
| mt4 | mt4 | Cả hai phía đọc MMF; FIX session **không connect** (G4) | ☐ |
| mt4 | mt5 | Cả hai phía đọc MMF; FIX session **không connect** | ☐ |
| mt5 | mt4 | Cả hai phía đọc MMF; FIX session **không connect** | ☐ |
| mt5 | mt5 | Cả hai phía đọc MMF; FIX session **không connect** | ☐ |
| mt4 | ctrader | A đọc MMF, B đọc FIX; gap tính đúng | ☐ |
| mt5 | ctrader | A đọc MMF, B đọc FIX; gap tính đúng | ☐ |

#### G3 — đổi platform giữa phiên, KHÔNG restart

- [ ] Đang chạy `mt5`/`mt5` → Save đổi `platform_b` sang `ctrader` → giá B chuyển sang nguồn FIX
      **trong ≤ 1 giây**, không restart app.
- [ ] Đổi ngược `ctrader` → `mt5` → giá B quay về MMF trong ≤ 1 giây.
- [ ] Sau mỗi lần đổi: gap vẫn tính đúng, không có tick nào mang giá trị của nguồn cũ.

#### G4 — session im lặng khi B ≠ ctrader

- [ ] `platform_b = mt5`: **không có** log `[CTRADER]` logon nào; kiểm bằng netstat rằng không có
      kết nối tới `demo-uk-eqx-01.p.c-trader.com`.
- [ ] Đổi sang `ctrader` → session connect, `[CTRADER][INFO]` logon xuất hiện.
- [ ] Đổi khỏi `ctrader` → session **logout**, kết nối đóng, không treo.

### Không hồi quy

- [ ] Đổi `platform_b` về `mt5` → so log với một phiên trước Phase 4. Hành vi phải **y hệt**.
- [ ] `SignalEntryGuard.CheckPriceFreeze` vẫn hoạt động ở cả hai chế độ — verify bằng cách dừng EA
      chân A và xem freeze có được phát hiện không.
- [ ] Ghi lại **tần suất skip `LATENCY` ở chân B** trong suốt thời gian soak — đây là dữ liệu để
      quyết R8 về sau.

### Soak

- [ ] **Chạy nhiều ngày liên tục** trước khi sang Phase 5, qua ít nhất một lần cuối tuần (để thấy hành
      vi session khi thị trường đóng — điều mà tài liệu cTrader **không** mô tả).
- [ ] Không rò bộ nhớ, không tăng handle.

### Gate chung

- [ ] Test suite không tăng so với baseline Phase 0. Build sạch.

---

## Rollback

Revert commit và đổi `platform_b` về `mt5`.

Vì `IExchangeQuoteSource` là một thay đổi cấu trúc bên trong `SharedMemoryMarketDataReader`, revert
sẽ đưa file về nguyên trạng. **Kiểm tra kỹ rằng impl MMF được bê nguyên văn** — nếu có sửa dù chỉ một
dòng trong lúc tách thì revert không còn là thao tác an toàn.

---

## Cổng sang Phase 5

- [ ] Toàn bộ checklist nghiệm thu pass, **bao gồm soak nhiều ngày**.
- [ ] Số liệu skip `LATENCY` chân B đã có và đã được xem xét.
- [ ] Đã chốt câu hỏi "Chốt trước khi code" của [Phase 5](phase-5-open-positions.md).
