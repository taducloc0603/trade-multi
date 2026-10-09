# PrimeXBT — Kill switch (1 trang)

Dùng khi cần dừng ngay mọi lệnh sàn B PrimeXBT. Từ nhanh/nhẹ tới mạnh. Mọi bước **không đóng vị thế** — vị thế
đang mở phải tự đóng (bước 4).

| # | Cách | Hiệu lực | Ảnh hưởng |
|---|---|---|---|
| 1 | Tắt hai switch **Open Gap Buy / Open Gap Sell** trên dashboard | Ngay | Không mở cặp mới; Auto Close, recovery, manual close vẫn chạy |
| 2 | **Stop** | Ngay | Dừng logic; không mở/đóng gì nữa (vị thế còn mở vẫn nằm đó) |
| 3 | DB: `platform_b` ⇒ `mt5` (dòng `configs` của máy) rồi **Reconnect** | ~1 s | Phiên `fws` tự đóng (`stopped (sàn B không còn là PrimeXBT)`), map B về MMF MT5. **Chỉ làm khi đã flat** — slot đang mở mang ticket PrimeXBT sẽ không tìm thấy trên MT5 |
| 4 | Đóng tay trên web PrimeXBT: **Positions → đóng từng sub-position** | — | KHÔNG dùng "Close all" / đóng dòng gộp nếu chân A còn mở (sẽ thành lệch chân) |
| 5 | Code: `App.xaml.cs` thay `PrimeXbtTradeExecutor` bằng `NullPrimeXbtTradeExecutor` (1 dòng), build lại | Sau khi chạy bản mới | Mọi lệnh B trả `PrimeXBT chưa được kích hoạt` ⇒ router đi partial-open/rollback; F4-3 chặn Open sau 2 lần |
| 6 | Đăng xuất: Config → PrimeXBT → **Đăng xuất** (xoá file DPAPI) và/hoặc Sign out trên web | Lần kết nối sau | Phiên mới không có token ⇒ `AuthRequired`, B fail-closed |

## Dấu hiệu phải dùng kill switch

- Telegram `PRIMEXBT_ORDER_RECONCILED` với `UNKNOWN`, hoặc `FILLED` mà pair không được xác nhận trong `open_pending_time_ms`.
- `PRIMEXBT_RECONNECT_STORM` lặp lại sau 3 lần tự thử.
- `OPEN_PARTIAL_ROLLBACK_STREAK` (đã tự chặn Open — kiểm nguyên nhân trước khi Stop/Start).
- `PRIMEXBT_PROTOCOL_DRIFT` liên quan `fx/market`, `positions` hoặc `trade-settings`.
- `PRIMEXBT_NOT_HEDGE` (tài khoản không còn HEDGE).

## Kiểm tra sau khi dừng

1. `Desktop\trade-log\{yyyyMMdd}-primexbt.log`: dòng `[ORDER]` cuối cùng đều có `ACK`/`REJECT`/đối soát.
2. Physical A / Physical B trên dashboard = 0 (hoặc đúng các cặp còn chủ ý giữ).
3. DB `current_slots` khớp vị thế còn mở.
