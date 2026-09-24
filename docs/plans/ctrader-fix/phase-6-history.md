# Phase 6 — Luồng LỊCH SỬ, live, read-only

> **⚠️ ĐỌC TRƯỚC — nguồn gốc số liệu (ghi 2026-09-24):** mọi con số đo đạc trong tài liệu này (soak, phân
> phối tick, độ trễ, spread, gap, tỉ lệ skip) là của **FxPro 8220816**. Từ **2026-09-23 16:42** sàn B
> production là **Deriv `live.deriv.1551176`** vì FxPro chặn đặt lệnh qua FIX (`CHANNEL_IS_BLOCKED`, xem
> [Phase 7 P7-A1](phase-7-execution.md)). Phần nghiệm thu **CHỨC NĂNG** vẫn còn giá trị — cùng giao thức FIX,
> code không đổi. Phần **SỐ LIỆU** thì KHÔNG chuyển sang được: các mục soak phải đo lại trên Deriv.


> Vẫn **chưa gửi order nào**. `NullCTraderTradeExecutor` vẫn nguyên chỗ.

[← Phase 5](phase-5-open-positions.md) · [Index](README.md) · Phase sau: [Phase 7](phase-7-execution.md)

---

## Mục tiêu

Hoàn thiện cửa dữ liệu thứ ba. Sau phase này, **toàn bộ chiều đọc** của cTrader đã xong và app nhìn
chân B y hệt một chân MMF.

Cửa này quan trọng hơn vẻ ngoài của nó: `RegisterCloseExecutionForNewHistoryTickets` là **cơ chế xác
nhận đóng lệnh** của app. Không có history thì Phase 7 không thể biết lệnh đã đóng xong.

---

## Phụ thuộc phase trước

- **Hoãn có điều kiện (2026-09-17):** Phase 5 Lớp 1 đạt; soak P5-D1 + P5-O1 chạy song song, bắt buộc xong trước Phase 7.
  Làm Phase 6 **offline trước** (không restart app đang soak), nghiệm thu live dồn vào một lần restart có chủ đích.
- Phase 5 đã merge và soak: trades decorator ổn định, R2 đã được chứng minh không xảy ra.
- Phase 3: `CTraderHistoryProjector` đã có và đã test.

---

## Chốt trước khi code

| # | Câu hỏi | Bối cảnh |
|---|---|---|
| 1 | `Profit` và `Commission` của history B là **số tổng hợp**, không phải số broker cấp (R10). Hiển thị thế nào để không bị hiểu nhầm? | FIX không trả P&L. Đề xuất: điền `0` cho `Commission`, tính `Profit` từ `(closePrice - openPrice)` theo điểm giá, và **ghi rõ trong README §8** rằng cột này với chân cTrader là số tính lại. Nếu tab History đang hiển thị như số broker thì cần một chú thích trên UI. **ĐÃ QUYẾT (2026-09-19): Commission=0, Profit tính lại; chỉ ghi README §8 + log, KHÔNG sửa UI.** |
| 2 | Position đóng **một phần** (partial close) thì sinh mấy record history? | Theo kết quả Phase 0 câu 5. Nếu cTrader cho partial close thì mỗi lần đóng một phần là một fill — cần quyết: một record mỗi fill, hay chỉ ghi khi position đóng hẳn. Đề xuất: **chỉ ghi khi position đóng hẳn** (`LeavesQty = 0`), vì app không dùng partial close. **ĐÃ QUYẾT (2026-09-19): chỉ ghi khi position đóng hẳn (biến mất khỏi cache); kiểm lại Phase 7 Bước A câu 5.** |
| 3 | Giữ history bao lâu trong bộ nhớ? | MMF history map có `HISTORY_MEMORY_SIZE 65536` / `HISTORY_RECORD_SIZE 124` ≈ 528 record. Đề xuất giữ tương đương, FIFO. **ĐÃ QUYẾT: 528 FIFO — đã có `CTraderHistoryProjector.DefaultCapacity` (Phase 3).** |
| 4 | (phát sinh 2026-09-19) Position biến mất qua PositionReport mà **không có fill đóng** (đóng lúc mất kết nối TRADE, ER bị lỡ)? | **ĐÃ QUYẾT: chỉ log `[CTRADER][TRADE][WARN]`**, không bịa record (không có ClosePrice thật). Xét lại ở Phase 7. |

---

## Việc làm

| File | Việc | Rủi ro |
|---|---|---|
| `Infrastructure/CTrader/CTraderAwareHistoryReader.cs` (mới) | Decorator `IHistorySharedMemoryReader`, cùng khuôn với trades decorator | **R2**, **R3** |
| `Infrastructure/CTrader/CTraderFixSession.cs` | Nối `CTraderHistoryProjector`: fill của order đóng → `HistorySharedRecord` | §4.4 |
| `Infrastructure/DependencyInjection.cs` | Đăng ký decorator | — |

