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
| 4-A1 | Dashboard hiện bid/ask B khớp web PrimeXBT (chênh ≤ 1 tick) | ✅ 151/154 mẫu UI (40 s, 250 ms/mẫu) khớp **đúng** bid/ask của một phiên `fws` độc lập (spike `quotes`) trong ±1.5 s; 3 mẫu còn lại là tick thoáng qua spike không lấy mẫu kịp |
| 4-A2 | Soak demo **24 h** liên tục: không treo UI, số reconnect, tick interval khớp memo Phase 0 | ✅ **rút gọn theo chủ dự án** (soak dài để lúc chạy thực tế re-check). Đoạn mạng ổn định 14:38–14:45 (7 cửa sổ `[STATS]`): 0 lần rớt sau khi nối, 149–170 tick/phút, tuổi tick p50 ~203 / p95 516–797 / max ≤ 2 000 ms, `would_skip_latency_b=0`, RTT heartbeat ≤ 1 015 ms, UI không treo. Sau lần tắt Wi‑Fi mạng laptop chập chờn (MT5 cũng không đóng được lệnh trong lúc đó) nên số soak sau 14:52 không dùng để đánh giá |
| 4-A3 | Rút mạng 60 s: `IsConnectedB=false` ngay khi heartbeat quá hạn; cắm lại tự phục hồi; không bão reconnect | ✅ Chủ dự án tắt Wi‑Fi ~14:51:38. 14:52:04.887 socket lỗi ⇒ `disconnected`, giá xoá ngay (trước mốc heartbeat 36 s; trong 26 s trước đó tuổi tick tăng tới 26 s nên guard latency B 2 000 ms đã chặn — `would_skip` 50 %). Backoff 0.5→1→2→4→8→10 s, `kết nối thất bại` không tính vào ngắt mạch ⇒ **0 NGẮT MẠCH**. Telegram `PRIMEXBT_DISCONNECTED` (> 30 s) + `PRIMEXBT_RECONNECTED`. 14:53:11 nối lại; phiên đầu sau khi có mạng bị im ⇒ **heartbeat quá 16 s ⇒ đóng socket** 14:53:47 ⇒ nối lại 14:53:49 (đường heartbeat cũng đã chạy thật) |
| 4-A4 | Gap/Signal hoạt động với B = primexbt (switch Open tắt) — log `[GUARD]` latency B hợp lý với ngưỡng mới | ✅ log `20261009_144124`: signal Open bắn liên tục, 169 lần `SIDE_DISABLED` (switch tắt); `[GAP_STABILITY][GUARD]` 190/190 PASS; `[STATS]` `confirm_latency_ms=2000`, tuổi tick p50 ~210 / p95 ~750 / max 1 719 ms ⇒ `would_skip_latency_b=0` |
| 4-A5 | Ma trận cũ smoke sạch; test fail = baseline; không warning mới | ✅ recheck: 1 257 test, 11 fail = baseline, warning 3 = baseline; đổi `platform_b=mt5` + Reconnect ⇒ B về `Local\MT_B_Tick` có giá, phiên PrimeXBT tự `stopped` |
| 4-A6 | Log phiên + log primexbt không chứa token/cookie | ✅ quét `{date}-primexbt.log`, 4 file phiên, `startup.log`: 0 chuỗi JWT/cookie/auth-guard |
| 4-A7 | (Laptop demo, **bật Auto**) Có giá PrimeXBT thật + executor vẫn là `NullPrimeXbtTradeExecutor` ⇒ signal Open bắn → chân A (MT5 demo) mở, chân B fail "PrimeXBT chưa được kích hoạt" → **rollback đóng chân A** (`CloseOpenedLegByTimeoutAsync`), slot không kẹt, không mở cặp mới liên tục (cooldown/quota vẫn đúng). Kiểm đường partial-open trước khi có executor thật | ✅ có phát hiện. 14:42:33 Open Buy: A `clicked` → ticket 77470314; B `PrimeXBT chưa được kích hoạt` → `PARTIAL_OPEN rollback=pending`. 14:43:05 (31.6 s, `open_pending_time_ms`) `OpenPartialRollback` đóng A → MMF xác nhận đóng 14:43:05.614; `[SLOT][ABORT]` slot bị gỡ. Lần 1 phát hiện F4-1 (barrier không nhả). **Sau khi sửa F4-1 (lần 2, log `20261009_145830`):** 14:58:31 A mở 77472153 → 14:59:02 rollback → MMF xác nhận đóng 14:59:03.0 → `[NON_AUTO_BARRIER][END]` 14:59:04.0 → Auto mở cặp mới ngay 14:59:04.4 (A 77472192) → rollback 14:59:35 (mạng chập chờn: retry-close bấm lại ~1.5 s/lần, đóng được 14:59:58.5) → `END` 14:59:59.5. Đường rollback + retry + nhả barrier đúng; **lộ ra F4-3** (vòng lặp mở A khi B fail liên tục) |

## Phát hiện

