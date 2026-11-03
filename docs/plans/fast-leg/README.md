# Sàn C "Fast Leg" — kiến trúc, ngữ nghĩa, rủi ro

> Tài liệu nền cho [MASTER-PLAN.md](MASTER-PLAN.md). Mọi con số dòng (`:123`) là của commit baseline `30f38db`.

## 1. Bài toán

Tín hiệu A–B (`GapBuy = (B.Bid − A.Ask) × point`) chỉ nhìn hai sàn. Nó không phân biệt được:

| Tình huống | Bản chất | Nên làm |
|---|---|---|
| Một sàn chậm báo giá, thị trường đứng yên | Lệch "sạch", do báo giá | Vào lệnh |
| Giá thật đang chạy, sàn chậm sắp cập nhật | Lệch sẽ khép trước khi lệnh khớp ⇒ trượt 4–7 điểm | Không vào |

Bot khớp lệnh mất hàng trăm ms, nên không thắng được cuộc đua độ trễ. Vì vậy dùng quy tắc **quiet** (tránh xung lượng):
lấy một nguồn giá thứ ba nhanh (sàn C), đo giá giữa của C đi được bao xa trong 200 ms. Nếu |move| ≥ ngưỡng thì
coi như giá đang chạy ⇒ không vào lệnh.

F (sàn C) **không bao giờ đặt lệnh**. F chỉ chặn hoặc hoãn một signal mà engine đã tạo ra (CLAUDE.md Rule E).

## 2. Quyết định (D1–D9)

Xem bảng đầy đủ ở [MASTER-PLAN.md](MASTER-PLAN.md) mục Context. Tóm tắt:

| # | Nội dung |
|---|---|
| D1 | Sàn C là terminal MT5 thứ 3, EA `DataExporter` với `EA_CHANNEL_ID="C"`, đọc `Local\MT_C_Tick` |
| D2 | Chỉ quy tắc quiet |
| D3 | Bật filter ngay theo thông số Long; vẫn hỗ trợ observe |
| D4 | Gate nằm trong engine, trước khi xoá cycle: chặn ⇒ trả null, không reset |
| D5 | SOS Close bỏ qua F |
| D6 | Close chỉ hoãn khi TooFast; Down/Stale luôn cho đóng |
| D7 | Nhánh `feature/exchange-c-fast-leg` |
| D8 | Trần hoãn `maxDeferMs` (mặc định 3000) trong JSON `fast_leg` |
| D9 | Stale vẫn chặn Open; heartbeat `MT_C_Trades` tách Down khỏi Stale |

## 3. Luồng dữ liệu

```
Terminal MT5 #3 (EA "C") ─ Local\MT_C_Tick (64 B đầu) + Local\MT_C_Trades (heartbeat, 16 B đầu)
        │
FastLegMmfSampler (thread riêng, ~15.6 ms/nhịp thực tế, đọc chống torn-read)
        │ Push(sample) / SetHeartbeat(...)
FastLegTickBuffer (lock, trần 1024 mẫu)
        │
DashboardViewModel.OnSnapshotReceived (UI thread, 50 ms)
  └─ ngay sau Map: verdict = FastLegGate.EvaluateNow()   (một lần mỗi tick)
        ├─ gap-tick log (c_* trước point=)
        ├─ dải "Sàn C" + FastLegStats + FastLegHealthTracker
        └─ GapSignalSnapshot(..., FastLeg: verdict) → PortfolioCoordinator → engine Open / Close
TradeExecutionRouter.ValidateOpenPolicy → FastLegOpenPolicy.Check(...)   (chỉ Open)
```

Reader A/B (`SharedMemoryMarketDataReader`) **không bị sửa**. Lý do: `RefreshMapReaders` dispose mọi map khác map1/map2,
và vòng 50 ms đó là luồng dữ liệu chính của giao dịch.

## 4. Ngữ nghĩa phép đo

### 4.1 Layout MMF (EA `DataExporter/MQ5`)

`Local\MT_<ID>_Tick`, ghi mỗi `OnTick` (không ghi trong `OnTimer`), body trước header sau (`SharedMemoryBase.CommitWrite`):

| Offset | Kiểu | Trường |
|---|---|---|
| 0 | int32 | version (= 1) |
| 4 | uint64 | `GetTickCount64()` lúc EA ghi |
| 12 | — | padding |
| 16 | double | bid |
| 24 | double | ask |
| 32 | double | spread = ask − bid (EA tính, dùng làm checksum) |
| 40 | int64 | `time_msc` (giờ server) |
| 48..63 | ASCII | symbol (16 B) |

`Local\MT_<ID>_Trades` (heartbeat), ghi cả trong `OnTimer` (50 ms):

| Offset | Kiểu | Trường |
|---|---|---|
| 0 | int32 | số lệnh |
| 4 | uint64 | `GetTickCount64()` |
| 12 | int32 | `TERMINAL_CONNECTED` |

### 4.2 Đọc an toàn
- Đọc `ts` → body → `ts`; khác nhau ⇒ thử lại (tối đa 3), rồi bỏ mẫu.
- Bỏ mẫu nếu `spread ≠ ask − bid`, `time_msc` đi lùi, `version ≠ 1`, hoặc symbol khác cấu hình.
- Tick mới khi `(ts, time_msc, bid, ask)` đổi. Không dùng mỗi `ts`, vì `GetTickCount64` chỉ nhảy mỗi ~15.6 ms.

