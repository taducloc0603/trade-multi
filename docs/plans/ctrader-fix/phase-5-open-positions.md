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

- Phase 4 đã soak nhiều ngày, QUOTE session ổn định.
- Phase 3: `CTraderPositionCache` (có content-version), `CTraderTicketCodec`, `CTraderSessionHealth`
  đã có và đã test.

---

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | `PositionsSynced=false` kéo dài thì làm gì? | Log `[CTRADER][WARN]` ngay; Telegram `CTRADER_POSITIONS_NOT_SYNCED` sau **60 s**. Không tự retry bằng cách gửi lệnh gì khác ngoài `RequestForPositions`. |
| 2 | Tần suất `RequestForPositions` định kỳ? | Một lần khi logon + một lần mỗi **60 s** làm reconciliation. ExecutionReport là nguồn chính, AN/AP là lưới an toàn. `710 PosReqID` mỗi lần một chuỗi mới (`pos-{unixMs}`) để phân biệt batch. |
| 3 | Batch AP "xong" khi nào? | Khi (a) nhận AP có `728=2` (không có position — **vẫn là xong, `PositionsSynced=true`, danh sách rỗng**), hoặc (b) đã nhận đủ `727 TotNumPosReports` AP với `728=0` cho cùng `710`. Thiếu ca (a) thì tài khoản trống **không bao giờ** qua được R2 gate. |
| 4 | Position **không phải do app mở** (user mở tay trong cTrader) hiển thị thế nào? | Vẫn hiện ở tab Trade như MMF đang làm. `IsAppGeneratedTicket` sẽ lọc chúng ra khỏi logic pair — **giữ nguyên hành vi đó**, không sửa. |

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

Mở/đóng position **bằng tay trong app cTrader**; app TradeDesktop **chỉ quan sát**.

### Dữ liệu đúng

- [ ] Position mở tay hiện ở tab Trade: ticket (dạng đã mã hoá), symbol, type, open price đều đúng.
- [ ] Profit realtime chân B nhúc nhích **đúng chiều** khi giá chạy.
- [ ] Đóng tay trong cTrader → row biến mất khỏi app trong ≤ 1 chu kỳ poll (500 ms).
- [ ] Mở 2–3 position cùng lúc → hiện đủ, không nhân bản, không mất.

### Test R2 trực diện — quan trọng nhất

- [ ] **Restart app trong lúc đang có position B.** Trong cửa sổ chưa sync:
  - [ ] `GetLivePairTradeState` phải là `MapUnavailableOrParseError`
  - [ ] **Không được** là `OnlyAOpen`
  - [ ] Log **không hề có** external-partial-close
  - [ ] Watchdog log ghi "skip" chứ không phải "self-heal"
- [ ] **Kill socket TRADE** (giữ QUOTE sống) → trades map thành unavailable **ngay**, mọi nhánh skip.
- [ ] Logout rồi logon lại → `PositionsSynced` về `false` rồi `true`, không có cửa sổ nào
      `IsMapAvailable=true` mà chưa sync.

### Test R3 trực diện

- [ ] Để yên **5 phút** không thao tác → `ApplyTradeResult` **không** rebuild liên tục
      (grep log, đếm số lần).
- [ ] Nút "Đóng" per-pair **bấm một lần ăn ngay**, không phải bấm 2–3 lần (cạm bẫy CLAUDE.md §5).
- [ ] Mở một position mới → version tăng đúng một lần, `ApplyTradeResult` chạy.

### Test R4

- [ ] Ticket hiển thị nằm ngoài dải ticket MT; `TryDecode` cho lại đúng positionId gốc (kiểm bằng log).
- [ ] Không có cảnh báo cross-wire nào trong log.

### Không hồi quy

- [ ] Đổi `platform_b` về `mt5` → hành vi y hệt trước Phase 5.
- [ ] Hai chân MT4/MT5 chạy song song bình thường trong lúc B là cTrader ở chế độ quan sát.

### Gate chung

- [ ] Test suite không tăng so với baseline Phase 0. Build sạch.
- [ ] Soak ít nhất **2 ngày** với position mở tay để yên.

---

## Rollback

**Revert dòng đăng ký decorator ở `Infrastructure/DependencyInjection.cs` là đủ** để quay về đọc MMF
— không cần revert cả commit. Đây là lý do decorator được chọn thay vì sửa trực tiếp reader.

Nếu `current_slots` đã lưu ticket dạng mã hoá thì sau khi rollback chúng sẽ không khớp MMF nào và bị
recovery discard như snapshot stale — hành vi đã có sẵn, an toàn.

---

## Cổng sang Phase 6

- [ ] Toàn bộ checklist nghiệm thu pass, **đặc biệt là test R2 trực diện**.
- [ ] Không có bất kỳ lần nào external-partial-close bị kích hoạt sai trong suốt thời gian soak.