**F4-1 — Barrier non-auto không nhả sau rollback một chân (có từ trước, không riêng PrimeXBT). ĐÃ SỬA 2026-10-09 (chủ dự án duyệt):** chân không có ticket coi như đã xác nhận đóng — `PendingCloseConfirmation.AreAllLegsConfirmed` (Application, có `PendingCloseConfirmationTests`); cặp đủ hai ticket giữ nguyên hành vi. Kiểm live: `END` ~1 s sau khi MMF xác nhận chân A đóng.
`CloseOpenedLegByTimeoutAsync` gọi `BeginNonAutoCloseOperation(pairId)` rồi đăng ký pending-close **chỉ chân đã mở**
(`TicketB = null`). Trong `CollectPendingCloseRetryActions`: lần kiểm đầu đặt `CloseConfirmedA = true` nhưng
`CloseConfirmedB` vẫn `false` nên không vào nhánh `CloseConfirmedA && CloseConfirmedB` (nơi gọi
`TryBeginWaitAfterCloseFromPending` → `EndNonAutoCloseOperation`); lần kiểm sau rơi vào nhánh
`!needCheckA && !needCheckB` → `IsResolved = true` **không nhả barrier**. Hệ quả: sau mọi partial-open rollback,
Auto bị chặn (`HasNonAutoCloseInFlight`) tới khi Stop/Start. Vi phạm Rule B ("nhả ngay khi MMF xác nhận flat")
theo hướng **an toàn** (chặn thừa, không mở/đóng sai). Nhánh có từ commit `init`. Sửa là đổi logic recovery/barrier
⇒ cần chủ dự án duyệt (CLAUDE.md §0.1).

**F4-2 — `[MARKET][WARN] Latency spike exchange=B` dày khi B là nguồn tuổi-tick** (ngưỡng cố định 500 ms; tuổi tick
PrimeXBT thường vượt 500 ms giữa hai tick). Hành vi sẵn có, cTrader cũng vậy; panel realtime đã throttle 10 s. Chỉ
ghi nhận, không sửa.

**F4-3 — Khi chân B fail liên tục, Auto lặp "mở A → rollback" mỗi ~33 s (`open_pending_time_ms` + ~1 s). ĐÃ LÀM 2026-10-09 (chủ dự án chọn N = 2):** `PortfolioCoordinator.RecordPartialOpenRollback` đếm rollback liên tiếp (gọi trong `ExecutePendingOpenTimeoutActionsAsync`, chỉ Auto); đủ 2 ⇒ `CanOpenNewSlot` trả `PARTIAL_OPEN_ROLLBACK_STREAK` (cả hai chiều) + Telegram CRITICAL `OPEN_PARTIAL_ROLLBACK_STREAK` một lần; `MarkSlotOpenConfirmed` (cặp đủ hai chân) reset; `ClearAllSlots`/`Reset` (Start, Stop, Reconnect) reset. Chỉ chặn Open, không đụng Close/slot đang Live. Test `PartialOpenRollbackStreakTests`. Live (log `20261009_152639`): rollback 1 (15:27:12, `count=1/2`) → mở lại → rollback 2 (15:27:45, `count=2/2` ERROR) → `[SLOT][SKIP] Open … PARTIAL_OPEN_ROLLBACK_STREAK (2/2)`, đúng 2 lệnh Open trong phiên. Mô tả cũ: Trước F4-1 vòng này
bị barrier kẹt chặn **vô tình**; nay mỗi vòng tốn spread chân A + 1 Telegram CRITICAL `OPEN_PARTIAL_A_ONLY`. Áp dụng cho
**mọi** platform B (cTrader từ chối lệnh, PrimeXBT khi Null/lỗi). Không có guard "fail liên tiếp" nào trong code. Đề xuất (cần
chủ dự án quyết — là quy tắc mới): sau N (vd 2) partial-open rollback liên tiếp thì **chặn Auto Open** + Telegram, nhả khi
Stop/Start hoặc một cặp mở đủ hai chân; chỉ chặn, không tạo lệnh ⇒ không vi phạm Rule E.

**F4-4 — `server close 4003 No pongs` (14:49:08, 14:50:29), trước khi tắt Wi‑Fi.** Server đóng vì không nhận pong cho ping
WebSocket của nó. Spike Phase 0 (60 phút) không gặp. Chưa phân biệt được mạng hay app (pong do vòng nhận trả lời — thread pool
nghẽn sẽ làm pong trễ). Đã thêm `tp_threads`/`tp_pending` vào `[STATS]` để re-check lúc chạy thực tế; nếu 4003 lặp lại trên
mạng ổn định ⇒ cân nhắc chạy vòng nhận trên thread riêng. Ảnh hưởng hiện tại: mỗi lần rớt ~1.5 s, fail-closed đúng.

## Rollback

Đổi `platform_b` về platform cũ (session không mở socket); hoặc revert.

## Cổng ra → Phase 5

4-A1…4-A6 ✅.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| 2026-10-09 | df8e971 | — | Recheck vào PASS (A1–A7, probe OK) |
| 2026-10-09 | df8e971 + thay đổi Phase 4 (chưa commit) | 1 246 / 11 (baseline) | Recheck ra PASS (A1 WARN = thay đổi chưa commit). 4-A1/A4/A5/A6/A7 ✅; 4-A2 soak đang chạy; 4-A3 chờ chủ dự án |
| 2026-10-09 | df8e971 + Phase 4 + F4-1 (chưa commit) | 1 256 / 11 (baseline) | Recheck ra PASS. 4-A1…4-A7 ✅ (A2 rút gọn theo chủ dự án). **Phase 4 đóng.** Mở: F4-3 (chờ quyết), F4-4 (re-check lúc chạy thực tế) |
