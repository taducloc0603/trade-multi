# Phase 5 — Luồng LỆNH ĐANG MỞ, live, read-only

> **⚠️ ĐỌC TRƯỚC — nguồn gốc số liệu (ghi 2026-09-24):** mọi con số đo đạc trong tài liệu này (soak, phân
> phối tick, độ trễ, spread, gap, tỉ lệ skip) là của **FxPro 8220816**. Từ **2026-09-23 16:42** sàn B
> production là **Deriv `live.deriv.1551176`** vì FxPro chặn đặt lệnh qua FIX (`CHANNEL_IS_BLOCKED`, xem
> [Phase 7 P7-A1](phase-7-execution.md)). Phần nghiệm thu **CHỨC NĂNG** vẫn còn giá trị — cùng giao thức FIX,
> code không đổi. Phần **SỐ LIỆU** thì KHÔNG chuyển sang được: các mục soak phải đo lại trên Deriv.


> Mở thêm **TRADE session**, nhưng **chỉ để nhận** `RequestForPositions` / `PositionReport`.
> **Chưa gửi order nào.** `NullCTraderTradeExecutor` vẫn nguyên chỗ.

[← Phase 4](phase-4-quote-feed.md) · [Index](README.md) · Phase sau: [Phase 6](phase-6-history.md)

---

## Mục tiêu

Position cTrader hiện đúng trong app như một lệnh MMF bình thường, và — quan trọng hơn — **chứng minh
R2 không xảy ra**.

Đây là phase nguy hiểm nhất trong nhóm read-only, vì nó là lúc `GetLivePairTradeState` bắt đầu nhìn
thấy dữ liệu cTrader. Một sai sót ở đây khiến app **đóng chân A của một hedge đang mở thật**.

---

## Phụ thuộc phase trước

- Phase 4 đã soak nhiều ngày, QUOTE session ổn định. **Hoãn có điều kiện (2026-09-17):** soak và các mục P4-D1…D9
  chạy song song, bắt buộc xong trước Phase 7 — xem [Phase 4 "Cổng sang Phase 5"](phase-4-quote-feed.md).
- Phase 3: `CTraderPositionCache` (có content-version), `CTraderTicketCodec`, `CTraderSessionHealth`
  đã có và đã test.

---

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | `PositionsSynced=false` kéo dài thì làm gì? | Log `[CTRADER][WARN]` ngay; Telegram `CTRADER_POSITIONS_NOT_SYNCED` sau **60 s**. Không tự retry bằng cách gửi lệnh gì khác ngoài `RequestForPositions`. **ĐÃ QUYẾT (2026-09-17): WARN ngay + Telegram `CTRADER_POSITIONS_NOT_SYNCED` sau 60 s.** |
| 2 | Tần suất `RequestForPositions` định kỳ? | Một lần khi logon + một lần mỗi **60 s** làm reconciliation. ExecutionReport là nguồn chính, AN/AP là lưới an toàn. `710 PosReqID` mỗi lần một chuỗi mới (`pos-{unixMs}`) để phân biệt batch. **ĐÃ QUYẾT (2026-09-17): logon + mỗi 60 s, `710` mới mỗi lần.** |
| 3 | Batch AP "xong" khi nào? | Khi (a) nhận AP có `728=2` (không có position — **vẫn là xong, `PositionsSynced=true`, danh sách rỗng**), hoặc (b) đã nhận đủ `727 TotNumPosReports` AP với `728=0` cho cùng `710`. Thiếu ca (a) thì tài khoản trống **không bao giờ** qua được R2 gate. **ĐÃ QUYẾT (2026-09-17): đúng (a)/(b) — đã cài trong `CTraderPositionCache.ApplyPositionReport` Phase 3 (chỉ một cách hợp lệ).** |
| 4 | Position **không phải do app mở** (user mở tay trong cTrader) hiển thị thế nào? | Vẫn hiện ở tab Trade như MMF đang làm. `IsAppGeneratedTicket` sẽ lọc chúng ra khỏi logic pair — **giữ nguyên hành vi đó**, không sửa. **ĐÃ QUYẾT (2026-09-17): hiện ở tab Trade, giữ nguyên lọc `IsAppGeneratedTicket`.** |

---

## Việc làm

