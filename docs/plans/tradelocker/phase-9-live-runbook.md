# Phase 9 — Runbook lên live TradeLocker (chủ dự án TỰ làm)

> CLAUDE.md §0.0: AI **không** đăng nhập, đặt lệnh, đổi config hay deploy trên tài khoản/host thật. Tài liệu này để chủ dự
> án tự thực hiện. Khi có host TradeLocker live, thêm nó vào danh sách "tài khoản đã biết là THẬT" ở §0.0.

## 0. Điều kiện trước

- Phase 0–8 đã ✅ trên demo (xem MASTER-PLAN §1). Soak 24 h demo (8-A) đã chạy sạch.
- Broker live **chạy engine APOLLO trên `app.tradelocker.com`** (Phase 0 Q11). Nếu broker dùng TradeLocker bản cũ
  (`live.tradelocker.com` / Public API REST) thì client này KHÔNG dùng được — dừng.
- Đọc lại rủi ro T&C (phase-0-spike.md §Q12) và điều khoản riêng của broker về bot/arbitrage.
- Ngân sách lỗ chấp nhận được cho giai đoạn thử, lot nhỏ nhất (thường 0.01).

## 1. Chuẩn bị host

1. Build bản đã qua recheck (A1–A7 PASS). Ghi commit.
2. Trên host live: Config → Sàn B = **TradeLocker** → điền:
   - **Brand**: brand của broker (giá trị `X-ODA-Brand`; demo là `PRDTL`, live khác — lấy từ broker/web).
   - **Account ID**: accId số của tài khoản live (xem trong web, hoặc để trống nếu profile chỉ có 1 account — KHÔNG khuyến
     nghị trên live).
   - **Symbol / Volume B (lot) / Contract size / Volume A / Latency B** theo broker.
3. **Đăng nhập** (2 bước): bấm *Đăng nhập* ⇒ đăng nhập trong Chrome thường ⇒ đóng Chrome ⇒ bấm *Mở TradeLocker (Chrome)*
   ⇒ trạng thái "✔ Đã đăng nhập — refresh token hết hạn …".
4. Kiểm `Desktop\trade-log\{yyyyMMdd}-tradelocker.log`:
   - dòng `account … type=…` — **type phải là tài khoản THẬT bạn chủ định**; sẽ có WARN `KHÔNG phải demo` + Telegram
     `TRADELOCKER_ACCOUNT_NOT_DEMO` (đúng thiết kế, để nhắc).
   - dòng `symbol … lotSize=… step=… min=…` khớp broker; `PointDigitsConsistencyChecker` không báo lệch.

## 2. Chạy thử có kiểm soát

1. Hai switch Open **TẮT**, Start 30 phút: chỉ xem giá B, `[STATS]` (tick/phút, tuổi tick), không ERROR/DRIFT.
2. Bật **một** chiều, quota tổng 1, lot nhỏ nhất. Theo dõi cặp đầu tiên: popup lệch lot cặp đầu (nếu có) ⇒ kiểm.
3. Sau ≥ 10 cặp sạch mới tăng quota/lot từng bước.

## 3. Theo dõi & dừng khẩn cấp

- Telegram cần phản ứng ngay: `TRADELOCKER_ORDER_UNCERTAIN`, `TRADELOCKER_PROTOCOL_DRIFT`, `TRADELOCKER_RECONNECT_STORM`,
  `OPEN_PARTIAL_ROLLBACK_STREAK`, `TRADELOCKER_HEDGE_VOLUME_MISMATCH`.
- Dừng: [KILL-SWITCH.md](KILL-SWITCH.md).
- Refresh token sống ~30 ngày: đặt lịch đăng nhập lại trước hạn (trạng thái trong Config hiện ngày hết hạn).
