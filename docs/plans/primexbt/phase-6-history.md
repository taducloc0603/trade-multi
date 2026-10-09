# Phase 6 — Lịch sử (live demo, chỉ đọc)

> History map `PRIMEXBT_B_History` cho tab History và cho các đường xác nhận đóng đang dùng history.

[← Phase 5](phase-5-open-positions.md) · [Index](README.md) · Phase sau: [Phase 7](phase-7-execution.md)

---

## Cổng vào (recheck)

| # | Kiểm tra |
|---|---|
| 6.R1 | Bước chung README §8 (1–6) |
| 6.R2 | Nghiệm thu Phase 1–5 chạy lại PASS (Layer 2 của Phase 5 làm lại: mở/đóng tay trên web) |

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | Lấy history lúc nào? | Poll `report/orders2` (orderBy id desc, limit 50) **khi một sub-position biến mất** + định kỳ 10 s; không spam mỗi 500 ms |
| 2 | Profit cột "$" | Tự tính `(closePrice − openPrice) × qty × dấu` (USD, đúng với XAU/USD) — giống ý `CTraderHistoryProjector` (`move × units`); `rpl` chỉ để đối chiếu (P11). Commission = `fee` |
| 3 | Lấy open price của lệnh đóng ở đâu? | Từ cache vị thế lúc còn mở (giữ bản ghi cuối của sub id); nếu thiếu (app khởi động sau khi đã đóng) ⇒ dùng `rpl` và đánh dấu "ước lượng" |

## Đã làm (2026-10-09)

- `PrimeXbtHistoryBook` (Infrastructure): dựa `PrimeXbtOrderReportParser` + `PrimeXbtHistoryProjector` (Phase 3); dedupe theo
  order id, sắp theo giờ đóng, FIFO 528, `Version` riêng; nhớ vị thế từng thấy (≤ 4096) để có giá/giờ mở; không biết giá mở
  ⇒ profit = `rpl`, giá mở suy ngược, log "ƯỚC LƯỢNG"; `Commission = −fee`.
- `PrimeXbtFwsSession`: `report/orders2` (REQUEST, body như web app) lúc đồng bộ vị thế, 0.5 s sau khi một sub-position
  biến mất, định kỳ 10 s; cách nhau ≥ 2 s; request chưa trả lời chờ tối đa 15 s (không gửi chồng). `ReadHistory`: cùng cổng
  với trades + đã nhận `report/orders2` của kết nối hiện tại. `[STATS]` thêm `history_requests=`; log `history +N […]` in
  từng lệnh (id, chiều, giá mở→đóng, profit, rpl).
- `PrimeXbtAwareHistoryReader` bọc ngoài `CTraderAwareHistoryReader`.
- **Tab History chỉ hiện ticket do app tạo** (`IsAppGeneratedTicket`, hành vi sẵn có cho mọi sàn) ⇒ vị thế mở bằng web/spike
  KHÔNG lên tab. 6-A1 kiểm ở mức map (log + `MMF_HISTORY Ticket set changed`); hiển thị trên tab kiểm ở 7B.

## Việc làm (plan gốc)

| File | Việc |
|---|---|
| `Infrastructure/PrimeXbt/PrimeXbtHistoryProjector.cs` | FIFO 528 bản ghi, `Version` riêng, ticket = encode(positionId) |
| `Infrastructure/PrimeXbt/PrimeXbtAwareHistoryReader.cs` | Decorator cho `PRIMEXBT_B_History` |
| `PrimeXbtTradeSession` | Lịch poll `report/orders2`, dedupe theo order id |

## Test mới

- `PrimeXbtHistoryProjectorTests`: dedupe, FIFO, profit tự tính khác `rpl:0`, thiếu open price ⇒ đánh dấu ước lượng.

## Nghiệm thu

| # | Tiêu chí | Kết quả |
|---|---|---|
| 6-A1 | User mở/đóng tay 2 vị thế trên web: tab History hiện đúng 2 dòng, giá, profit | ✅ ở mức map (tab chỉ hiện ticket app tạo — xem trên). Spike mở/đóng Buy 10689586 + Sell 10689594: `history +1` ~1 s sau mỗi lần đóng, `MMF_HISTORY Ticket set changed map=PRIMEXBT_B_History added=1`; Buy 4192.77→4192.61 profit −0.0016, Sell 4192.63→4192.36 profit +0.0027 (đúng (đóng−mở)×qty×dấu; `rpl` sàn = 0 do làm tròn). Lúc kết nối nạp 25 lệnh đóng cũ (ước lượng theo rpl) |
| 6-A2 | Không gửi `report/orders2` quá 1 lần / 5 s khi không có sự kiện | ✅ `[STATS] history_requests=6` ở 2 cửa sổ yên liên tiếp (15:42, 15:43) = 1 lần / 10 s; cửa sổ có 2 lần đóng = 7 |
| 6-A3 | Ma trận cũ smoke sạch; test fail = baseline; không warning mới | ✅ mt5/mt5: `Local\MT_B_History` True, reconcile PASS; recheck 1 302 / 11 baseline, warning 3 |

## Rollback

Gỡ decorator history; Trades map không bị ảnh hưởng.

## Cổng ra → Phase 7

6-A1…6-A3 ✅.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| 2026-10-09 | 116b9c9 | 1 285 / 11 | Recheck vào PASS (Phase 5 đóng cùng phiên) |
| 2026-10-09 | (chưa commit) | 1 302 / 11 | Recheck ra PASS. 6-A1…A3 ✅ ⇒ **Phase 6 đóng** |