| File | Việc | Rủi ro |
|---|---|---|
| `Infrastructure/CTrader/CTraderFixSession.cs` | Start **initiator thứ hai** cho TRADE (`IApplication` + `SessionSettings` riêng, theo `FixClient.cs` của Spotware); gửi `RequestForPositions(AN)`; nhận `PositionReport(AP)` + `ExecutionReport(8)`. Nếu Phase 4 đã phải mở TRADE cho SecurityList thì phase này chỉ **mở rộng** nó | — |
| `Infrastructure/CTrader/CTraderAwareTradesReader.cs` (mới) | Decorator `ITradesSharedMemoryReader` | **R2**, **R3** |
| `Infrastructure/DependencyInjection.cs` | Đăng ký decorator bọc `TradesSharedMemoryReader` | — |

### Decorator

```csharp
public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName)
{
    if (!_routing.IsCTraderTradeMap(mapName))
        return _inner.ReadTrades(mapName);        // MMF như cũ, không đổi gì

    return _cache.ReadAsMapResult();              // R2 + R3 nằm trong đây
}
```

### R2 — invariant cứng nhất của cả dự án

`ReadAsMapResult()` **phải** trả `SharedMapReadResult.MapNotFound(...)` khi bất kỳ điều kiện nào chưa
thoả:

```
TradeLoggedOn && SymbolResolved && PositionsSynced
```

và quay lại `MapNotFound` **ngay lập tức** khi logout hoặc đứt socket.

> **Vì sao:** nếu trả `IsMapAvailable=true, Count=0` trong cửa sổ chưa sync xong thì
> `GetLivePairTradeState` → `OnlyAOpen` → `TryDetectAndHandleExternalPartialClose` kết luận B đã bị
> đóng bên ngoài → **đóng chân A của một hedge đang mở thật**.
>
> `!IsMapAvailable` → `MapUnavailableOrParseError` → watchdog skip, external-partial-close skip,
> `TryRefreshCloseLeg` fail closed. Đây là đòn bẩy đúng, đã kiểm chứng trong code.

### R3 — `Timestamp` là content-version, không phải thời gian

Dùng `CTraderPositionCache.Version` từ Phase 3. Không phải `UtcNow`. Không phải hằng số.
Xem bảng hậu quả ở [README §3 R3](README.md).

### Các field phải đặt đúng

| Field của `TradeSharedRecord` | Nguồn | Ghi chú |
|---|---|---|
| `Ticket` | `CTraderTicketCodec.Encode(positionId)` | **R4** — đây cũng là dạng lưu vào `current_slots` |
| `TradeType` | tag 54: `1=Buy → 0`, `2=Sell → 1` | |
| `Price` | `730 SettlPrice` (AP) hoặc `6 AvgPx` (ER) | Giá mở trung bình |
| `Lot` | `704 LongQty` / `705 ShortQty` | |
| `Symbol` | **`1007 SymbolName`** từ `SecurityList` | Phải trùng cái quote side báo cho B (§4.2) |
| `OpenEaTimeLocal` | `Environment.TickCount64` **lúc parse ExecutionReport** | §4.4 — để `0` sẽ sinh execution-latency rác nuôi báo động Telegram sai |
| `Sl` / `Tp` | `0` | R10 — FIX không gắn được SL/TP |
| `Profit` | `0` hoặc tính từ AvgPx | Chỉ hiển thị; profit thật do `CalculateTradeProfit` tính lại từ điểm giá |
| `Connected` (header) | `1` khi cả hai session logged on, `0` khi không, **không bao giờ `-1`** | §4.5 |

### R10 — cảnh báo nhánh inferred

`BuildInferredResyncedOpenSlots` ghép chân A/B theo `TradeType` rồi thứ tự `TimeMsc`. AP **không có**
timestamp mở nên record cTrader sẽ có `TimeMsc == 0` và thứ tự suy giảm.

Thực tế `BuildTrackedResyncedOpenSlots` (dùng `_pairIdByTicket` từ `current_slots`) thắng bất cứ khi
nào `current_slots` còn — nên chỉ cắn ở nhánh fallback legacy với >1 slot mở sau restart mất
`current_slots`. **Chấp nhận, nhưng log `[CTRADER][WARN]`** khi rơi vào nhánh inferred.

---

## Rủi ro liên quan

**R2** (chủ đạo) · **R3** · **R4** · R10 · §4.4 · §4.5

---

## Nghiệm thu

> **Tái cấu trúc 2026-09-16:** phase này chia hai lớp. **Lớp 1 (ở đây)** chạy trên **tài khoản trống**,
> không chạm tiền. **Lớp 2 — cần position thật** — chuyển sang [Phase 7 Bước B](phase-7-execution.md)
> để chạy chung phiên tiền thật. Cổng sang Phase 6 chỉ đòi Lớp 1.

