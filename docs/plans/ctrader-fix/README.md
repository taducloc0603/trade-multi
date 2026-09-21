# cTrader FIX API cho sàn B — index kế hoạch

> Trạng thái: **đang review**. Chưa phase nào được bắt đầu.
> Ngày tạo: 2026-09-16 · Branch dự kiến: tách từ `dev-5-gap-on-dinh`

---

## 1. Bối cảnh

`TradeDesktop` hiện chỉ chạy được cặp MT4/MT5: cả hai chân A/B đều lấy giá + danh sách lệnh từ shared
memory do EA MQL4/MQL5 ghi, và đặt/đóng lệnh bằng native click vào cửa sổ terminal (P/Invoke theo
HWND).

Mục tiêu: **sàn B có thể là cTrader**, kết nối qua **FIX API** (https://help.ctrader.com/fix/),
sàn A giữ nguyên MT4/MT5.

Ràng buộc đã chốt với chủ dự án:

| # | Ràng buộc |
|---|---|
| 1 | cTrader **chỉ ở vị trí B**. `platform_a = ctrader` phải bị từ chối cứng. |
| 2 | **FIX API**, không dùng Open API, không automate UI cTrader. |
| 3 | **Trừu tượng hoá tối thiểu** — không refactor, không đổi hành vi signal engine / coordinator / Rule A–G (CLAUDE.md §0.2). |
| 4 | **Mỗi phase phải được thảo luận trước khi code và kiểm chứng xong mới sang phase sau.** |

### Credentials demo (FxPro)

| | QUOTE | TRADE |
|---|---|---|
| Host | `demo-uk-eqx-01.p.c-trader.com` | `demo-uk-eqx-01.p.c-trader.com` |
| Port | 5211 (SSL) / 5201 (plain) | 5212 (SSL) / 5202 (plain) |
| SenderCompID | `demo.fxpro.10649643` | `demo.fxpro.10649643` |
| TargetCompID | `cServer` | `cServer` |
| Sender/TargetSubID | `QUOTE` | `TRADE` |
| Username (tag 553) | `10649643` | `10649643` |

> Bảng trên **đã đối chiếu với panel FIX API của FxPro** ngày 2026-09-16 (chủ dự án chép tay): khớp
> từng trường. Panel ghi mật khẩu là *"a/c 10649643 password"* — tức mật khẩu FIX chính là mật khẩu
> đăng nhập tài khoản.

Symbol B: **SymbolId `41`** (rất có thể XAUUSD — Phase 0 câu 3 xác nhận). Quy đổi khối lượng chủ dự án
báo **OrderQty `1` = `0.01` lot** chính là 1 unit của XAUUSD với contract size 100 — không phải quy ước
riêng của broker. Xem §5 `volumeBUnits` / `contractSizeB`; Phase 0 câu 4 xác minh contract size.

Mật khẩu **không** nằm trong tài liệu này. Nó được lưu trong `configs.sans_json` (quyết định chủ dự án)
theo **ba quy tắc bảo vệ** ở §5 và [Phase 2](phase-2-config.md).

---

### Ma trận platform được hỗ trợ

|  | B = `mt4` | B = `mt5` | B = `ctrader` |
|---|---|---|---|
| **A = `mt4`** | ✅ đang chạy production | ✅ đang chạy production | ✅ mục tiêu |
| **A = `mt5`** | ✅ đang chạy production | ✅ đang chạy production | ✅ mục tiêu |
| **A = `ctrader`** | ❌ reject | ❌ reject | ❌ reject |

Hai chiều của ma trận này **độc lập** với nhau: `platform_a` và `platform_b` là hai cột DB riêng, và
`TradeExecutionRouter` **luôn** dispatch per-leg (`ExecuteOpenPairPerLegAsync` ~:809,
`ExecuteClosePairPerLegAsync` ~:840) — resolve `executorA` và `executorB` độc lập, **không có** nhánh
"hai chân cùng platform thì gọi `OpenPairAsync` một phát".

→ Cặp trộn platform **đã là đường chạy bình thường hôm nay** (mt4+mt5 đang chạy production như vậy).
cTrader ở B không tạo ra dạng đường đi mới, chỉ là một executor thứ ba trên đúng đường đó.

> **Hệ quả:** `ITradePlatformExecutor.OpenPairAsync` / `ClosePairAsync` **không được router dùng**.
> Xem [Phase 7](phase-7-execution.md) về cách implement chúng cho cTrader.

**Mọi phase chạm vào việc chọn platform (1, 2, 4, 7) phải nghiệm thu đủ 6 ô ✅ của ma trận này**, không
chỉ ô `ctrader`. Đây là bảo vệ chống hồi quy: thêm nền tảng thứ ba mà làm hỏng cặp MT-MT đang chạy
production là kết quả tệ nhất có thể.

---

## 2. Ý tưởng trung tâm

cTrader **giả lập thành một nguồn MMF**. Toàn bộ pipeline hiện tại tiêu thụ dữ liệu B qua đúng 3 cửa:

| Cửa | Interface | Nhịp |
|---|---|---|
| Giá | `IExchangePairReader.SnapshotReceived` → `SharedMemorySnapshot(SanA, SanB, TimestampUtc)` | 50 ms |
| Lệnh đang mở | `ITradesSharedMemoryReader.ReadTrades(mapName)` → `SharedMapReadResult<TradeSharedRecord>` | 500 ms |
| Lệnh đã đóng | `IHistorySharedMemoryReader.ReadHistory(mapName)` → `SharedMapReadResult<HistorySharedRecord>` | 500 ms |

Cắm adapter cTrader vào đúng 3 cửa này thì `GetLivePairTradeState`, `RegisterOpenExpectedForNewTickets`,
`RegisterCloseExecutionForNewHistoryTickets`, `TryRefreshCloseRows`, `TryRebuildCoordinatorFromMmf`,
watchdog, `_pairIdByTicket`, persistence `current_slots` — **chạy nguyên xi, không sửa dòng nào**.

Chiều đặt lệnh dùng seam có sẵn: `ITradePlatformExecutor` + `enum TradeLegPlatform`.

### Không dùng sentinel prefix trong tên map

Phương án từng cân nhắc là nhét `ctrader:B` vào `map_name_2`. **Đã bác bỏ**: nó bắt 4 chỗ phải sniff
chuỗi (`SharedMemoryChecker`, `SharedMemoryMarketDataReader`, `OrderMapNameResolver`, nhãn
`TargetMapName` trên panel + log `[MMF_TRADES]`), tệ hơn một cờ tường minh. Thay bằng:

```csharp
// TradeDesktop.Application/Abstractions/ICTraderRouting.cs (mới)
public interface ICTraderRouting
{
    bool IsCTraderExchangeB { get; }            // CurrentPlatformB == "ctrader"
    bool IsCTraderTradeMap(string? mapName);    // so khớp CHÍNH XÁC map B đã resolve
    bool IsCTraderHistoryMap(string? mapName);
}
```

Khi `platform_b = ctrader`, kênh B dùng **hằng `CTraderFixConfig.ChannelMapName = "CTRADER_B"` do app đặt**
(người dùng không nhập — quyết 2026-09-17, Phase 2 câu 3). `RuntimeConfigState.CurrentMapName2` trả hằng này;
`mapNames[1]` MT đã lưu trong `sans_json` giữ nguyên để chuyển về MT không mất cấu hình. `OrderMapNameResolver`
sinh `CTRADER_B_Trades` / `CTRADER_B_History` không cần sửa. Không có MMF nào tên `CTRADER_B` nên reader không
thể đọc nhầm EA MT sàn B còn chạy.

---

## 3. Bảng rủi ro R1–R11

Mọi quyết định thiết kế ở §4 đều bắt nguồn từ bảng này. Các file phase trỏ ngược về đây bằng mã R#.

### R1 — Đóng position bằng tag 721: spec không nói thẳng, nhưng SDK chính thức làm đúng vậy · **CHẶN DỰ ÁN**

Spec cTrader **không hề viết** "muốn đóng position thì làm X" (Rules of Engagement: NOT STATED). Cách
"market order ngược chiều + `721=positionId`" dựa trên hai bằng chứng **từ chính Spotware**:

1. **Mô tả tag 721 trong spec:** *"A position ID where this order should be placed... It can be
   specified only for hedged accounts."*
2. **Mã nguồn SDK chính thức** `spotware/quickfixnsamples.net/ConsoleSample/Program.cs:230-270` — tag
   721 được set **chỉ ở nhánh market order** của NewOrderSingle; và `spotware/FIX-API-Sample/
   MessageConstructor.cs:346` với comment nguyên văn: *"Position ID, where this order should be placed.
   If not set, new position will be created, it's id will be returned in ExecutionReport(8)."*

Tức "market order + 721 = tác động lên position đang có" là cách Spotware **chính thức đặt vào SDK**,
không phải suy luận từ forum. Đóng position là trường hợp riêng: side ngược + đủ volume.

**Câu còn phải verify trên demo thu hẹp lại một điểm:** với tài khoản **hedged**, market order **ngược
chiều** mang 721 có net về 0 (đóng) không, hay bị từ chối / mở position đối ứng.

**Nếu sai thì không có đường nào đóng lệnh B qua FIX** — chiều đóng của executor phải chuyển sang Open
API (`ProtoOAClosePositionReq`). Chiều đọc (Phase 3–6) **không bị ảnh hưởng**.

> **Tái cấu trúc 2026-09-16:** FxPro tắt FIX cho demo; live 8220816 là tài khoản duy nhất kết nối được.
> Câu 1 (và 4/5/6) **chuyển từ Phase 0 sang Phase 7 Bước A** để gom mọi việc tiền thật vào một phiên.
> Phase 7 vẫn **mở bằng spike 721 trước khi bật executor** — đó vẫn là cổng chặn, chỉ dời vị trí.
> Xem [Phase 7](phase-7-execution.md) và [Phase 0 § Tái cấu trúc](phase-0-spike.md).

### R2 — `IsMapAvailable=true` khi chưa sync xong positions → **đóng nhầm chân A đang sống**

Nếu decorator trả `IsMapAvailable=true, Count=0` trong cửa sổ giữa lúc app start và batch
`PositionReport(AP)` đầu tiên:

```
GetLivePairTradeState → OnlyAOpen
  → TryDetectAndHandleExternalPartialClose kết luận B đã bị đóng bên ngoài
    → đóng chân A của một hedge đang mở thật
```

→ **Invariant cứng nhất của cả dự án:** adapter trả `SharedMapReadResult.MapNotFound(...)` cho tới khi
`TradeLoggedOn && SymbolResolved && PositionsSynced`, và quay lại `MapNotFound` **ngay** khi
logout/đứt socket. `!IsMapAvailable` → `MapUnavailableOrParseError` → watchdog skip,
external-partial-close skip, `TryRefreshCloseLeg` fail closed.

### R3 — `Timestamp` của `SharedMapReadResult` là cạm bẫy hai chiều

`ShouldApplyTradeResult` bỏ qua kết quả khi `Timestamp` không đổi.

| Cách làm sai | Hậu quả |
|---|---|
| Trả hằng số | `ApplyTradeResult` không bao giờ chạy lại → **open không bao giờ được confirm**, slot treo PendingOpen, `CloseOpenedLegByTimeoutAsync` rollback mọi lệnh |
| Trả `UtcNow` | Luôn đổi → rebuild `TradeRealtimeProfitRows` mỗi 500 ms → đúng cạm bẫy WPF ở CLAUDE.md §5 (nút "Đóng" per-pair nuốt click, phải bấm 2–3 lần) |

→ Phải là **content-version counter đơn điệu, chỉ tăng khi tập record thực sự đổi**.

### R4 — Trùng không gian ticket giữa positionId cTrader và ticket MT

`_pairIdByTicket`, `_openRequestByTicket`, `_profitSnapshotByTicket`, `_openExecutionMsByTicket` đều
key bằng `ulong` trần, **không kèm sàn**. positionId cTrader là bộ đếm riêng 8–9 chữ số, trùng ticket
MT là chuyện có thật → cross-wire pair ownership → đóng nhầm lệnh.

→ Mã hoá namespace:

```csharp
// TradeDesktop.Application/Services/CTrader/CTraderTicketCodec.cs (mới)
const ulong Namespace = 0x4000_0000_0000_0000UL;   // ticket MT không bao giờ chạm 2^62
static ulong Encode(long positionId);
static bool TryDecode(ulong ticket, out long positionId);
```

Dạng đã mã hoá là dạng lưu `current_slots` và round-trip qua recovery; executor decode lại khi ghi 721.

### R5 — Lệch khối lượng hedge là **vô hình** với logic TP / Rule D

`CalculateTradeProfit` = `(Bid - openPrice) * point`, **bỏ qua lot size**. Nếu B lệch notional so với
A thì `slot.LastProfitSnapshot` vẫn chỉ là tổng hai delta điểm giá; `min_profit_to_close` (Rule D
gate) và lựa chọn priority-close ra quyết định trên con số **không còn bám tiền thật**.

→ Không sửa công thức (ngoài phạm vi). Thêm `HedgeVolumeConsistencyChecker` cảnh báo tỉ lệ (Phase 8).

### R6 — `point` là một giá trị global dùng chung hai sàn

`CurrentPoint` nuôi `GapCalculator` (cả hai chân), `SignalEntryGuard.CheckSpread`,
`CalculateTradeProfit`, `CalculateTradeOpenSlippage`. Nếu digits của symbol cTrader khác symbol MT ở A
thì **mọi giá trị gap sai một luỹ thừa 10** → mở lệnh theo nhiễu.

→ **Fail closed, không phải warn**: lệch digits ⇒ B `IsConnected=false` + trades map unavailable +
`[CTRADER][ERROR]` + Telegram. Guard `!metrics.IsConnectedB` sẵn có ở router dừng toàn bộ giao dịch.
Point sai 10 lần không phải "chế độ suy giảm", nó là chế độ **mở lệnh theo nhiễu**.

### R7 — `NormalizePlatform` có **5 bản sao**, tất cả fallback unknown → `"mt5"`

`ConfigService` 2 chỗ (:28, :496) + `RuntimeConfigState` (:460) + `SupabaseConfigRepository` (:255, dùng cả
đọc lẫn ghi DB) + `ConfigViewModel` (:709, setter `PlatformA/B` → Save). Hai bản cuối phát hiện lúc làm
Phase 1 (2026-09-17). Thiếu một chỗ thì `platform_b` âm
thầm thành `mt5` và app **click vào HWND chart MT5 cũ hoặc sai symbol**. Fallback sai kiểu im lặng là
loại nguy hiểm nhất.

→ Sửa cả 5 trong cùng một commit (Phase 1, đã làm). Test parity qua API public cho 3 bản ở Application/
Infrastructure (`PlatformNormalizationTests`); 2 bản ở App (test không reference App) kiểm bằng grep:
mọi `is "mt4" or "mt5"` phải đi kèm `or "ctrader"`.

### R8 — Latency / execution-delay của B khác A về bản chất

MT đo EA→app cùng máy (dưới 1 ms). cTrader đo "bao lâu rồi broker chưa gửi tick" — trên instrument
thưa thì vài giây là bình thường. `SignalEntryGuard.CheckLatency` so **cả hai** sàn với cùng một
`confirm_latency_ms` → sẽ có skip `LATENCY` ở B. Tương tự `ComputeExecutionMilliseconds` phía B sẽ gồm
cả round-trip broker → số delay lớn hơn hẳn, ngưỡng cảnh báo Telegram có thể phải chỉnh.

→ **Không special-case riêng B bên trong guard** (đổi hành vi service dùng chung = vi phạm §0.2).
Hoặc chấp nhận skip, hoặc thêm ngưỡng riêng cho B như một task tách biệt.
**ĐÃ QUYẾT (2026-09-17): tách ngưỡng** — task **R8-B** `confirm_latency_ms_b`: cột nullable (null → dùng `confirm_latency_ms`, MT-MT y hệt); nối `SupabaseConfigRepository` → `ConfigRecord` → `ConfigService` → `RuntimeConfigState` (sentinel `-1`, không mặc định `0`) → `SignalEntryGuard.CheckLatency` **và** `TradeExecutionRouter` (`LATEST_LATENCY_EXCEEDS_LIMIT`) sửa cùng nhịp: chân A so ngưỡng chung, chân B so ngưỡng B — tổng quát, không rẽ nhánh theo platform (§0.2); test: null = hành vi cũ, B dùng ngưỡng B, mở Config không mất giá trị, router khớp guard. Thời điểm: sau soak Phase 4 (lấy số liệu tuổi tick B), trước Phase 7. Chi tiết: [Phase 4](phase-4-quote-feed.md) "Chốt" câu 1.

### R9 — Mất session mà vẫn phục vụ tick cuối

Nếu giữ tick cuối, freeze guard vẫn bắt được nhưng phải chờ hết `price_freeze_ms`.
→ Mất quote session thì **xoá quote book** ⇒ `IsConnected=false` ngay tick 50 ms kế tiếp.

### R10 — Giới hạn của FIX so với MMF

- FIX **không gắn được SL/TP** vào NewOrderSingle → `TradeSharedRecord.Sl/Tp` của B luôn `0`.
- `PositionReport(AP)` **không có P&L, swap, commission, thời điểm mở**.
  `TradeSharedRecord.Profit` chỉ để hiển thị (profit thật được tính lại từ điểm giá) nên an toàn,
  nhưng `HistorySharedRecord.Profit/Commission` của B sẽ là số tổng hợp.
- `BuildInferredResyncedOpenSlots` ghép chân A/B theo `TradeType` rồi thứ tự `TimeMsc`; AP không có
  timestamp mở nên record cTrader khôi phục sẽ có `TimeMsc == 0` và thứ tự suy giảm. Thực tế
  `BuildTrackedResyncedOpenSlots` (dùng `_pairIdByTicket` từ `current_slots`) thắng bất cứ khi nào
  `current_slots` còn — nên chỉ cắn ở nhánh fallback legacy với >1 slot mở sau restart mất
  `current_slots`. Chấp nhận + log `[CTRADER][WARN]` khi rơi vào nhánh inferred.
- **Tab History sàn B (Phase 6, ĐÃ QUYẾT 2026-09-19):** `Commission = 0`, `Profit = (ClosePrice − OpenPrice) × point`
  theo chiều lệnh — là **số TÍNH LẠI**, không phải số broker (không có swap/commission thật). Không có chú thích trên UI;
  mỗi record ghi log `[CTRADER][TRADE][INFO] History record … (Profit/Commission là số TÍNH LẠI)`. Record chỉ sinh khi
  position đóng hẳn qua fill; position biến mất qua AP mà không có fill (đóng lúc mất kết nối) → chỉ `[WARN]`, không có record.

### R11 — Quirk FIX của cTrader dễ gây lỗi câm

| Quirk | Hậu quả nếu bỏ sót |
|---|---|
| `264 MarketDepth`: **`1` = spot, `0` = full depth** — **ngược** FIX chuẩn | Subscribe nhầm full depth |
| Tag 55 phải là **SymbolId số**, riêng từng broker | `INVALID_REQUEST: Expected numeric symbolId` |
| Message sai định dạng bị **drop im lặng, không Reject**; thứ tự tag 8,9,35 nghiêm ngặt | Treo chờ response vĩnh viễn, không biết vì sao |
| `103 OrdRejReason` luôn `= 0`; lý do thật ở free-text tag 58 | Không phân loại được lỗi |
| Market order sinh **hai** ExecutionReport: `150=0/39=0` rồi `150=F/39=2` | Coi cái đầu là khớp |
| Nhiều connection cùng credentials ⇒ mọi response gửi bản sao cho từng connection | Xử lý fill hai lần |
| QUOTE session **không gửi heartbeat khi đang stream quote** (có tài liệu) | Tưởng session chết |

---

## 4. Quyết định thiết kế đã chốt

### 4.1 **Giữ nguyên** `TryRefreshCloseRows` cho cả leg cTrader

Không bypass. Rule F yêu cầu router re-resolve ticket **sau khi lấy mutex**, fail closed nếu ticket
không còn. Vì decorator phục vụ position cache qua đúng `ReadTrades`, hàm này chạy **không sửa**: nó
chứng minh position còn mở, còn `RowIndex` nó sinh ra thì `CTraderTradeExecutor` bỏ qua (executor đóng
bằng `Ticket` → decode → tag 721).

Thêm `if (platform == CTrader) skip` sẽ **xoá một bảo đảm Rule F** cho riêng một platform và phải viết
thành amendment Rule F — đắt hơn nhiều so với không làm gì.

### 4.2 Vẫn xác nhận OPEN qua polling Trades map, **không** short-circuit bằng ExecutionReport

`RegisterOpenExpectedForNewTickets` làm nhiều hơn gán ticket: `_openRequestByTicket`,
`_pairIdByTicket`, `_openExecutionMsByTicket`, `CalculateTradeOpenSlippage`,
`CaptureSignalLegDiagnostic`, `MarkOpenPairLegConfirmed`, `NotifySlippageAndDelayIfNeeded`,
`SignalLogItems`. Short-circuit = viết lại toàn bộ cho một platform → vi phạm §0.2.

Race đã giải sẵn: `CapturePendingOpenRequest` chạy *trước* dispatch; fill chỉ nằm trong cache chờ tối
đa một chu kỳ 500 ms.

ExecutionReport **dùng cho reject, không dùng cho confirm**: `OpenLegAsync` chờ terminal report
(`150=F` fill hoặc `150=8` reject) với timeout **nhỏ hơn hẳn** `OpenPendingTimeoutMs`, trả
`Success=false` khi bị từ chối. MT click luôn trả `Success=true`; ở đây trả sự thật mà vẫn nằm trong
contract cũ — router xử lý partial-open và `CloseOpenedLegByTimeoutAsync` lo phần còn lại.

Khớp `TryConsumePendingOpenRequest`: `Symbol` phải là **`SymbolName` tag 1007** và phải trùng cái quote
side báo cho B (vì `CapturePendingOpenRequest` lấy symbol kỳ vọng từ `snapshot.ExchangeB.Symbol`) —
một nguồn sự thật duy nhất là `SecurityList`. **Không điền `Volume` cho pending leg cTrader**
(`IsNullOrVolumeMatch` pass khi null; điền vào là mời gọi lệch units-vs-lots rồi âm thầm tụt xuống
tier khớp theo type).

### 4.3 Giữ nguyên nhịp 50 ms và phát snapshot vô điều kiện

`SignalEntryGuard.CheckPriceFreeze` cần ≥2 entry đổi giá trong cửa sổ hold. Tách `IExchangeQuoteSource`
**bên trong** `SharedMemoryMarketDataReader`: impl MMF bê nguyên văn code cũ, impl cTrader chỉ đọc
top-of-book đang cache. Vòng `PeriodicTimer` 50 ms và `SnapshotReceived?.Invoke(...)` không đổi → book
cTrader đứng im tự nhiên sinh bid/ask lặp → freeze guard phát hiện đúng như thiết kế cũ.

`LatencyMs` phía cTrader = `Environment.TickCount64 - tickCountLúcNhậnQuoteCuối` — cùng họ ngữ nghĩa
"tuổi của tick" như MT, đồng hồ đơn điệu cùng máy, miễn nhiễm clock skew broker.
`MarkAndCheckNewTick` key theo `tickRecord.TimestampMs` nên nguồn cTrader phải cấp một stamp đơn điệu
theo từng quote để TPS/latency tính đúng.

### 4.4 `OpenEaTimeLocal` / `CloseEaTimeLocal` phải stamp thật

Chúng nuôi `ComputeExecutionMilliseconds(eaTimeLocal, appRequestRawMs)` với `appRequestRawMs` là
`Environment.TickCount64` lúc dispatch. Adapter **phải** stamp `Environment.TickCount64` đúng lúc parse
ExecutionReport khớp lệnh. Để `0` sẽ sinh execution-latency rác nuôi `NotifySlippageAndDelayIfNeeded`
(báo động Telegram sai).

### 4.5 `Connected` đặt trung thực

Field `Connected` ở header MMF hiện chỉ chảy vào `_connectionEvaluator` — **đang bị comment out**
(`DashboardViewModel` ~:3897, kill-switch tắt theo commit `f3ab5ee`). Blast radius thấp hôm nay, nhưng
vẫn đặt đúng: `1` khi cả hai session logged on, `0` khi không, **không bao giờ `-1`**. Nếu kill-switch
được bật lại mà ta hardcode thì cTrader thành một sàn "luôn khoẻ" vĩnh viễn.

### 4.6 Session: DI singleton, không phải `IHostedService`

Lifecycle dữ liệu của app do user điều khiển (`ISharedMemoryReader.StartAsync/StopAsync`). Hosted
service tự chạy sẽ logon vào broker **trước khi config load xong** (config load async theo
`MachineHostName`) và giữ session sống khi app rảnh.

- Đăng ký `ICTraderFixSession` singleton ở `Infrastructure/DependencyInjection.cs`, start/stop từ
  **đúng các call site** của `ISharedMemoryReader.StartAsync/StopAsync`.
- **Hai `SocketInitiator`** — một QUOTE, một TRADE — mỗi cái `IApplication` + `SessionSettings` riêng.
  Đây là topology SDK Spotware dùng (`AspNetCoreSample/Services/FixClient.cs:16-47`). Cho phép Phase 4
  start riêng QUOTE và Phase 5 thêm TRADE mà không đụng nhau.
- QuickFIX/n **không tự gắn** `553`/`554` vào Logon từ `Username`/`Password` trong cfg — transport phải
  set trong `ToAdmin` như `QuickFixNApp.cs:57-75`. Quên ⇒ server bỏ qua im lặng.
- **`MemoryStoreFactory` chứ không `FileStoreFactory`** — cTrader gửi `141=Y` reset seqnum mỗi lần
  logon nên file store không mua được gì mà thêm nguyên một lớp lỗi "seqnum lệch sau shutdown bẩn".
- Cấu hình QuickFIX/n lấy **đúng các khoá Spotware dùng** trong `quickfixnsamples.net/ConsoleSample/
  Config.cfg`: `ReconnectInterval=2`, `ResetOnLogon=Y`, `ResetOnDisconnect=Y`, `LogoutTimeout=100`,
  `HeartBtInt=30`, `UseDataDictionary=Y`, `DataDictionary=FIX44-CSERVER.xml`. Lưu ý Spotware dùng
  `FileStorePath` **nhưng** reset ở mọi ranh giới phiên — file store không giữ được gì qua restart, nên
  `MemoryStoreFactory` cho cùng hành vi với ít chỗ hỏng hơn.
- Reconnect dựa vào `ReconnectInterval=2` / `HeartBtInt=30` của QuickFIX/n, **không tự viết vòng
  reconnect**.
- Re-issue `SecurityListRequest` mỗi lần logon.

### 4.7 Một bản ghi sức khoẻ, hai nơi tiêu thụ, cả hai fail-closed

```csharp
public sealed record CTraderSessionHealth(
    bool QuoteLoggedOn, bool TradeLoggedOn,
    bool SymbolResolved, bool PositionsSynced,
    long LastQuoteTickCount);
```

| Bề mặt | Điều kiện | Guard sẵn có bị kích hoạt |
|---|---|---|
| `ExchangeMetrics(B).IsConnected` | `QuoteLoggedOn && SymbolResolved && hasTopOfBook` | `TradeExecutionRouter` `!metrics.IsConnectedB` → chặn mọi open/close |
| `SharedMapReadResult.IsMapAvailable` (trades + history) | `TradeLoggedOn && SymbolResolved && PositionsSynced` | `GetLivePairTradeState` → `MapUnavailableOrParseError`; `TryRefreshCloseLeg` cancel; watchdog skip; external-partial-close skip |

---

## 5. Cấu hình mới

Gom vào `sans_json` (đã có `SansJsonHelper.TryParseSans/BuildSans`) dưới key `ctraderFix`, tránh
migration ~10 cột DB:

```json
"ctraderFix": {
  "quote": { "host": "demo-uk-eqx-01.p.c-trader.com", "portSsl": 5211, "portPlain": 5201 },
  "trade": { "host": "demo-uk-eqx-01.p.c-trader.com", "portSsl": 5212, "portPlain": 5202 },
  "useSsl": true,
  "senderCompId": "demo.fxpro.10649643",
  "targetCompId": "cServer",
  "password": "<plaintext>",
  "username": "",
  "symbolId": 41,
  "symbolName": "",
  "volumeBUnits": 1,
  "contractSizeB": 100,
  "volumeALots": 0.01
}
```

Hình dạng này **soi gương panel FIX API của cTrader** (hai khối QUOTE/TRADE, mỗi khối host + 2 cổng)
để người nhập chép 1:1, không phải tự chọn cổng theo SSL. `useSsl` quyết định app dùng `portSsl` hay
`portPlain` — đổi SSL/plain không phải gõ lại cổng. Chi tiết ánh xạ từng field ở
[Phase 2 — Bảng ánh xạ](phase-2-config.md).

- **`password` lưu plaintext trong `sans_json`** — quyết định của chủ dự án (2026-09-16) để đổi mật khẩu
  không cần restart. Đổi lại, **ba quy tắc bắt buộc** ở [Phase 2](phase-2-config.md): không log
  `sans_json` thô (qua `SansJsonHelper.Redact`), không log tag 554 và không bật `FileLogPath` của
  QuickFIX/n, `CTraderFixConfig.ToString()` che mật khẩu.
- `volumeBUnits`: OrderQty gửi tag 38 — **đơn vị cơ sở của hợp đồng, KHÔNG phải lot** (spec: *"number
  of shares ordered... maximum precision is 0.01"*). Với XAUUSD contract size 100: `1 unit = 0.01 lot`.
  Với FX major contract size 100 000: `1 000 units = 0.01 lot`. **Đổi symbol là đổi hệ số.**
  Con số "`1 = 0.01`" chủ dự án cấp chính là trường hợp vàng — không phải quy ước riêng của broker.
- `contractSizeB`: số unit trong 1 lot của symbol B. Ứng viên `100` cho XAUUSD — **Phase 0 xác minh,
  không hardcode.** Dùng cho `HedgeVolumeConsistencyChecker`.
- `volumeALots`: **chỉ khai báo, không cưỡng chế**. Dùng cho `HedgeVolumeConsistencyChecker`:
  log `[CTRADER][INFO] hedge ratio=1.00` mỗi lần apply config; `[WARN]` ngoài `[0.95, 1.05]`;
  Telegram `HEDGE_VOLUME_ASYMMETRY` ngoài `[0.8, 1.2]`.

### Quan hệ với `docs/PLAN-MT5-OCT-VOLUME-INPUT.md`

Plan đó **đang bế tắc ở chính cổng kiến trúc của nó** (Phương án A Python `order_send` vs Phương án B
ghi volume vào panel OCT), chỉ nhắm MT5, cần cả việc dịch ngược control OCT và thêm API volume vào
`native/mt5engine-capi`.

**Không phụ thuộc, không chờ nó** — nhưng đặt tên key ngay theo hình dạng nó sẽ cần, để sau này chỉ
việc chuyển `volumeALots` từ "khai báo" sang "cưỡng chế".

---

## 6. Tuân thủ Rule E / Rule F

**Rule E — tuân thủ.** Executor mới chỉ là một *transport* nằm dưới `ITradePlatformExecutor`, chỉ tới
được từ `TradeExecutionRouter`, chỉ tới được từ các dispatch site hiện có. Signal engine, coordinator,
guard, ma trận quota/cooldown không đổi. **Không cần thêm exception nào vào danh sách CLAUDE.md §2
Rule E.**

> **Điều cấm — ghi thành comment ở đầu adapter:** lớp FIX phải **thuần bị động** — báo cáo trạng thái,
> thực thi lệnh được ra lệnh, hết. Không bao giờ tự flatten position nó cho là mồ côi, không tự retry
> close, không tự reconcile bằng cách gửi lệnh. Bất kỳ hành vi nào như vậy là một đường open/close mới
> bỏ qua signal engine → **vi phạm Rule E trực tiếp**. Đây là cách dễ nhất để một người sau này phá
> Rule E mà không nhận ra.

**Rule F — tuân thủ.** `CloseExecutionOwner` theo `pairId`, `_closeDispatchInFlight`,
`TryAcquireTradeAction`, ticket re-validation dưới mutex: tất cả nằm **trên** executor, không đổi.
Thêm guard lúc save config: `platform_a == "ctrader"` → reject.

---

## 7. Bảng tiến độ

| Phase | File | Nội dung | Live? | Đặt lệnh được? | Trạng thái |
|---|---|---|---|---|---|
| 0 | [phase-0-spike.md](phase-0-spike.md) · [memo](phase-0-memo.md) | Kết nối + chiều đọc trên live (câu 2,3,7,8,9,10) — code vứt đi | — | **không** (đã tách) | ✅ **GO-READ** (2026-09-17): câu 2/3/7/8/9/10 trên live 8220816; baseline Windows 11 = macOS, 0 regression; phát hiện cServer reject 553/554 trên Logout (Phase 3 sửa theo). Câu 1/4/5/6 → Phase 7 |
| 1 | [phase-1-platform-enum.md](phase-1-platform-enum.md) | Nhận diện `ctrader`, chưa có cTrader | không | không | ✅ **Xong** (2026-09-17): 5 bản normalize, enum/router/B1, Null executor; +22 test xanh; baseline 11 giữ nguyên; App build 0 warning; smoke app (MT-MT + `platform_b=ctrader`) pass. Smoke đặt lệnh → Phase 7 B |
| 2 | [phase-2-config.md](phase-2-config.md) | UI + persistence `ctraderFix` | không | không | ✅ **Xong** (2026-09-17): UI 2 mode Sàn B, `ctraderFix` trong `sans_json`, 3 cửa HWND B, `CTRADER_B`, Redact, chặn đổi platform khi còn slot; +91 test xanh, baseline 11 giữ nguyên; smoke app pass. Còn: log `[HWND][SKIP]` (Phase 4), 2 ca còn-slot (Phase 7 B) |
| 3 | [phase-3-fix-core-offline.md](phase-3-fix-core-offline.md) | Lõi FIX + test, không wire | không | không | ✅ **Xong** (2026-09-17): QuickFIXn 1.10.0 trong Infrastructure, dictionary cServer, lõi health/codec/masker/book/catalog/AP parser/cache/history/factory/transport; +98 test xanh (message dựng validate qua `FIX44-CSERVER.xml`, W/X/y thật từ Phase 0), baseline 11 giữ nguyên; App build 3 warning baseline; 0 `using QuickFix` ở Application; 0 đăng ký DI. Chủ dự án đã xác nhận 2 lệch plan (câu 3 spot fail-closed; không gửi `265`) |
| 4 | [phase-4-quote-feed.md](phase-4-quote-feed.md) | Luồng **GIÁ** | QUOTE | không | ⏸️ **Tạm đóng — hoãn có điều kiện P4-D1…D9, bắt buộc trước Phase 7** (2026-09-17, soak chạy song song từ 16:53): code xong, live 8220816 — gap 5/5, đối chiếu web, G3/G4, kill socket, R6 point sai đạt; kill mạng im lặng ❌ < 1 s (có kiểm tra sống 5 s/5 s, chưa đo lại); MT4 không có trên máy; price-freeze/so log cần Start (không bấm theo #8); soak + giờ nghỉ + probes CHƯA KIỂM. Test 904/893/11 |
| 5 | [phase-5-open-positions.md](phase-5-open-positions.md) | Luồng **LỆNH ĐANG MỞ** — code + test + nghiệm thu **tài khoản trống** | + TRADE | không | ⏸️ **Tạm đóng — Lớp 1 xong; hoãn có điều kiện P5-D1/P5-O1, bắt buộc trước Phase 7** (2026-09-17): TRADE session chỉ đọc + decorator; 728=2 path, cửa sổ chưa sync, kill socket TRADE (MapNotFound 20 ms), relogon, R3 5 phút, reconciliation 60 s, về mt5 — đạt; test 922/911/11. Còn: soak 2 ngày (CHƯA KIỂM), vấn đề mở P5-O1 (QUOTE logout lặp 1 lần, không tái hiện). Lớp 2 → Phase 7 B |
| 6 | [phase-6-history.md](phase-6-history.md) | Luồng **LỊCH SỬ** — code + test + nghiệm thu **tài khoản trống** | + TRADE | không | ⏳ **Lớp 1 xong (offline + live 2026-09-21), đang soak**: decorator history + projector; 13 unit test; live: map MapNotFound→AVAILABLE sau sync, version history độc lập 6 phút, kill socket TRADE → MapNotFound 40 ms, về mt5 đọc lại `MT_B_History`. Test 964/953/11. Còn: soak 2 ngày; Lớp 2 → Phase 7 B |
| **7** | [phase-7-execution.md](phase-7-execution.md) | **PHIÊN TIỀN THẬT duy nhất**: A) spike 721 (câu 4/1/5/6) · B) nghiệm thu 5/6 **có position** · C) executor thật | đầy đủ | **có** | ☐ |
| 8 | [phase-8-hardening.md](phase-8-hardening.md) | Cảnh báo, Telegram, tài liệu | đầy đủ | có | ☐ |