### 4.3 Phán quyết
- `mid = (bid + ask) / 2`; mẫu tham chiếu = mẫu mới nhất có `ts ≤ now − windowMs` (không có ⇒ mẫu cũ nhất).
- `move = (mid_last − mid_ref) × point`; `age = max(0, now − ts_last)`.
- Thứ tự: **Down** (tắt / không map / heartbeat cũ > 2 s hoặc mất kết nối / version, symbol sai / point ≤ 0) →
  **Stale** (chưa có tick hoặc `age > maxAgeMs`) → **TooFast** (`|move| ≥ maxMovePts`) → **Confirmed**.
- `BlocksOpen = filter ∧ Kind ≠ Confirmed ∧ ¬(failOpen ∧ Kind ∈ {Down, Stale})`.
- `BlocksClose = filter ∧ applyToExit ∧ Kind == TooFast`.

### 4.4 Đơn vị
1 điểm = 1 / `point` đơn vị giá. Với `point = 100` (XAU), 1 điểm = 0.01 USD — trùng đơn vị Long dùng
(`max_move = 5` ⇒ giá giữa của C đi ≥ 0.05 USD trong 200 ms).

## 5. Hành vi engine khi F chặn (Phase 4)

| Loại | F chặn khi | Hành vi |
|---|---|---|
| Open | `BlocksOpen` | Trả null, giữ cycle; tick sau hỏi lại; quá `maxDeferMs` ⇒ reset + `ABANDON` |
| Normal Close | `BlocksClose` và không phải SOS | Như Open, theo slot và chiều |
| TP | `BlocksClose` | Trả null, giữ cửa sổ TP; profit rớt dưới target ⇒ `ABANDON` |
| SOS Close | Không bao giờ | Đi qua như cũ |
| Manual per-pair / Recovery | Không bao giờ | Không đi qua engine |

Router chỉ kiểm lại cho Open, với mã `FAST_LEG_TOO_FAST` / `FAST_LEG_STALE` / `FAST_LEG_DOWN`. Router chặn ⇒ signal mất
(engine đã reset cycle), giống các khối `LATEST_*` khác.

## 6. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| Cycle bị giữ lâu ⇒ `_gaps` phình (lỗi treo UI cũ) | Trần `maxDeferMs`; soak kiểm không có `long_cycle` |
| C chết ⇒ không mở được lệnh | Đúng ý `fail_open=false`; `FAST_LEG_DOWN` sau 30 s (log + panel; Telegram chỉ khi có token); Close không bị ảnh hưởng (D6) |
| Thị trường quá yên ⇒ C không tick > 1 s ⇒ Stale chặn Open | D9: đo `stale_hb_alive_pct` rồi quyết lại |
| EA C cài nhầm kênh "A"/"B" ⇒ ghi đè giá chân đang giao dịch | Cảnh báo ở runbook; kiểm lúc Start (symbol + giá C trùng A/B ⇒ WARN — một tick trùng là bằng chứng yếu nên không ERROR, không chặn Start) |
| Close bị hoãn trong lúc giá chạy | Chỉ khi TooFast; trần `maxDeferMs`; SOS vẫn đóng |
| Rule D: SOS lợi nhuận thấp đóng trước Normal lợi nhuận cao lúc TooFast | Chấp nhận, ghi vào CLAUDE.md |
| Ngưỡng 5 đo trên cặp THINK–GTC, không phải cặp của mình | `[FAST_LEG][STATS]` + công cụ Phase 7 để chỉnh |
| Router chặn Open ⇒ signal mất | Hiếm (engine vừa xét cùng verdict); đếm bằng công cụ Phase 7 mục 5 |

## 7. Vận hành

- **Bật/tắt:** cửa sổ Config, mục **Sàn C**: tick **Bật sàn C (Fast Leg)** rồi **Save**; bỏ tick ⇒ ghi `enabled=false`.
  Nút **Check** đọc map tick (không đọc heartbeat) và tự điền Symbol nếu ô trống. Không sửa mục Sàn C ⇒ Save KHÔNG ghi cột `fast_leg`. Hoặc sửa cột trực tiếp
  ([FAST-LEG-MIGRATION.sql](../../FAST-LEG-MIGRATION.sql)) rồi Reconnect. Tắt khẩn cấp: [KILL-SWITCH.md](KILL-SWITCH.md).
- **Theo dõi:** `[FAST_LEG][STATS]` mỗi phút; `FAST_LEG_DOWN` / `FAST_LEG_RECOVERED` / `FAST_LEG_NOT_READY_AT_START` trong
  panel Signal + `trade-log`, và qua Telegram nếu `TelegramNotifier` có token (bản trong repo để trống ⇒ không gửi).
  Danh mục log: `LOGS.md` mục `[FAST_LEG]`.
- **Chọn ngưỡng:** chạy `mode=observe` vài phiên rồi
  `python -I docs/plans/fast-leg/tools/fast-leg-report.py <prefix phiên> [...]` (xem docstring đầu file cho định nghĩa
  markout / slippage). Ở `mode=filter` dữ liệu bị cắt cụt tại ngưỡng đang dùng — báo cáo có cảnh báo.
- **Số đo thực tế (2026-11-03, C = Exness `XAUUSD`, A/B = Connext-Demo):** C đi trước A khoảng 150–200 ms; feed C thưa
  (1.3–2 lần đổi giá/s, tuổi tick p50 ~360 ms); \|move\| p90 ~16 pt (Long: 4); TooFast 23.8 %, Stale 9.4 %. Cấu hình Long chặn
  Open ~33 % thời gian; max_age 1500 ⇒ ~26 %; max_move 6 + max_age 1500 ⇒ ~21 %. Chưa đo được edge thật vì A/B cùng broker.
- **Lên live:** [phase-8-live-runbook.md](phase-8-live-runbook.md) — chủ dự án tự làm.