Chạy 2026-09-17 trên Windows 11, live 8220816 (trống), **không bấm Start** (tiếp #8 Phase 4). Bằng chứng:
`Desktop/trade-log/20260917-ctrader.log` (raw log chẩn đoán bật bằng `CTRADER_FIX_RAW_LOG=1`, đã che 554),
`netwatch2.ps1` (socket 5211/5212 mỗi 250 ms), ảnh tab Trade.

### Thiết kế đã duyệt ở Bước 3 (2026-09-17)

- `CTraderTradeSession` riêng (transport riêng, chỉ start TRADE): SecurityList + `RequestForPositions` lúc logon, 60 s
  reconciliation, WARN chưa sync ngay + Telegram `CTRADER_POSITIONS_NOT_SYNCED` sau 60 s; không gửi order nào.
- `CTraderAwareTradesReader` bọc `TradesSharedMemoryReader`; vòng đời TRADE do decorator điều khiển → rollback = 1 dòng DI.
- `CTraderPositionCache` (Phase 3): position mới qua AP stamp `Environment.TickCount64`, reconciliation giữ stamp cũ.
- `DashboardViewModel.BuildResyncedOpenSlots`: chỉ THÊM log R10 `[CTRADER][WARN]` khi rơi nhánh inferred với ticket cTrader.
- Mất mạng im lặng trên TRADE: chưa có kiểm tra sống (heartbeat ~35–60 s) — chấp nhận, gắn với P4-D3/D4.
- Chẩn đoán (duyệt riêng): biến môi trường `CTRADER_FIX_RAW_LOG=1`; log "config thiếu" hạ INFO khi config chưa nạp.

### Lớp 1 — tài khoản trống, không chạm tiền (nghiệm thu tại phase này)

- [x] **`728=2` path (R2):** 17:23:31.545 TRADE logon → `710=pos-1789640611545` → AP `727=0|728=2` → 17:23:31.847
      `PositionsSynced=true count=0 version=0` → 17:23:31.886 `trades map AVAILABLE count=0 version=0 connected=1`.
      Tab Trade sàn B (`CTRADER_B_Trades`): "Chưa có dữ liệu" (xanh) thay cho "Không tìm thấy map". Unit
      `R2_NoPositions728Eq2_IsSyncedEmpty_Available`.
- [x] **Cửa sổ chưa sync (R2):** giữa logon và AP, `ReadTrades` = `MapNotFound` (`[STATS][TRADE]` phút đầu: 41/1345 lần đọc
      không available, `reads_available` chỉ tăng sau 17:23:31.886). `GetLivePairTradeState` với `!IsMapAvailable` →
      `MapUnavailableOrParseError` (code [DashboardViewModel.cs:4978-4983], không sửa); log VM của state này không ghi được
      khi chưa Start (logger phiên) — bằng chứng là log chuyển trạng thái map + unit
      `R2_UnsyncedWindow_IsMapNotFound_NeverAvailableWithZeroCount` (IsMapAvailable=false, Timestamp=0 — không phải true/Count=0).
- [x] **Kill socket TRADE** (TCPView Close Connection, QUOTE giữ sống): `TRADE logged out` 17:28:47.307 → `trades map MapNotFound`
      17:28:47.327 (**20 ms**); netwatch 17:28:48.1 chỉ còn 5211 Established. Watchdog/external-partial-close: nhận
      `MapUnavailableOrParseError` và đều đang tắt vì chưa Start (guard `IsTradingLogicEnabled`) — log "skip" cần Start → Phase 7 B.
- [x] **Logout rồi logon lại:** 17:28:49.680 logon → WARN `PositionsSynced=false` → `710=pos-1789640929680` → 17:28:49.904 synced →
      17:28:49.922 AVAILABLE. Không có lần đọc available trước khi sync. Unit `R2_Logout_MapNotFoundImmediately_RelogonNeedsFreshBatch`.
- [x] **R3 với danh sách rỗng:** 17:23:31 → 17:28:31, 5 cửa sổ `[STATS][TRADE]` liên tiếp `version=0 count=0`, qua 5 lần
      reconciliation + 1 lần relogon. Timestamp không đổi → `ShouldApplyTradeResult` trả false (so khớp timestamp,
      [DashboardViewModel.cs:5957]) → `ApplyTradeResult` không rebuild. (Đếm log `ApplyTradeResult` cần Start.) Unit
      `R3_EmptyList_VersionStableAcrossReadsAndReconciliation` (600 lần đọc).
- [x] **Reconciliation 60 s:** `710` mới mỗi phút: …611545, …672145, …732147, …792141, …852156, …913140; mỗi AP `728=2` lặp,
      `version` giữ 0. Unit `Reconciliation_NotSentBefore60s`.
- [x] **Đổi `platform_b` về `mt5`:** Save 17:30:58 → QUOTE unsubscribe `263=2` + `35=5`, TRADE `35=5`, cả hai được server xác nhận;
      `QUOTE stopped` 17:31:00.570, `TRADE stopped` 17:31:00.706; netwatch 17:31:00.2 không còn 5211/5212; tab Trade B đọc lại
      `Local\MT_B_Trades`. Decorator đi thẳng MMF khi không phải map cTrader (unit `Decorator_NonCTraderMapOrPlatform_PassesThroughToMmf`).

### Lớp 2 — cần position thật → **Phase 7 Bước B** (chỉ liệt kê, không nghiệm thu ở đây)

- Position mở tay hiện ở tab Trade: ticket mã hoá, symbol, type, open price đúng; profit đúng chiều. → Phase 7 B
- Đóng tay → row biến mất ≤ 500 ms. Mở 2–3 position → đủ, không nhân bản. → Phase 7 B
- **Restart app khi đang có position B** → cửa sổ chưa sync là `MapUnavailableOrParseError`, không
  external-partial-close, watchdog "skip". → Phase 7 B
- R3: mở position mới → version tăng đúng một lần; nút "Đóng" per-pair ăn một lần bấm. → Phase 7 B
- R4: `TryDecode(ticket)` = Position ID thật trên cTrader Web. → Phase 7 B (unit `R4_PositionFromReport_EncodedTicket_…` đã pass)

### Vấn đề mở

- **P5-O1 — CHẨN ĐOÁN 2026-09-21 (CHƯA đủ): lỗi PARSE ở CLIENT, không phải phía sàn.**
  Tái hiện nặng lúc 19:23 (QUOTE logout ~29 lần/phút, ~500 lần logon trong 18 phút). Raw log `MaskedFixLogFactory`
  (bật bằng `CTRADER_FIX_RAW_LOG=1`) cho thấy chính QuickFIX/n **tự** gửi `Logout 58=Incorrect BeginString (FIX.4.4)`
  sau khi ném `QuickFix.UnsupportedVersion` từ
  `DefaultMessageFactory.Create(beginString, msgType, groupCounterTag)` ← `Message.SetGroup` ← `Message.FromString`.
  Tức là lỗi **tra factory cho repeating group**, KHÔNG phải BeginString thật sự lệch — message kích hoạt là
  SecurityList `35=y` trả về **toàn bộ 316 symbol, 9 369 byte** (nhóm 146). 14 lần ném đều nằm trong các lần chạy
  trước bản sửa; sau bản sửa: 0.
  **Cách sửa (chủ dự án duyệt 2026-09-21):**
  - **A — hỏi SecurityList có lọc symbol:** `CTraderMessageFactory.SecurityListRequest(reqId, symbolId)` set thêm
    `55=<symbolId>`; cả QUOTE lẫn TRADE dùng. Live 19:59:58: response còn `9=168` / `146=1`, hai session đều
    `symbolName=XAUUSD symbolId=41 digits=2`, `728=2`, map trades/history AVAILABLE, cross-check 1.0000.
  - **B — chỉ subscribe giá SAU khi nhận `35=y`:** logon chỉ gửi SecurityListRequest; `MarketDataRequest` gửi khi
    SecurityList đã xử lý xong (lớp chống chen W vào giữa message dài, đồng thời không có tick nào trước khi kiểm
    digits theo R6). Nếu 10 s không có SecurityList thì **hỏi lại**, tuyệt đối không subscribe mò.
  - **Ngắt mạch kèm theo:** > 5 lần mất phiên trong 60 s → dừng hẳn session, KHÔNG tự nối lại, bắn
    `CTRADER_RECONNECT_STORM`. Đã nghiệm thu trên live (6 lần logout thay vì ~500) và bằng
    `CTraderReconnectStormTests`.
  Bằng chứng live sau sửa: 19:59:53 → 20:05:59 **0 lần QUOTE logged out**, `stale_events=0`, `x_anomalies=0`,
  ~322–435 tick/phút, `reads_disconnected=0`.

- **P5-A1 — NGUYÊN NHÂN GỐC, ĐÓNG 2026-09-23.** Sửa A (lọc SecurityList) chỉ làm lỗi thưa đi chứ không hết:
  22/09 20:43:10 QUOTE nhận `35=y` **đã lọc 1 symbol (9=168, 146=1)** vẫn ném `UnsupportedVersion`, trong khi
  TRADE cùng tiến trình parse đúng **cùng một message**. Message hỏng và message chạy tốt lúc 20:46 giống nhau
  từng tag ⇒ không phải do nội dung.
  Chẩn đoán bằng reflection trên QuickFIX/n 1.10.0: `new DefaultMessageFactory()` **quét file DLL trong thư mục
  app** (`LoadLocalDlls` + `GetAppDomainAssemblies` + `IsMessageFactory`) để dựng bảng factory, và **mỗi session
  dựng một bảng RIÊNG**. Lần quét nào không bắt được `QuickFix.FIX44.dll` thì bảng thiếu khoá `"FIX.4.4"`, và
  khi đó MỌI message có repeating group (`35=y` nhóm 146, `35=W` nhóm 268) ném `UnsupportedVersion` ngay trong
  `Message.SetGroup` → thư viện tự gửi `Logout 58=Incorrect BeginString` → vòng lặp logout/logon.
  Parse tự nó KHÔNG lỗi: 80 000 lần, 1/2/8 luồng, dictionary + factory dùng chung → **0 lần ném**.
  **Sửa:** `QuickFixCTraderTransport.CreateMessageFactory()` khai báo thẳng
  `new DefaultMessageFactory(new IMessageFactory[] { new QuickFix.FIX44.MessageFactory() })` và truyền qua
  overload `SocketInitiator(app, store, settings, logFactory, messageFactory)` — hết phụ thuộc thư mục và thứ
  tự nạp assembly. Test khoá: `CTraderMessageFactoryWiringTests`.

- **P5-O2 — ngắt mạch bắn nhầm, ĐÓNG 2026-09-23.** Live 23/09 05:36:03 sàn reset phiên hằng ngày; QUOTE lên lại
  sau 2 s, TRADE gửi Logon 5 lần (05:36:05/07/09/11/13) **không được trả lời**. Mỗi lần thử hỏng vẫn bị
  `TrackLogoutForStorm` đếm là "mất phiên" → đủ 6/60 s → dừng hẳn; TRADE **nằm chết 3 h 17 m**, chỉ 1 Telegram
  lúc đầu rồi im. **Sửa:** (1) chỉ đếm khi `wasLoggedOn == true`; (2) sau ngắt mạch **tự thử lại sau 5 phút,
  tối đa 3 lần**, số lượt chỉ được xoá khi session đứng vững ≥ 5 phút; (3) hết lượt thì nhắc Telegram **mỗi
  15 phút** thay vì im lặng. Test khoá: `FailedLogonAttempts_DoNotTripBreaker`,
  `Quote_BreakerAutoRetriesAfterCooldown_ThenStopsForGood`, `Trade_BreakerAutoRetriesAfterCooldown`.

### Gate chung

- [x] Test suite: **922 total / 911 pass / 11 fail** — 11 tên trùng baseline memo §2.2b. Phase 5 thêm 18 test
      (`CTraderTradeSessionTests`). Build App `--no-incremental`: 0 error, 3 warning `CA1416` baseline.
- [ ] Soak ≥ 2 ngày tài khoản trống — **CHƯA KIỂM — cần soak 2 ngày** (bắt đầu lại lúc chuyển về cTrader sau 17:31 ngày 2026-09-17).

---

## Rollback

**Revert dòng đăng ký decorator ở `Infrastructure/DependencyInjection.cs` là đủ** để quay về đọc MMF
— không cần revert cả commit. Đây là lý do decorator được chọn thay vì sửa trực tiếp reader.

Nếu `current_slots` đã lưu ticket dạng mã hoá thì sau khi rollback chúng sẽ không khớp MMF nào và bị
recovery discard như snapshot stale — hành vi đã có sẵn, an toàn.

---

## Cổng sang Phase 6

### Hoãn có điều kiện (chủ dự án quyết 2026-09-17)

> **Tiêu chí soak theo PHIÊN (chủ dự án quyết 2026-09-21).** Máy phải tắt mỗi ngày khi đi làm về và không có
> VPS, nên "2 ngày liên tục" là không khả thi. Thay bằng bộ điều kiện dưới đây — **không phải nới lỏng**, mà
> đổi cách gom thời gian, vì 4 thứ soak cần chứng minh (rò rỉ, tự khỏi sau đứt phiên, P5-O1, R2) không đòi
> 48 giờ liền một mạch. Áp trên **bản build cuối** (`b8b0bbf`), dùng chung cho P4-D1, P5-D1 và gate Phase 6:
>
> | # | Điều kiện | Cách đo |
> |---|---|---|
> | S1 | Tổng **≥ 24 giờ chạy tích luỹ**, tối đa 4 phiên | cộng thời gian các phiên trong `-ctrader.log` |
> | S2 | **≥ 2 đêm** qua trọn giờ nghỉ 03:59:45 → 05:00 **và** mốc 00:00 UTC (07:00 giờ ta) | log hai mốc: logout → tự logon lại ≤ 30 s |
> | S3 | **≥ 1 phiên liên tục ≥ 10 giờ** | RAM/handle đầu–cuối phiên đó lệch < 20 % |
> | S4 | **0 lần** P5-O1 (QUOTE logout lặp > 3 lần trong 5 phút) | grep `QUOTE logged out` |
> | S5 | **0 lần** map B available khi chưa sync; 0 external-partial-close sai | `trades map AVAILABLE` luôn đứng sau `PositionsSynced=true` |
> | S6 | Mỗi phiên kết thúc bằng **đóng app bằng nút X** | log có `unsubscribe` + `QUOTE/TRADE stopped` |
>
> Một phiên cuối tuần **≥ 40 giờ** (thứ Bảy → Chủ Nhật ở nhà) coi như đạt luôn S1 + S3.
> Cách chạy: bấm đúp `Desktop\start-soak-ctrader.cmd` sau mỗi lần bật máy (tự bật raw log, chặn mở 2 instance),
> **không bấm Start**; trước khi shutdown thì đóng app bằng nút X.
> Cái chấp nhận mất: không bắt được rò rỉ rất chậm (> 24 h) — bù lại Phase 7 chỉ chạy ~3 giờ.

Phase 5 **tạm đóng để sang Phase 6** (chỉ đọc, tài khoản trống). Mục dưới đây **bắt buộc xong trước Phase 7**.
Ràng buộc để soak không bị reset: Phase 6 code + unit test **offline**, build ra thư mục tạm, **không restart app đang soak**;
nghiệm thu live Phase 6 dồn vào **một lần restart có chủ đích** sau ≥ 1 đêm soak; sau đó soak tiếp bằng bản Phase 6 (tính cho
cả Phase 4/5/6). **Không chạy 2 instance app cùng lúc** (cùng login cTrader).

| ID | Việc còn treo | Cách đóng | Chặn |
|---|---|---|---|
| P5-D1 | ~~Soak ≥ 2 ngày~~ **1 đêm xong (10,5 h)**: TRADE sống liên tục, `728=2` mỗi 60 s (624 lần), `version=0` và `history_version=0` suốt, `reads_available` = `reads` mỗi phút, không external-partial-close; 2 lần mất phiên (05:00 session reset, 07:00 seqnum) đều tự khỏi ≤ 3 s. Còn: chạy tiếp cho đủ 2 ngày + cuối tuần. | `[STATS][TRADE]` | Phase 7 |
| P5-O1 | **ĐÓNG 2026-09-21** — nguyên nhân: QuickFIX/n ném `UnsupportedVersion` khi dựng repeating group của SecurityList 316 symbol rồi tự gửi Logout. Sửa A (hỏi SecurityList lọc `55=<symbolId>`) + B (subscribe sau `35=y`, hỏi lại sau 10 s) + ngắt mạch bão reconnect. | `-ctrader.log`, `CTraderReconnectStormTests`, `CTraderQuoteSessionTests` | — (đã đóng) |


- [x] Toàn bộ checklist **Lớp 1** pass, đặc biệt cửa sổ chưa sync và `728=2` path (xem Nghiệm thu, 2026-09-17).
- [ ] Không có bất kỳ lần nào external-partial-close bị kích hoạt sai trong suốt thời gian soak → **hoãn P5-D1**.
- [x] Danh sách Lớp 2 đã được chép sang checklist Phase 7 Bước B (không bỏ sót) — 2026-09-17 đối chiếu 5/5, bổ sung mục "row biến mất ≤ 500 ms" và "kill TRADE có Start".
