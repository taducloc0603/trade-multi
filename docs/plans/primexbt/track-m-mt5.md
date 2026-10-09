# Track M — Dùng MetaTrader 5 của PrimeXBT cho sàn B

> Chỉ làm khi cổng Phase 0 ra **GO-M**. Không có code mới: sàn B vẫn là `platform_b = mt5`, chỉ đổi broker.

[Index](README.md) · [Phase 0](phase-0-spike.md)

---

## Mục tiêu

Chạy cặp A (MT4/MT5 hiện tại) ↔ B (MT5 tài khoản PrimeXBT) bằng đúng đường MT5 đang chạy production.

## Cổng vào (recheck)

| # | Kiểm tra |
|---|---|
| M.R1 | Memo Phase 0 có kết luận **GO-M** và bảng A8 (so sánh MT5 vs PXTrader) |
| M.R2 | Baseline test + warning như Phase 0 (không có code đổi thì phải y hệt) |
| M.R3 | Cặp `mt5/mt5` với broker B cũ vẫn chạy Start 2 phút sạch log (để có đối chứng) |

## Việc làm

| Bước | Nội dung |
|---|---|
| M.1 | User tạo tài khoản MT5 **DEMO** tại PrimeXBT, cài terminal MT5 PrimeXBT riêng (thư mục riêng, không ghi đè terminal broker khác) |
| M.2 | Cài EA DataExporter theo quy trình hiện có (xem docs cài lại EA DataExporter); xác nhận map name B mới không trùng map A |
| M.3 | Ghi thông số symbol từ MT5: tên symbol (vd `XAUUSD`/`XAUUSD.`), digits, contract size, volume min/step, giờ giao dịch, stop level |
| M.4 | Cập nhật config Supabase cho máy test: `platform_b = mt5`, map name B, HWND B (chart + trade), volume B; kiểm tra `point` toàn cục khớp digits B (R6) |
| M.5 | Chạy app chỉ đọc 1 h (switch Open Buy/Sell tắt): kiểm tra Gap, latency B, `[HEDGE_VOLUME]` không WARN |
| M.6 | Bật giao dịch demo khối lượng nhỏ nhất: ≥ 5 cặp open/close Auto + 1 Manual per-pair close + 1 recovery (đóng tay chân B trong terminal → app đóng chân còn lại) |
| M.7 | Ghi kết quả vào bảng dưới; nếu đạt, lập kế hoạch chuyển live cùng chủ dự án (không tự làm) |

## Nghiệm thu

| # | Tiêu chí | Kết quả |
|---|---|---|
| M-A1 | EA xuất giá + trades + history của PrimeXBT MT5, app đọc được | |
| M-A2 | Đủ 3 kịch bản đóng (Auto / Manual per-pair / Recovery) hoạt động, log không có ERROR mới | |
| M-A3 | `[HEDGE_VOLUME]` không WARN; lot check cặp đầu (`HedgeLotMatchChecker`) không báo lệch | |
| M-A4 | Ma trận platform cũ không đổi (không có code đổi) | |

## Rollback

Trả config Supabase về broker B cũ. Không có thay đổi code.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
