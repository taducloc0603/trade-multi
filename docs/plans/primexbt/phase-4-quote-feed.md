# Phase 4 — Kết nối + luồng giá (live demo, chỉ đọc)

> Lần đầu app mở socket thật. Chỉ subscribe giá/settings/metrics; **chưa** đọc vị thế, **không** đặt lệnh.
> Executor vẫn là Null.

[← Phase 3](phase-3-protocol-core-offline.md) · [Index](README.md) · Phase sau: [Phase 5](phase-5-open-positions.md)

---

## Mục tiêu

`SanB` của snapshot 50 ms lấy từ PrimeXBT khi `platform_b = primexbt`, fail-closed trung thực, tự phục hồi khi rớt mạng.

## Cổng vào (recheck)

| # | Kiểm tra |
|---|---|
| 4.R1 | Bước chung README §8 (1–6) — từ phase này **bắt buộc chạy `probe`** |
| 4.R2 | Nghiệm thu Phase 1–3 chạy lại PASS |
| 4.R3 | Token demo còn hạn ≥ 24 h (nếu không: đăng nhập lại qua Phase 2 trước) |

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | Latency B | **Tuổi tick** (`now − lần nhận tick gần nhất`, `Environment.TickCount64`), giống cTrader. So với `primexbt_confirm_latency_b` |
| 2 | Market đóng (`isMarketOpen=false`) | `IsConnected=true` nhưng tick cũ ⇒ latency tăng ⇒ guard tự chặn. Thêm cờ hiển thị "Market closed" — **không** tự tạo logic chặn mới |
| 3 | Session lifetime | DI singleton + `EnsureState(platformB, cfg)` (giống ctrader §4.6), không phải `IHostedService` |

## Việc làm

| File | Việc |
|---|---|
| `Application/Abstractions/IPrimeXbtQuoteSession.cs` | `EnsureState`, `ExchangeMetrics Read(exchangeA, point, confirmLatencyMs)`, `IsLoggedOn`, `StatusText`, `EventRaised`, `LogLine` |
| `Infrastructure/PrimeXbt/PrimeXbtWsTransport.cs` | `ClientWebSocket` tới `fws` (URL theo [ws-session.json](fixtures/ws-session.json)), vòng đọc nền, gửi tuần tự, heartbeat `time` 20 s / timeout 16 s, reconnect backoff 0.5→10 s, **storm breaker** (≥ 5 lần/phút ⇒ ngừng + cảnh báo) |
| `Infrastructure/PrimeXbt/PrimeXbtAuthSession.cs` | Nạp token, refresh trước hạn (≥ 24 h trước `exp`), dedupe refresh đồng thời; refresh fail ⇒ trạng thái `AuthExpired` (fail-closed) |
| `Infrastructure/PrimeXbt/PrimeXbtQuoteSession.cs` | Subscribe `markets2` → resolve symbolId; `trade-settings`; `market/detail`; `fx/market`; `metrics`. `Read()` không chặn, không throw |
| `SharedMemoryMarketDataReader.PollLoopAsync` | Thêm nhánh `primexbt` → `_primeXbtQuoteSession.Read(...)`; **không đụng nhánh ctrader/MMF** |
| `App/Services/PrimeXbtSessionMonitor.cs` | Ghi `Desktop/trade-log/{date}-primexbt.log` + Telegram cho sự kiện kết nối/auth/storm |
| `DependencyInjection.cs`, `App.xaml.cs` | Đăng ký singleton; resolve monitor sớm |

### `Read()` trả `IsConnected=false` khi (fail-closed, đủ cả)

chưa desired · socket chưa mở · heartbeat quá hạn · auth hết hạn · chưa resolve symbol · digits ≠ `CurrentPoint`
(`PointDigitsConsistencyChecker`) · chưa có tick nào · `metrics.blocked/closed == true`. Mất kết nối ⇒ **xoá giá cuối** (R9).

## Test mới

- `PrimeXbtQuoteSessionTests` với `FakePrimeXbtTransport`: từng điều kiện fail-closed; tick-age tăng giữa hai tick; xoá giá khi rớt.
- `PrimeXbtReconnectStormTests`: 5 reconnect/phút ⇒ ngừng; reset sau cửa sổ.
- `PrimeXbtAuthSessionTests`: refresh trước hạn; refresh đồng thời chỉ gọi 1 lần; lỗi ⇒ AuthExpired, không throw.
- Reader: B = mt5/ctrader không gọi session PrimeXBT (test hành vi cũ không đổi).

## Rủi ro liên quan

P1, P2, P7, P9, P10, R6, R9.

## Nghiệm thu

| # | Tiêu chí | Kết quả |
|---|---|---|
| 4-A1 | Dashboard hiện bid/ask B khớp web PrimeXBT (chênh ≤ 1 tick) | |
| 4-A2 | Soak demo **24 h** liên tục: không treo UI, số reconnect, tick interval khớp memo Phase 0 | |
| 4-A3 | Rút mạng 60 s: `IsConnectedB=false` ngay khi heartbeat quá hạn; cắm lại tự phục hồi; không bão reconnect | |
| 4-A4 | Gap/Signal hoạt động với B = primexbt (switch Open tắt) — log `[GUARD]` latency B hợp lý với ngưỡng mới | |
| 4-A5 | Ma trận cũ smoke sạch; test fail = baseline; không warning mới | |
| 4-A6 | Log phiên + log primexbt không chứa token/cookie | |
| 4-A7 | (Laptop demo, **bật Auto**) Có giá PrimeXBT thật + executor vẫn là `NullPrimeXbtTradeExecutor` ⇒ signal Open bắn → chân A (MT5 demo) mở, chân B fail "PrimeXBT chưa được kích hoạt" → **rollback đóng chân A** (`CloseOpenedLegByTimeoutAsync`), slot không kẹt, không mở cặp mới liên tục (cooldown/quota vẫn đúng). Kiểm đường partial-open trước khi có executor thật | |

## Rollback

Đổi `platform_b` về platform cũ (session không mở socket); hoặc revert.

## Cổng ra → Phase 5

4-A1…4-A6 ✅.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