**Nguyên tắc gom (2026-09-16):** mọi việc cần `NewOrderSingle` hay position thật nằm **chỉ** ở Phase 7,
chạy trong **một phiên** trên live 8220816 (nạp ~$30–50; chi phí thực tế ≈ spread 24 pt/oz ≈ $0.24 mỗi
vòng + commission). Phase 0–6 **không chạm tiền**: có thể hoàn thành toàn bộ trước khi nạp tiền.

Quy ước chung cho mọi phase:
- Mỗi phase là **một commit độc lập, revert được riêng**.
- Sau mỗi phase chạy `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj`;
  **không được tăng so với baseline đo ở Phase 0**.
- Build sạch, không warning mới.
- Không sang phase sau khi checklist nghiệm thu chưa pass hết.

---

## 8. Giới hạn môi trường test (phải nhớ)

`TradeDesktop.Tests.csproj` chỉ reference **Application / Domain / Infrastructure**, **không reference
App**. Nên `TradeExecutionRouter`, các executor, `OrderMapNameResolver`, `SharedMemoryChecker`,
`DashboardViewModel` **không test được trên mọi OS**. Đó chính là lý do mọi thứ rủi ro phải nằm ở
Application/Infrastructure.

| Việc | macOS (máy dev) | Windows | Demo cTrader |
|---|---|---|---|
| Unit test logic thuần | ✅ | ✅ | — |
| Build app WPF | chỉ với `EnableWindowsTargeting` | ✅ | — |
| Smoke test app | ❌ | ✅ | — |
| Phase 0 spike | ✅ (`ConsoleSample` của Spotware, zero code) | ✅ | ✅ bắt buộc |
| Phase 4–7 nghiệm thu | ❌ | ✅ | ✅ bắt buộc |
| Đối chiếu phía cTrader (symbol info, Positions, History) | ✅ cTrader Web | ✅ cTrader Web | — |

