# PrimeXBT 7C — Runbook chạy thật trên VPS

> Chỉ chạy khi chủ dự án đồng ý trong đúng lượt đó. Tài khoản Real `L…`, volume nhỏ nhất, có người theo dõi trực tiếp.
> Dừng khẩn: [KILL-SWITCH.md](KILL-SWITCH.md).

## 0. Trước khi lên VPS

| # | Việc | Ai |
|---|---|---|
| 0.1 | Code: nhánh `primexbt` bản mới nhất (gồm Chrome/CDP, popup lệch lot, retry chân B không cần HWND) — lấy từ GitHub Release do CI build (đã kèm `mt5engine_capi.dll`) | Chủ dự án |
| 0.2 | Thư mục chạy có `mt5engine_capi.dll` (Phase 1: bản Release laptop từng THIẾU file này ⇒ Save Config báo HWND "không tồn tại") — chép cạnh `TradeMulti.exe` nếu thiếu | Chủ dự án |
| 0.3 | VPS có .NET 8 Desktop Runtime + **WebView2 Runtime** (đăng nhập PrimeXBT) + **Chrome hoặc Edge** (cửa sổ PrimeXBT luôn mở, đồng bộ token qua CDP) | Chủ dự án |
| 0.4 | `.env` có Telegram (cảnh báo `PRIMEXBT_*`) | Chủ dự án |
| 0.5 | Tài khoản PrimeXBT Real ở chế độ **HEDGE** (không phải NETTING) | Chủ dự án (web) |
| 0.6 | Chạy vào ngày thị trường mở (XAU/USD: 22:01 CN – 20:59 T6 giờ sàn; tránh khung HMR) | — |

## 1. Cấu hình DB (dòng `configs` của VPS)

- `platform_b = primexbt`
- `sans.primexbt`: `accountId` = `L…`, `symbol` = `XAU/USD`, `volumeBOz` = `1` (= 0.01 lot), `contractSizeB` = `100`,
  `volumeALots` = lot chân A thật, `confirmLatencyB` = `2000`
- `close_pending_time_ms` **≥ 2500** (F7-1), `open_pending_time_ms` ≥ 3000 (đang 30000 — giữ)
- `max_total_opens = 1` cho tuần đầu
- Giữ nguyên ngưỡng gap/hold của production (KHÔNG dùng số test nhanh −100 của laptop)

## 1b. Chrome PrimeXBT (xử lý tay)

- Bấm **PrimeXBT** trên dashboard ⇒ Chrome profile riêng mở trang sign-in; đăng nhập một lần (mật khẩu/2FA). App tự
  đồng bộ token; log `[PRIMEXBT][INFO] Browser session synced`. Giữ cửa sổ này mở để xử lý tay khi cần.
- **Không bấm Logout trên web** — có thể huỷ `refresh_token` phía server ⇒ refresh lỗi ⇒ Open bị chặn.
- Đóng một sub trên web ⇒ app coi chân B bị đóng bên ngoài và **đóng nốt chân A** (hành vi recovery có sẵn).
- Mở lệnh tay trên web nằm ngoài quản lý của app — tránh.
- Cổng debug của Chrome này KHÔNG xác thực: mọi process và **mọi user Windows** trên VPS điều khiển được (đọc cookie, thao tác tài khoản), kể cả khi app đã thoát. VPS chỉ một user; không dùng thì đóng cửa sổ Chrome PrimeXBT.
- Đây là đường đăng nhập **chính**; WebView2 (Config → Đăng nhập, §2.1) chỉ là dự phòng khi không có Chrome/Edge.
- HWND chân B (cột ChartB/TradeB) có thể để trống khi B = primexbt — đóng/retry chân B đi theo ticket, không cần cửa sổ.

## 2. Khởi động (switch Open TẮT)

1. Đăng nhập theo §1b (Chrome); chỉ khi không có Chrome/Edge mới dùng Config → PrimeXBT → **Đăng nhập** (WebView2). Save → Reconnect.
2. `Desktop\trade-log\{yyyyMMdd}-primexbt.log` phải có, theo thứ tự:
   `connected` → `account demo=false positionMode=HEDGE blocked=false` → `symbol=XAU/USD symbolId=1019 digits=2` →
   `trade-settings min=… step=… max=…` → `positions synced` → `market open=true` → `history synced`.
   Có `demo=true` ⇒ sai tài khoản. Có `NETTING` / `[DRIFT]` / `4003` ⇒ DỪNG, báo lại.
3. Tắt hai switch **Open Gap Buy / Sell**, rồi **Start**. Trong log phiên: `[HEDGE_VOLUME][INFO] PrimeXBT: hedge ratio=1.00`,
   `START_RECONCILE … result=PASS`.
4. Để chạy 15–30 phút. Mỗi phút một dòng `[STATS]`: `reads_disconnected` ≈ 0, `would_skip_latency_b` thấp,
   `tp_pending=0`, `history_requests` ≈ 6. Dashboard: giá B nhảy, Position Sync "OK".

## 3. Bật Auto (có người theo dõi)

1. Bật switch Open. Cặp đầu: log `[HEDGE_VOLUME][INFO] first_pair_lot OK`; nếu lệch ⇒ app tự Stop (đúng thiết kế).
2. Mỗi cặp phải có: `[ORDER] ACK` (open) → `[SLOT][OPEN_CONFIRMED]` (~1.5–2.5 s) → … → `[ORDER] ACK` (close) →
   `[SLOT][CLOSE_CONFIRMED]`.
3. Đối chiếu web PrimeXBT: số sub-position = số cặp đang mở trên dashboard.

## 4. Dừng ngay khi (kill switch)

- Lệnh mồ côi (Physical A ≠ Physical B kéo dài > 1 phút), rollback ngoài ý muốn.
- Telegram `PRIMEXBT_ORDER_RECONCILED` có `UNKNOWN`, hoặc `PRIMEXBT_ORDER_UNCERTAIN` lặp lại.
- `OPEN_PARTIAL_ROLLBACK_STREAK`, `PRIMEXBT_RECONNECT_STORM`, `PRIMEXBT_NOT_HEDGE`, `PRIMEXBT_PROTOCOL_DRIFT`.
- `PRIMEXBT_TOKEN_REFRESH_FAILED` (Open đã tự bị chặn — đăng nhập lại trước khi chạy tiếp).

## 5. Theo dõi tuần đầu (cổng ra 7C, đồng thời 8-A1 / 8-A3)

- Mỗi ngày: grep log `[ERROR]`, `TIMEOUT_UNCERTAIN`, `POSITION_NOT_FOUND`, `4003`, `[DRIFT]`, `aborted by timeout`.
- Ngày JWT gần hết hạn (refresh tự động từ 24 h trước): thấy `refresh token OK`.
- Cuối tuần: `thị trường XAU/USD ĐÓNG`, không lệnh, không lỗi; thứ Hai tự có giá lại.
- Ghi kết quả vào [phase-7-execution.md](phase-7-execution.md) (bảng Bước C) và MASTER-PLAN §6.
