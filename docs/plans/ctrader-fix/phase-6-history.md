# Phase 6 — Luồng LỊCH SỬ, live, read-only

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
| 1 | `Profit` và `Commission` của history B là **số tổng hợp**, không phải số broker cấp (R10). Hiển thị thế nào để không bị hiểu nhầm? | FIX không trả P&L. Đề xuất: điền `0` cho `Commission`, tính `Profit` từ `(closePrice - openPrice)` theo điểm giá, và **ghi rõ trong README §8** rằng cột này với chân cTrader là số tính lại. Nếu tab History đang hiển thị như số broker thì cần một chú thích trên UI. |
| 2 | Position đóng **một phần** (partial close) thì sinh mấy record history? | Theo kết quả Phase 0 câu 5. Nếu cTrader cho partial close thì mỗi lần đóng một phần là một fill — cần quyết: một record mỗi fill, hay chỉ ghi khi position đóng hẳn. Đề xuất: **chỉ ghi khi position đóng hẳn** (`LeavesQty = 0`), vì app không dùng partial close. |
| 3 | Giữ history bao lâu trong bộ nhớ? | MMF history map có `HISTORY_MEMORY_SIZE 65536` / `HISTORY_RECORD_SIZE 124` ≈ 528 record. Đề xuất giữ tương đương, FIFO. |

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

- [ ] History map `MapNotFound` cho tới `TradeLoggedOn && SymbolResolved && PositionsSynced`; sau đó
      `IsMapAvailable=true`, `Count=0`.
- [ ] Content-version history **độc lập** với trades: AP `728=2` định kỳ không làm history version đổi.
- [ ] Để yên 5 phút → `ApplyHistoryResult` không rebuild.
- [ ] Unit test `CTraderHistoryProjector` với chuỗi message đóng hộp (fill có `721` khớp position trong
      cache → 1 record; fill không khớp → 0 record; partial fill `LeavesQty>0` → chưa ghi).
- [ ] Đổi `platform_b` về `mt5` → tab History y hệt trước Phase 6.

### Lớp 2 — cần đóng position thật → **Phase 7 Bước B**

- Đóng tay trên cTrader Web → record ở tab History app ≤ 500 ms; `Ticket` trùng ticket ở tab Trade.
- `OpenPrice`/`ClosePrice` khớp web; `closeExecutionMs` hợp lý (vài chục–vài trăm ms).
- Chuỗi mở-đóng **3 lần** (giảm từ 10 để tiết kiệm spread) → đủ record, không mất, không nhân bản.
- Mở 2 position, đóng thứ tự ngược → history đúng thứ tự.
- `RegisterCloseExecutionForNewHistoryTickets` chạy đúng một lần mỗi ticket.

### Fail-closed (R2 áp dụng y hệt Phase 5)

- [ ] Kill socket TRADE → history map thành unavailable ngay.
- [ ] Restart app → không có cửa sổ nào `IsMapAvailable=true` mà chưa sync.

### R3

- [ ] Để yên 5 phút → `ApplyHistoryResult` không rebuild liên tục.
- [ ] Content-version của history **độc lập** với trades — mở một position (trades đổi) không làm
      history rebuild.

### Không hồi quy

- [ ] Đổi `platform_b` về `mt5` → tab History hành xử y hệt trước Phase 6.

### Gate chung

- [ ] Test suite không tăng so với baseline Phase 0. Build sạch.
- [ ] Soak ít nhất **2 ngày**.

---

## Rollback

Revert dòng đăng ký decorator ở `Infrastructure/DependencyInjection.cs`.

---

## Cổng sang Phase 7

Phase 7 là phase **duy nhất** app được đặt lệnh cTrader. Cổng này chặt nhất:

- [ ] Toàn bộ checklist **Lớp 1** của Phase 4, 5, 6 đã pass và đã soak đủ trên tài khoản trống.
- [ ] Danh sách Lớp 2 của Phase 5 và 6 đã nằm trong checklist Phase 7 Bước B.
- [ ] Tài khoản live 8220816 đã nạp đủ (≥ $30–50) và chọn được ngày chạy trọn A → B → C trong giờ
      XAUUSD mở. **Câu 1/4/5/6 (spike 721) chạy ở Phase 7 Bước A**, không phải điều kiện vào Phase 7.
- [ ] Đã chốt giá trị `volumeBUnits` thật sẽ dùng, và `contractSizeB` đã được Phase 0 xác minh.
- [ ] `max_total_opens` đã được đặt về **1** cho toàn bộ giai đoạn nghiệm thu Phase 7.
- [ ] `CurrentOpenPendingTimeMs >= 2000 ms` (≥ 4 × chu kỳ poll 500 ms) — xem [Phase 7](phase-7-execution.md).