### cTrader Web + Playwright MCP — công cụ đối chiếu, không phải công cụ giao dịch

Máy dev Windows hiện có **2 terminal MT5** đang chạy và đăng nhập được **cTrader Web** (`ct.fxpro.com`)
qua Playwright MCP. Cách dùng thống nhất cho mọi phase:

- MCP **chỉ đọc**: Symbol info, tab Positions/History, Bid/Ask, Market hours. Không bấm New order /
  Close — ràng buộc #2 (§1) vẫn giữ nguyên; thao tác tay ở Phase 5/6 do **người dùng** làm.
- Web **không có panel FIX API** (nút "FIX API" chỉ mở tài liệu). Host/port/CompID lấy từ cTrader
  desktop hoặc email broker.
- Snapshot Playwright nằm ở `.playwright-mcp/` (đã `.gitignore`) và chứa email đăng nhập — không đưa vào
  memo, chỉ chép số liệu cần thiết.
- Cùng đăng nhập có tài khoản **Live** (8220816, 8225904). Chỉ tài khoản Demo 10649643 được dùng cho
  toàn bộ kế hoạch.

Baseline test: CLAUDE.md §6 ghi 19 fail; ghi chú làm việc ghi macOS là 26. **Đo lại ở Phase 0**, đừng
tin con số cũ.

