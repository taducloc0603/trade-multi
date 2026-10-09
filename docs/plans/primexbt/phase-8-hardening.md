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

## Nghiệm thu

| # | Tiêu chí | Kết quả |
|---|---|---|
| 8-A1 | Soak demo 7 ngày liên tục qua ít nhất 1 lần refresh JWT | |
| 8-A2 | Mô phỏng drift (sửa fixture/fake) ⇒ WARN/fail-closed đúng | |
| 8-A3 | Cuối tuần: không lệnh, không lỗi, thứ Hai tự chạy lại | |
| 8-A4 | Ma trận cũ smoke sạch; test fail = baseline; không warning mới | |

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
