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
| **1** | **R8 — latency chân B.** `SignalEntryGuard.CheckLatency` so **cả hai** sàn với cùng một `confirm_latency_ms`. MT đo EA→app cùng máy (dưới 1 ms); cTrader đo "bao lâu rồi broker chưa gửi tick" — trên instrument thưa thì vài giây là bình thường. → Sẽ có skip `LATENCY` ở B. | Hai lựa chọn: **(a)** chấp nhận skip và theo dõi tần suất trong lúc soak; **(b)** thêm `confirm_latency_ms_b` riêng — nhưng đó là **task tách biệt**, không làm trong phase này. **Tuyệt đối không special-case riêng B bên trong guard** (đổi hành vi service dùng chung = vi phạm §0.2). **ĐÃ QUYẾT (chủ dự án, 2026-09-17): tách ngưỡng (b), làm SAU soak Phase 4, TRƯỚC Phase 7.** Trong Phase 4 giữ nguyên guard (chưa đặt lệnh được nên skip B chỉ ở log) và **đo** tần suất skip `LATENCY` B + phân bố tuổi tick B để chọn giá trị. Lưu ý: ngưỡng được kiểm ở **2 chỗ** — guard và `TradeExecutionRouter.cs:666-672`. Nội dung task **R8-B** `confirm_latency_ms_b`: cột nullable (null → dùng `confirm_latency_ms`, MT-MT y hệt); nối `SupabaseConfigRepository` → `ConfigRecord` → `ConfigService` → `RuntimeConfigState` (sentinel `-1`, không mặc định `0`) → `SignalEntryGuard.CheckLatency` **và** `TradeExecutionRouter` (`LATEST_LATENCY_EXCEEDS_LIMIT`) sửa cùng nhịp: chân A so ngưỡng chung, chân B so ngưỡng B — tổng quát, không rẽ nhánh theo platform (§0.2); test: null = hành vi cũ, B dùng ngưỡng B, mở Config không mất giá trị, router khớp guard. |
| 2 | Production dùng cổng SSL hay plain? | Đề xuất **SSL (5211)**. Plain chỉ để debug. **ĐÃ QUYẾT (2026-09-17): SSL 5211** (theo cờ `UseSsl` của `ctraderFix`, mặc định true; Phase 0 đã chạy SSL trên live). |
| 3 | Telegram bắn mỗi lần logon/logout có quá ồn không? | Đề xuất: bắn, nhưng có debounce — reconnect chớp nhoáng trong 30 s không bắn. **ĐÃ QUYẾT (2026-09-17): bắn + debounce 30 s** — logout mà reconnect trong 30 s thì im; logout kéo dài > 30 s bắn 1 lần, logon lại sau đó bắn 1 lần; `CTRADER_DIGITS_MISMATCH` luôn bắn ngay. |

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