### Decorator — cùng khuôn Phase 5

```csharp
public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
{
    if (!_routing.IsCTraderHistoryMap(mapName))
        return _inner.ReadHistory(mapName);

    return _projector.ReadAsMapResult();
}
```

**Áp dụng nguyên R2 và R3** như trades decorator:
- `MapNotFound` cho tới khi `TradeLoggedOn && SymbolResolved && PositionsSynced`, và ngay khi đứt.
- `Timestamp` là **content-version riêng cho history**, không dùng chung counter với trades
  (hai map độc lập, `ShouldApplyHistoryResult` theo dõi riêng).

### Nhận diện "order này là order đóng"

Một `ExecutionReport` khớp lệnh là order **đóng** khi nó mang `721 PosMaintRptID` của một position
đang có trong cache **và** làm position đó biến mất.

> **Không suy đoán theo chiều lệnh.** Một market order ngược chiều với `721` có thể là đóng, nhưng
> cũng có thể là mở position mới nếu `721` không khớp. Nguồn sự thật là **position biến mất khỏi
> cache**, chéo kiểm bằng `PositionReport` định kỳ.

### Các field phải đặt đúng

| Field của `HistorySharedRecord` | Nguồn | Ghi chú |
|---|---|---|
| `Ticket` | `CTraderTicketCodec.Encode(positionId)` | **Phải trùng** ticket đã dùng ở trades map (R4) |
| `OpenPrice` | Giá mở đã lưu trong position cache | |
| `ClosePrice` | `6 AvgPx` của fill đóng | |
| `CloseEaTimeLocal` | `Environment.TickCount64` **lúc parse fill đóng** | §4.4 — nuôi `ComputeExecutionMilliseconds`; để `0` sinh số rác |
| `OpenTimeMsc` | Từ position cache, hoặc `0` nếu khôi phục từ AP | R10 |
| `CloseTimeMsc` | tag 60 `TransactTime` của fill | |
| `Commission` | `0` | R10 — FIX không trả |
| `Profit` | Tính từ `(ClosePrice - OpenPrice)` theo điểm giá | R10 — **là số tính lại**, không phải số broker |

---

## Rủi ro liên quan

**R2** · **R3** · R4 (ticket phải trùng trades map) · **R10** (field tổng hợp) · §4.4

---

## Nghiệm thu

> **Tái cấu trúc 2026-09-16:** như Phase 5 — **Lớp 1** (tài khoản trống) nghiệm thu ở đây; **Lớp 2**
> (cần đóng position thật) chuyển sang [Phase 7 Bước B](phase-7-execution.md).

### Lớp 1 — tài khoản trống, không chạm tiền

- [x] History map `MapNotFound` cho tới `TradeLoggedOn && SymbolResolved && PositionsSynced`; sau đó
      `IsMapAvailable=true`, `Count=0`. **Live 2026-09-20 (tài khoản trống, cuối tuần):** 20:53:03.323
      `history map MapNotFound (trade_logged_on=False symbol_resolved=False positions_synced=False)` →
      20:53:05.168 `PositionsSynced=true … 728=2` → 20:53:05.327 `history map AVAILABLE count=0 version=0 connected=1`.
      Unit: `R2_HistoryMapNotFoundUntilSynced_ThenAvailableEmpty`.
- [x] Content-version history **độc lập** với trades: AP `728=2` định kỳ không làm history version đổi.
      **Live:** 6 cửa sổ `[STATS][TRADE]` liên tiếp 20:54→20:59 đều `version=0 … history_version=0 history_count=0`
      qua 6 lần reconciliation (`710` mới mỗi phút). Unit: `R3_HistoryVersionIndependentFromTradesAndReconciliation`.
- [x] Để yên 5 phút → `ApplyHistoryResult` không rebuild: `history_version` giữ 0 suốt 6 phút, `reads_available`
      1320/1320 mỗi phút → `ShouldApplyHistoryResult` so khớp Timestamp trả false (không rebuild).
- [x] Unit test với chuỗi message đóng hộp (2026-09-19, `TradeDesktop.Tests/CTrader/CTraderHistoryTests.cs`, 13 test xanh):
      fill có `721` khớp position → đúng 1 record (`OpenAndFullClose_…`, `SellClosedHigher_ProfitNegative`); fill không khớp →
      0 record, mở position mới (`FillForUnknownPosition_…`); đóng bớt → chưa ghi, đóng nốt → 1 record (`PartialClose_…`);
      fill trùng ExecID → không nhân đôi; ticket history = ticket trades (`R4_…`); mất qua AP không fill → 0 record + 1 WARN.