---

## Phụ lục A — Đối chiếu tài liệu ngoài

Trong quá trình lập kế hoạch có tham khảo một tài liệu tổng hợp do Gemini sinh ra. Nó đúng ở phần
chuẩn FIX chung, nhưng **sai ở vài điểm đặc thù cTrader**. Bảng này tồn tại để người implement sau
**không làm theo** những điểm sai đó. Cột "Spec" là nguyên văn từ
`https://help.ctrader.com/fix/specification/`.

| Điểm | Tài liệu ngoài nói | Spec chính thức | Kết luận |
|---|---|---|---|
| **Mật khẩu** | Tag 50 SenderSubID "bắt buộc chứa mật khẩu" | Tag 50: *"Must be set to `QUOTE` if `TargetSubID=QUOTE`"*. Username ở **tag 553**, password ở **tag 554**, trong Logon | ❌ **SAI, NGUY HIỂM.** Làm theo thì logon thất bại **và** mật khẩu rò vào header của mọi message → mọi log raw FIX |
| SenderCompID | Tag 49 = "tên tài khoản" | *"`<Environment>.<BrokerUID>.<Trader Login>`"* → `demo.fxpro.10649643` | ❌ Sai |
| TLS | Bắt buộc, không có thì server ngắt | Tag 98: *"only transport-level security is supported. The valid value is `0`"* — TLS tầng socket là tuỳ chọn; cổng plain 5201/5202 tồn tại | ❌ Sai. Production dùng SSL, spike có thể dùng plain |
| Bid/Ask | Tag 267 = "0 Bid / 1 Ask" | Tag 267 *"Always set to `2`"*; **tag 269** mới là `0=Bid 1=Offer` | ❌ Nhầm tag |
| MarketDepth | (không nhắc) | Tag 264: *"`0` = Depth, `1` = Spot"* — **ngược** FIX chuẩn | ⚠️ Bỏ sót quirk dễ gây bug nhất (R11) |
| Seqnum | Reset về 1 "mỗi sáng thứ Hai" | *"sequence numbers reset on establishing a FIX session"* (141=Y); cuối tuần: **NOT STATED** | ⚠️ Vì reset mỗi lần logon nên "thứ Hai" là thừa. `MemoryStoreFactory` + `141=Y` là đủ |
| Heartbeat | 30 s, TestRequest | Đúng | ✅ Nhưng bỏ sót: QUOTE session **không gửi heartbeat khi đang stream** (R11) |
| TargetCompID | `CSERVER` viết hoa | *"The valid value is `CSERVER`"*; nhưng `Config.cfg` chính thức trong SDK Spotware và panel FxPro đều dùng `cServer` | ✅ **Đã giải quyết**: server không phân biệt hoa thường; dùng `cServer` như broker cấp |
| Symbol | Tag 55 là ID số; XAUUSD = 41 | Đúng | ✅ Khớp `symbolId 41` — vẫn verify qua `SecurityList` |
| **OrderQty** | Tag 38 tính bằng đơn vị cơ sở; vàng 1 lot = 100 oz | *"number of shares... precision 0.01"* | ✅ **Đúng và hữu ích** — hoá giải "`1 = 0.01`", xem §5 `volumeBUnits` |
| ExecutionReport | Đọc tag 39: 0/2/8; lý do ở tag 58 | Đúng | ✅ Plan đọc **cả 39 lẫn 150** |
| Checksum | mod 256, 3 chữ số | Đúng | ✅ QuickFIX/n tự làm |
| **Đóng position** | (không có gì) | **NOT STATED** | ❌ Không giúp gì cho R1 — Phase 0 câu 1 vẫn là cổng chặn |