Chạy 2026-09-17 trên Windows 11, **live 8220816** (`live.cfixapi.com:5211` SSL), 2 MT5 (EA `Local\MT_A_Tick` /
`Local\MT_B_Tick`). **Không bấm Start** suốt phase (chủ dự án duyệt #8 — tránh chân A MT5 mở lệnh thật rồi rollback).
Bằng chứng: `Desktop/trade-log/20260917-ctrader.log` (file riêng, duyệt #5), ảnh dashboard, `netwatch.ps1` (trạng thái
adapter + socket :5211 mỗi 250 ms), Playwright chỉ đọc cTrader Web.

### Lệch plan đã được chủ dự án duyệt (2026-09-17)

| # | Plan | Thực tế | Lý do |
|---|---|---|---|
| 1 | Bê nguyên văn MMF sang `MmfExchangeQuoteSource` | **Chỉ thêm** vào `SharedMemoryMarketDataReader`: ctor thêm `ICTraderQuoteSession? = null`; mỗi tick `EnsureState` + `sanB = isCTraderB ? session.Read(...) : ReadExchangeMetrics(mapName2, "SanB")` | Diff chỉ dòng `+` (và 1 dòng `sanB` thành ternary) → chứng minh không sửa logic MMF, revert an toàn |
| 2 | — | File log riêng `{yyyyMMdd}-ctrader.log` + dòng `[STATS]` mỗi phút | Logger phiên bỏ mọi dòng khi chưa Start |
| 3 | — | **Kiểm tra sống** TestRequest: im 5 s → `35=1`, không có message nào về trong 5 s → xoá book (fail-closed) | Kill mạng lần 1: Windows không đóng socket, heartbeat mới phát hiện sau **46.8 s**, suốt thời gian đó phục vụ giá cũ. Bản 3 s/2 s ngắt oan lúc 16:47:49 (mạng tốt) → nới 5 s/5 s + đo `probes_sent/probes_answered` |
| 4 | UI `CheckMap2Command` | Dòng "FIX QUOTE" + nút Kiểm tra trong panel cTrader | Nút Check Map 2 nằm trong panel MT đã ẩn ở mode cTrader |

### Dữ liệu đúng

- [x] Giá B chạy trên dashboard; `Symbol = XAUUSD` từ SecurityList (`symbolName=XAUUSD symbolId=41 digits=2 (SecurityList 316 symbol)`, trả lời trên QUOTE — Phase 0 câu 10, không mở TRADE).
- [x] Spread hợp lý: 12–20 pt trong mọi dòng `[STATS]` (16:12–16:56), không âm.
- [x] **Gap tính đúng 5/5** (ảnh dashboard 16:12:48–16:13:08, point 100): GAP BUY = (B.Bid − A.Ask)×100 → −24 (4317.07−4317.31), −31 (4317.49−4317.80), −29 (4318.66−4318.95), −27 (4318.43−4318.70), −29 (4318.42−4318.71); GAP SELL 20/17/18/13/16 khớp. `[STATS] gap_buy_pts` cũng khớp công thức.
- [x] `Tps` 2–5, `LatencyMs` 63–156 ms trên dashboard; `[STATS]` 211–350 tick/phút.
- [x] **Đối chiếu cTrader Web** (Playwright chỉ đọc, 16:12): web 4316.80/4316.96 (16 pt) vs app `[STATS]` 16:12:31 b_bid 4316.97/b_ask 4317.13 (16 pt) — cùng độ lớn, 2 chữ số, cùng spread. Cross-check tự động mỗi logon: ratio 1.0000.

### Fail-closed

- [ ] **Kill mạng → B IsConnected=false < 1 s: ❌ không đạt < 1 s.** Mất mạng im lặng (tắt Wi-Fi) không đóng socket. Không có kiểm tra sống: **46.8 s** (16:19:14.9 → 16:20:01.7). Bản 3 s/2 s: **4.2 s** (16:33:54.3 → 16:33:58.6). Bản cuối 5 s/5 s: thiết kế ≤ 10 s — **CHƯA KIỂM lại bằng tắt Wi-Fi**. Đứt socket có RST (mục dưới) thì phát hiện ngay.
- [x] Nối lại mạng → tự reconnect, không restart: bật Wi-Fi 16:20:44.0 → logon 16:20:46.3; lần 2 mạng chập chờn → logon 16:34:33.7; SecurityList re-issue mỗi lần, `digits=2`.
- [x] Point sai một bậc (Supabase 100 → 1000 + Reconnect): `[CTRADER][ERROR] CTRADER_DIGITS_MISMATCH … point=1000 KHÔNG khớp digits=2` lúc 16:54:21.710; dashboard B trống, GAP BUY/SELL `-` → không thể có open signal. Trả 100 → `Hết lệch digits` 16:55:44.7, giá về lại. **Telegram: bỏ qua kiểm tra theo chủ dự án** (log ghi đã gửi 16:54:21.712).
- [x] Kill socket cưỡng bức (TCPView Close Connection): `QUOTE logged out` 16:47:20.577 (tức thì), SynSent 16:47:22.9, logon 16:47:23.670 (3.1 s), không reject seqnum.

### Ma trận platform

| A | B | Kỳ vọng | Kết quả |
|---|---|---|---|
| mt4 | mt4 | FIX không connect (G4) | ❌ không chạy được: máy không có terminal MT4. Đường code giống mt5 (`platform_b != ctrader`); unit `G4_NotCTrader_NeverOpensTransport("mt4")` pass |
| mt4 | mt5 | FIX không connect | ❌ không có MT4 (như trên) |
| mt5 | mt4 | FIX không connect | ❌ không có MT4; unit test mt4 pass |
| mt5 | mt5 | FIX không connect | ✅ 16:08: netstat chỉ `172.64.149.246:443` (Supabase), không có `:5211`, không có `-ctrader.log` |
| mt4 | ctrader | A MMF, B FIX | ❌ không có MT4 |
| mt5 | ctrader | A MMF, B FIX; gap đúng | ✅ xem "Dữ liệu đúng" |

#### G3 — đổi platform giữa phiên, KHÔNG restart

- [x] mt5 → ctrader (Save, không restart): `QUOTE connecting` 16:11:31.4, logon 16:11:33.0, có giá 16:11:33.4. (Thời gian tới khi có giá là thời gian connect SSL + logon ≈ 2 s; nguồn B chuyển ngay ở tick kế tiếp, trong lúc chờ là Disconnected.)
- [x] ctrader → mt5: `unsubscribe sent=True` 16:15:26.67 → `QUOTE stopped` 16:15:28.5; dashboard B đọc lại `Local\MT_B_Tick` (XAUUSD.s).
- [x] Sau khi đổi: B dùng MMF với Max Lat 94 ms (thống kê mới, không mang 2204 ms của nguồn cTrader); unit `Reader_UsesSessionForB_OnlyWhenPlatformIsCTrader_ResolvedEveryTick`.

#### G4 — session im lặng khi B ≠ ctrader

- [x] `platform_b = mt5`: không log `[CTRADER]`, netstat không có kết nối tới `145.241.197.73:5211` / `live.cfixapi.com`.
- [x] Đổi sang ctrader → connect + logon (16:11:33).
- [x] Đổi khỏi ctrader → unsubscribe `263=2` + logout, socket đóng, không treo (netstat 16:15:39 chỉ còn :443).

### Không hồi quy

- [ ] So log với phiên trước Phase 4 khi `platform_b = mt5`: **❌ chưa so log phiên** (cần Start — #8). Bằng chứng thay thế: diff reader chỉ thêm dòng; khi B ≠ ctrader nhánh `ReadExchangeMetrics` y hệt; dashboard mt5/mt5 hiển thị bình thường.
- [ ] `CheckPriceFreeze` khi dừng EA chân A: **❌ chưa chạy** — guard chỉ chạy khi bật trading logic (cần Start, #8). `SignalEntryGuard` không bị sửa (diff 0 dòng).
- [x] Số liệu R8 đang thu (`[STATS]` mỗi phút). Tới 16:56 với `confirm_latency_ms = 100`: `would_skip_latency_b` **46.6%–68.3%** các phút bình thường; tuổi tick p50 94–187 ms, p95 406–1437 ms, max 703–3687 ms. Tổng hợp min/median/p95/max cả phiên soak: **CHƯA KIỂM — cần soak**.

### Soak

- [ ] Chạy nhiều ngày, qua cuối tuần — **CHƯA KIỂM — cần soak nhiều ngày** (bắt đầu 16:53 ngày 2026-09-17, PID 34608).
- [ ] Giờ nghỉ hằng ngày 03:59:45 → 05:00 (UTC+7) — **CHƯA KIỂM — cần soak qua đêm 2026-09-18**.
- [ ] Không rò bộ nhớ / handle — **CHƯA KIỂM — cần soak**.
- [ ] Logout khi đóng app bằng nút X — lần đóng 16:52 socket đóng nhưng mất dòng log (monitor bị DI dispose trước session → đã sửa, giữ handler log). **CHƯA KIỂM lại** — kiểm lúc kết thúc soak.
- [ ] `probes_sent/probes_answered` — xác nhận cServer QUOTE có trả lời TestRequest không — **CHƯA KIỂM — cần soak** (đọc từ `[STATS]`).

### Gate chung

- [x] Test suite: **904 total / 893 pass / 11 fail** — 11 tên trùng baseline memo §2.2b. Phase 4 thêm 25 test (`CTraderQuoteSessionTests`).
- [x] `dotnet build TradeDesktop.App --no-incremental`: 0 error, 3 warning = `CA1416` baseline (dòng reader 201 → 215 do thêm code phía trên).

---

## Rollback

Revert commit và đổi `platform_b` về `mt5`.

Vì `IExchangeQuoteSource` là một thay đổi cấu trúc bên trong `SharedMemoryMarketDataReader`, revert
sẽ đưa file về nguyên trạng. **Kiểm tra kỹ rằng impl MMF được bê nguyên văn** — nếu có sửa dù chỉ một
dòng trong lúc tách thì revert không còn là thao tác an toàn.

---

## Cổng sang Phase 5

### Hoãn có điều kiện (chủ dự án quyết 2026-09-17)

Phase 4 **tạm đóng để sang Phase 5/6** (hai phase đó chạy tài khoản trống, không đặt lệnh). Các mục dưới đây
**chưa xong**, được mang sang và là **điều kiện bắt buộc trước Phase 7** (phiên tiền thật) — xem
[Phase 7 "Phụ thuộc phase trước"](phase-7-execution.md). Có thể chạy soak song song trong lúc làm Phase 5/6.

| ID | Việc còn treo | Cách đóng | Chặn |
|---|---|---|---|
| P4-D1 | Soak nhiều ngày, qua cuối tuần; không rò bộ nhớ/handle | Để app chạy `platform_b=ctrader`, không Start; đọc `-ctrader.log` + Task Manager | Phase 7 |
| P4-D2 | Giờ nghỉ hằng ngày 03:59:45 → 05:00 (UTC+7): QUOTE có logout, book xoá, IsConnected B thế nào | Đọc `-ctrader.log` quanh 03:59–05:01 | Phase 7 |
| P4-D3 | Kill mạng im lặng với kiểm tra sống bản cuối 5 s/5 s (thiết kế ≤ 10 s) — plan gốc đòi < 1 s, **❌ không đạt được với mất mạng im lặng** | Tắt Wi-Fi ~20 s, đo từ `netwatch.ps1` + log; chủ dự án chấp nhận ngưỡng ≤ 10 s hoặc chọn khác | Phase 7 |
| P4-D4 | cServer QUOTE có trả lời TestRequest không | `[STATS] probes_sent/probes_answered`; nếu 0 trả lời → quyết giữ/gỡ kiểm tra sống | Phase 7 |
| P4-D5 | Tổng hợp R8: tần suất skip `LATENCY` B + min/median/p95/max tuổi tick cả phiên soak | Gom các dòng `[STATS]` | Task R8-B → Phase 7 |
| P4-D6 | Logout khi đóng app bằng nút X (đã sửa monitor giữ handler log, chưa kiểm lại) | Đóng app bằng X, log phải có `unsubscribe` + `QUOTE stopped` | Phase 7 |
| P4-D7 | `CheckPriceFreeze` khi dừng EA chân A + so log phiên mt5/mt5 với trước Phase 4 — cần Start | Làm trong Phase 7 Bước B (lúc đó đã chấp nhận Start) | Phase 7 |
| P4-D8 | Ma trận có MT4 (4 ô) — máy không có terminal MT4 | Cài MT4 hoặc chủ dự án chấp nhận chỉ unit test | Phase 8 |
| P4-D9 | Telegram `CTRADER_*` thực nhận trên điện thoại — chủ dự án bỏ qua kiểm | — (ghi nhận bỏ qua) | — |

- [x] Checklist nghiệm thu offline + live ngắn đã chạy; phần còn lại **hoãn có điều kiện** P4-D1…D9 (bảng trên).
- [ ] Số liệu skip `LATENCY` chân B đã có và đã được xem xét → **hoãn P4-D5** (đã có số liệu 1 giờ đầu: 47–68 %).
- [ ] Đã chốt câu hỏi "Chốt trước khi code" của [Phase 5](phase-5-open-positions.md).
