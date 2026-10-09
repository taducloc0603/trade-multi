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

## Việc làm

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
| 6-A1 | User mở/đóng tay 2 vị thế trên web: tab History hiện đúng 2 dòng, giá, profit | |
| 6-A2 | Không gửi `report/orders2` quá 1 lần / 5 s khi không có sự kiện | |
| 6-A3 | Ma trận cũ smoke sạch; test fail = baseline; không warning mới | |

## Rollback

Gỡ decorator history; Trades map không bị ảnh hưởng.

## Cổng ra → Phase 7

6-A1…6-A3 ✅.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