- [x] Đổi `platform_b` về `mt5` → tab History y hệt trước Phase 6. **Live 2026-09-21 09:00:39:** Save MT5 → QUOTE `unsubscribe` + logout, TRADE logout, `QUOTE stopped` 09:00:41.555,
      `TRADE stopped` 09:00:42.135, netwatch `fix=none` 09:00:41.8; ảnh chụp: tab Trade `Sàn B (Local\MT_B_Trades)`,
      tab History `Sàn B (Local\MT_B_History)` — y hệt trước Phase 6. Đổi lại cTrader 09:09:10 → sync + hai map AVAILABLE.

### Lớp 2 — cần đóng position thật → **Phase 7 Bước B**

- Đóng tay trên cTrader Web → record ở tab History app ≤ 500 ms; `Ticket` trùng ticket ở tab Trade.
- `OpenPrice`/`ClosePrice` khớp web; `closeExecutionMs` hợp lý (vài chục–vài trăm ms).
- Chuỗi mở-đóng **3 lần** (giảm từ 10 để tiết kiệm spread) → đủ record, không mất, không nhân bản.
- Mở 2 position, đóng thứ tự ngược → history đúng thứ tự.
- `RegisterCloseExecutionForNewHistoryTickets` chạy đúng một lần mỗi ticket.

### Fail-closed (R2 áp dụng y hệt Phase 5)

- [x] Kill socket TRADE → history map thành unavailable ngay. **Live 2026-09-21 08:54:55** (TCPView Close Connection
      cổng 5212, QUOTE giữ nguyên): `TRADE logged out` 08:54:55.569 → trades map MapNotFound 08:54:55.594 (**25 ms**) →
      **history map MapNotFound 08:54:55.609 (40 ms)**; logon lại 08:54:58.310 → `728=2` → trades AVAILABLE 08:54:58.544 →
      history AVAILABLE 08:54:58.600. Không có lần đọc nào available trước khi sync xong.
- [x] Restart app → không có cửa sổ nào `IsMapAvailable=true` mà chưa sync: lần mở app 20:53 (live) có đúng thứ tự
      MapNotFound → synced → AVAILABLE, không có lần đọc nào available trước `PositionsSynced=true`.

### R3

- [x] Để yên 5 phút → `ApplyHistoryResult` không rebuild liên tục (xem Lớp 1).
- [x] Content-version của history **độc lập** với trades — unit `R3_…`: mở position làm trades version đổi nhưng
      history version giữ nguyên; chỉ khi position đóng hẳn history version mới tăng. Live có position → Phase 7 B.

### Không hồi quy

- [x] Đổi `platform_b` về `mt5` → tab History hành xử y hệt trước Phase 6 (xem Lớp 1, live 2026-09-21 09:00).

### Gate chung

- [x] Test suite: **964 total / 953 pass / 11 fail**, 11 tên trùng baseline memo §2.2b (+13 test Phase 6
      `CTraderHistoryTests`). Build App `--no-incremental`: 0 error, 3 warning `CA1416` baseline.
- [ ] Soak — **CHƯA KIỂM**. Dùng **tiêu chí theo phiên S1–S6** (chủ dự án quyết 2026-09-21, xem
      [Phase 4 "Hoãn có điều kiện"](phase-4-quote-feed.md)): tổng ≥ 24 h tích luỹ, ≥ 2 đêm qua giờ nghỉ và
      mốc 00:00 UTC, ≥ 1 phiên ≥ 10 h. Đã có trên bản cũ: phiên 10,5 h (RAM 282 → 191 MB, handle 1773 → 1263,
      không rò rỉ). Gộp chung với P4-D1/P5-D1 trên bản build cuối `b8b0bbf`.

---

## Rollback

Revert dòng đăng ký decorator ở `Infrastructure/DependencyInjection.cs`.

---

## Cổng sang Phase 7

Phase 7 là phase **duy nhất** app được đặt lệnh cTrader. Cổng này chặt nhất:

- [ ] Toàn bộ checklist **Lớp 1** của Phase 4, 5, 6 đã pass và đã soak đủ trên tài khoản trống.
- [ ] Danh sách Lớp 2 của Phase 5 và 6 đã nằm trong checklist Phase 7 Bước B.
- [x] ~~Tài khoản live 8220816~~ → **Deriv 1551176** (FxPro chặn đặt lệnh qua FIX, P7-A1). Tài khoản Deriv có tiền, chân A là MT5 demo.
      XAUUSD mở. **Câu 1/4/5/6 (spike 721) chạy ở Phase 7 Bước A**, không phải điều kiện vào Phase 7.
- [ ] Đã chốt giá trị `volumeBUnits` thật sẽ dùng, và `contractSizeB` đã được Phase 0 xác minh.
- [ ] `max_total_opens` đã được đặt về **1** cho toàn bộ giai đoạn nghiệm thu Phase 7.
- [ ] `CurrentOpenPendingTimeMs >= 2000 ms` (≥ 4 × chu kỳ poll 500 ms) — xem [Phase 7](phase-7-execution.md).
