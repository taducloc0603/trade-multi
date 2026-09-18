# Phase 5 — Luồng LỆNH ĐANG MỞ, live, read-only

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

- **P5-O1 — QUOTE bị server logout lặp khi chạy chung TRADE (17:19:12–17:19:39, 13 lần, `35=5` không có 58, ~0.45 s sau logon,
  không có 35=3/j).** Không tái hiện ở 3 lần chạy sau (17:22:53, 17:23:31 kéo dài; relogon TRADE 17:28:49). Loại trừ trùng
  `SessionID` QuickFIX/n (probe: `…/QUOTE->cServer/QUOTE` ≠ `…/TRADE->cServer/TRADE`). Raw log để bật suốt soak để bắt
  chuỗi message thật nếu tái diễn. **Bắt buộc có kết luận trước Phase 7.**

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

Phase 5 **tạm đóng để sang Phase 6** (chỉ đọc, tài khoản trống). Mục dưới đây **bắt buộc xong trước Phase 7**.
Ràng buộc để soak không bị reset: Phase 6 code + unit test **offline**, build ra thư mục tạm, **không restart app đang soak**;
nghiệm thu live Phase 6 dồn vào **một lần restart có chủ đích** sau ≥ 1 đêm soak; sau đó soak tiếp bằng bản Phase 6 (tính cho
cả Phase 4/5/6). **Không chạy 2 instance app cùng lúc** (cùng login cTrader).

| ID | Việc còn treo | Cách đóng | Chặn |
|---|---|---|---|
| P5-D1 | Soak ≥ 2 ngày liên tục qua cuối tuần, tài khoản trống: TRADE sống, AP `728=2` mỗi 60 s, không rò bộ nhớ/handle; không có external-partial-close sai | `-ctrader.log` (`[STATS][TRADE]`), Task Manager; bản build cuối cùng trước Phase 7 | Phase 7 |
| P5-O1 | QUOTE bị server logout lặp khi chạy chung TRADE (xem Vấn đề mở) | Raw log bật suốt soak; tái diễn → phân tích chuỗi message; không tái diễn qua soak → ghi kết luận | Phase 7 |


- [x] Toàn bộ checklist **Lớp 1** pass, đặc biệt cửa sổ chưa sync và `728=2` path (xem Nghiệm thu, 2026-09-17).
- [ ] Không có bất kỳ lần nào external-partial-close bị kích hoạt sai trong suốt thời gian soak → **hoãn P5-D1**.
- [x] Danh sách Lớp 2 đã được chép sang checklist Phase 7 Bước B (không bỏ sót) — 2026-09-17 đối chiếu 5/5, bổ sung mục "row biến mất ≤ 500 ms" và "kill TRADE có Start".
