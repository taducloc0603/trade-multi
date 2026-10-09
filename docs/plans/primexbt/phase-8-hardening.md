# Phase 8 — Hardening & vận hành

> Những thứ chỉ lộ ra khi chạy nhiều ngày: token hết hạn, PrimeXBT deploy bản web mới, cuối tuần, HMR.

[← Phase 7](phase-7-execution.md) · [Index](README.md)

---

## Cổng vào (recheck)

| # | Kiểm tra |
|---|---|
| 8.R1 | Bước chung README §8 (1–6) |
| 8.R2 | Nghiệm thu Phase 1–7B chạy lại PASS trên demo (7B-1, 7B-3, 7B-5 tối thiểu) |
| 8.R3 | Đọc log live 7C: không có `Unknown`/rollback nhầm |

## Việc làm

| # | Hạng mục | Chi tiết |
|---|---|---|
| 8.1 | Vòng đời token | Refresh tự động trước hạn; refresh fail ⇒ Telegram + chặn Open mới (Close vẫn được nếu socket còn) ; nhắc đăng nhập lại khi refresh token còn < 3 ngày |
| 8.2 | Drift giao thức | Chạy `probe` lúc Start; log `client-version` server/web; trường lạ/thiếu trong frame ⇒ `[PRIMEXBT][DRIFT]` WARN; trường bắt buộc thiếu ⇒ fail-closed |
| 8.3 | Cuối tuần / HMR | Hiển thị `nextOpenTime`; cảnh báo khi vào khung HMR (đòn bẩy 100, margin tăng) — **không** thêm logic chặn/đóng (Rule E) |
| 8.4 | Reconnect giữa lệnh | Kịch bản rớt mạng khi đang PendingOpen/PendingClose ⇒ reconciler + router xử lý; test lại 7A-4 trên demo |
| 8.5 | Kill switch | Tài liệu 1 trang: đổi DI về Null / đổi `platform_b` / Sign out web |
| 8.6 | Tài liệu | README §7.1 (config mới), §13 nếu liên quan; CLAUDE.md thêm pitfall: dòng gộp vs sub-position, qty ounce, không idempotent, tick không timestamp, latency B = tuổi tick |

## Đã làm trước 7C (2026-10-09) — lệch thứ tự có chủ đích

Làm cứng trước khi chạy live an toàn hơn; các mục cần thời gian thật (8-A1 soak 7 ngày, 8-A3 cuối tuần) để sau 7C.

| # | Kết quả |
|---|---|
| 8.1 | Refresh JWT thất bại ⇒ `OpenBlockReason` + Telegram `PRIMEXBT_TOKEN_REFRESH_FAILED` (≤ 1 lần/giờ). Router hỏi `ITradeLegOpenReadiness` trong `ValidateOpenPolicy` (trước VÀ sau mutex) ⇒ chặn cả cặp **trước dispatch** (`LEG_B_NOT_READY`), không sinh partial-open; Close không bị ảnh hưởng. Refresh OK sau đó / đăng nhập lại (kho có JWT mới) ⇒ tự nhả. JWT đã hết hạn ⇒ chặn. **Chưa làm:** nhắc khi `refresh_token` còn < 3 ngày (cookie ~30 ngày theo scan, app chưa lưu hạn cookie) |
| 8.2 | `[PRIMEXBT][WARN] [DRIFT]` + Telegram `PRIMEXBT_PROTOCOL_DRIFT`, mỗi loại 1 lần/đời session: action lạ, 20 frame `fx/market` liên tiếp của đúng symbol không đọc được (giá đã fail-closed qua tuổi tick), `trade-settings` thiếu min/step/max (planner fail-closed). `probe` lúc Start KHÔNG thêm vào app (đã chạy trong recheck A7 trước mỗi phase); client-version không có ở phía app |
| 8.3 | Subscribe `market/detail` (`symId`) ⇒ log `market open=… hmr=… nextOpen=… nextClose=…`; đổi trạng thái ⇒ Telegram `PRIMEXBT_MARKET` ("thị trường ĐÓNG — mở lại lúc …", "VÀO khung HMR: đòn bẩy còn 100"). `trade-settings` log giờ giao dịch + khung HMR một lần mỗi kết nối. Không chặn/đóng gì (Rule E) |
| 8.4 | Rớt socket giữa lệnh: 7A-4 (FILLED đúng) + test `SendOrder_SocketDropsAfterSend_*`, `SendOrder_CloseNoAck_*` |
| 8.5 | [KILL-SWITCH.md](KILL-SWITCH.md) |
| 8.6 | README §7.1 (khối `sans.primexbt`, khuyến nghị `close_pending_time_ms` ≥ 2500) + §8; CLAUDE.md mục "Sàn B PrimeXBT" + §9 |

Live (laptop demo): `market/detail` lần đầu dùng `symbolId` ⇒ server `WRONG_ARGS` ⇒ sửa thành `symId` (khớp fixture web) +
test so byte với `fixtures/ws-session.json`; sau đó `market open=true hmr=false nextOpen=2026-10-12 05:01`, 0 `[DRIFT]`.

## Nghiệm thu

| # | Tiêu chí | Kết quả |
|---|---|---|
| 8-A1 | Soak demo 7 ngày liên tục qua ít nhất 1 lần refresh JWT | ⏳ cần chạy dài (JWT hiện hết hạn 2026-10-16 07:03Z ⇒ refresh tự động từ 2026-10-15) |
| 8-A2 | Mô phỏng drift (sửa fixture/fake) ⇒ WARN/fail-closed đúng | ✅ `Drift_UnknownAction_WarnsOnce`, `Drift_QuoteShapeChanged_FailsClosedViaTickAge_*`, `Drift_TradeSettingsMissingLimits_*` |
| 8-A3 | Cuối tuần: không lệnh, không lỗi, thứ Hai tự chạy lại | ⏳ cuối tuần đầu tiên (thị trường đóng 2026-10-10 03:59 giờ máy, mở 2026-10-12 05:01) |
| 8-A4 | Ma trận cũ smoke sạch; test fail = baseline; không warning mới | ✅ mt5/mt5 mở/đóng 1 cặp thật sau khi router đổi; recheck 1 320 / 11 baseline, warning 3 |

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| 2026-10-09 | d3efba2 + 8.1–8.6 (chưa commit) | 1 320 / 11 | Recheck PASS. 8-A2, 8-A4 ✅; 8-A1, 8-A3 chờ thời gian thật |