---

## Phụ lục B — Nguồn đã đọc

Để câu hỏi "đã đọc hết tài liệu chưa" không phải hỏi lại. Cột cuối ghi mỗi nguồn đóng góp gì cho plan.

| Nguồn | Trạng thái | Đóng góp |
|---|---|---|
| `help.ctrader.com/fix/` — Getting started | ✅ đọc trực tiếp | Bối cảnh; không có kỹ thuật |
| `/benefits/` | ✅ | Không có kỹ thuật |
| `/limitations/` | ✅ | Không balance/equity/margin, không historical data; khuyến nghị Open API kèm → R10 |
| `/getting-credentials/` | ✅ | FIX symbol ID xem được trong cửa sổ thông tin symbol của cTrader desktop → kiểm `41 = XAUUSD` không cần SecurityList. QUOTE/TRADE credentials riêng, không dùng chéo |
| `/communication-model/` | ✅ | *"A session can include multiple physical connections"* → giải thích report nhân đôi (R11). Cuối tuần: không nói |
| `/sending-and-receiving-messages/` | ✅ | Logon mẫu chính thức `...56=CSERVER...57=TRADE|50=any_string|...553=12345|554=passw0rd!` → đóng vĩnh viễn điểm sai tag-50 ở Phụ lục A. Trang ghi "cập nhật 03/02/2017" |
| `/specification/` Rules of Engagement | ✅ | Nguồn nguyên văn cho Phụ lục A; tag 264/267/269/38/721/553/554/141 |
| `/faqs/` | ✅ | Report nhân đôi; QUOTE không heartbeat khi stream; không tag 266; message sai → im lặng → R11 |
| `FIX44-CSERVER.xml` | ✅ (qua luồng khảo sát phụ) | Tag tuỳ biến 1000–1008 |
| `github.com/spotware/quickfixnsamples.net` | ✅ tải `Config.cfg`, `SessionSettingsFactory.cs`, `ConsoleSample/Program.cs`, `Common/MessageExtensions.cs`, `Common/QuickFixNApp.cs`, `AspNetCoreSample/Services/FixClient.cs` | **R1** (721 ở nhánh market order), `TargetCompID=cServer`, khoá QuickFIX/n ở §4.6, `GetPosition` đọc 704/705/730/1000/1002, **`ToAdmin` phải tự gắn 553/554**, **topology 2 initiator**, `SecurityList` gửi qua TRADE, tag 1007/1008 đọc số thô, MarketDataRequest typed với 2 `NoMDEntryTypesGroup`. **`ConsoleSample` được dùng làm công cụ Phase 0** |
| `github.com/spotware/FIX-API-Sample` | ✅ tải `MessageConstructor.cs` | Comment nguyên văn về 721 ở dòng 346; xác nhận chéo R1 |

