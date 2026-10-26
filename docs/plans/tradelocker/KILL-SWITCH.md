# TradeLocker — Kill switch (1 trang)

Dùng khi cần dừng ngay mọi lệnh sàn B TradeLocker. Thứ tự từ nhẹ tới mạnh. Không bước nào **đóng vị thế**: vị thế đang mở
phải tự đóng (bước 4).

| # | Cách | Hiệu lực | Ảnh hưởng |
|---|---|---|---|
| 1 | Tắt hai switch **Open Gap Buy / Open Gap Sell** trên dashboard | Ngay | Không mở cặp mới; Auto Close, recovery, manual close vẫn chạy |
| 2 | **Stop** | Ngay | Dừng logic; không mở/đóng gì nữa (vị thế còn mở vẫn nằm đó) |
| 3 | DB: `platform_b` ⇒ `mt5` (dòng `configs` của máy) rồi **Reconnect** | ~1 s | Phiên hermes tự đóng (`stopped (sàn B không còn là TradeLocker)`), map B về MMF MT5. **Chỉ làm khi đã flat**: slot đang mở mang ticket TradeLocker (bit 63) sẽ không tìm thấy trên MT5 |
| 4 | Đóng tay trên web `app.tradelocker.com`: tab **Positions → Full Close từng dòng** | — | KHÔNG dùng **Close All** khi chân A còn mở (sẽ lệch chân) |
| 5 | Code: `App.xaml.cs` thay `TradeLockerTradeExecutor` bằng `NullTradeLockerTradeExecutor` (1 dòng), build lại | Sau khi chạy bản mới | Mọi lệnh B trả `TradeLocker chưa được kích hoạt` ⇒ router đi partial-open/rollback; guard `PARTIAL_OPEN_ROLLBACK_STREAK` chặn Open sau 2 lần |
| 6 | Đăng xuất: Config → TradeLocker → **Đăng xuất** (xoá file DPAPI) và/hoặc Sign out trên web | Lần kết nối sau | Phiên mới không có refresh token ⇒ `AuthRequired`, B fail-closed |

## Dấu hiệu phải dùng kill switch

- Telegram `TRADELOCKER_ORDER_UNCERTAIN` (lệnh không rõ kết quả, AMBIGUOUS, hoặc RECONCILE không thấy position sau lệnh "không chắc").
- `TRADELOCKER_RECONNECT_STORM` lặp lại sau lần tự thử (5 phút).
- `OPEN_PARTIAL_ROLLBACK_STREAK` (đã tự chặn Open — kiểm nguyên nhân trước khi Stop/Start).
- `TRADELOCKER_PROTOCOL_DRIFT` liên quan `position-aggregates`, `tradable-unit-aggregates` hoặc quote (web đổi giao thức).
- `TRADELOCKER_ACCOUNT_NOT_DEMO` trên máy dev/laptop (CLAUDE.md §0.0 — dừng ngay, kiểm cấu hình account).

## Kiểm tra sau khi dừng

1. `Desktop\trade-log\{yyyyMMdd}-tradelocker.log`: mọi dòng `[ORDER] SEND` đều có `ACK`/`REJECT`/`TIMEOUT_UNCERTAIN`, và mọi lệnh mở có `FILLED`/`RECONCILE`/`AMBIGUOUS`.
2. Physical A / Physical B trên dashboard = 0 (hoặc đúng các cặp còn chủ ý giữ).
3. DB `current_slots` khớp vị thế còn mở.
