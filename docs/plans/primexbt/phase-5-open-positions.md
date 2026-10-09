# Phase 5 — Vị thế mở (live demo, chỉ đọc)

> App đọc vị thế PrimeXBT thành Trades map `PRIMEXBT_B_Trades`. **App không đặt lệnh**; vị thế để thử do user mở
> tay trên web demo.

[← Phase 4](phase-4-quote-feed.md) · [Index](README.md) · Phase sau: [Phase 6](phase-6-history.md)

---

## Mục tiêu

Trades map của B phản ánh đúng **từng sub-position**, không bao giờ báo "có sẵn" khi chưa đồng bộ, và cho ticket ổn
định để slot/`current_slots` lưu được.

## Cổng vào (recheck)

| # | Kiểm tra |
|---|---|
| 5.R1 | Bước chung README §8 (1–6) |
| 5.R2 | Nghiệm thu Phase 1–4 chạy lại PASS (4-A3 rút mạng chạy lại thật) |
| 5.R3 | Tài khoản demo đang ở **HEDGE** (`metrics.positionMode`) và flat trước khi thử |

## Việc làm

| File | Việc |
|---|---|
| `Application/Abstractions/IPrimeXbtTradeSession.cs` | `EnsureState`, `ReadTrades(map, quoteLoggedOn)`, `ReadHistory(...)` (Phase 6), `SendMarketOrderAsync`/`ClosePositionAsync` (Phase 7, chưa cài), `TryGetOpenPosition(subId)` |
| `Infrastructure/PrimeXbt/PrimeXbtPositionCache.cs` | Thay toàn bộ theo snapshot `positions` (dùng `PrimeXbtPositionsParser`); `Version` = bộ đếm **thay đổi nội dung** (R3), không phải thời gian; `PositionsSynced` chỉ true sau RESPONSE `positions` đầu tiên của kết nối hiện tại; rớt kết nối ⇒ `PositionsSynced=false` |
| `Infrastructure/PrimeXbt/PrimeXbtTradeSession.cs` | Subscribe `positions`, `metrics`; `IsMapAvailable = LoggedOn ∧ SymbolResolved ∧ PositionsSynced ∧ positionMode==HEDGE` |
| `Infrastructure/PrimeXbt/PrimeXbtAwareTradesReader.cs` | Decorator: map `PRIMEXBT_B_Trades` ⇒ session; còn lại chuyển xuống reader bên trong (giữ nguyên chuỗi decorator cTrader) |
| `ToTradeRecords` | `Ticket = PrimeXbtTicketCodec.Encode(subId)`, `Lot = qty / ContractSizeB`, `OpenPrice`, `Type` theo side, `OpenEaTimeLocal` = stamp thật lần đầu thấy |
| `DashboardViewModel.BuildResyncedOpenSlots`, `LogHedgeVolumeConsistency` | Nhận ticket PrimeXBT (thêm nhánh, không đổi nhánh ctrader) |

### Đã làm khác plan (2026-10-09)

- **Không tách `PrimeXbtTradeSession` / socket thứ hai.** Snapshot `positions` đi chung socket `fws` với giá, nên
  `PrimeXbtFwsSession` cài thêm `IPrimeXbtTradeSession` (DI: một instance cho cả hai interface). Vòng đời, heartbeat,
  reconnect, ngắt mạch dùng chung; rớt socket ⇒ giá **và** Trades map B cùng fail-closed.
- `PrimeXbtPositionCache` coi **mọi** frame `positions` (RESPONSE lẫn EVENT) là snapshot đầy đủ ⇒ đồng bộ ở snapshot hợp lệ
  đầu tiên của kết nối. Snapshot hỏng ⇒ mất đồng bộ (giữ nội dung, không coi là "đóng hết") tới snapshot hợp lệ kế tiếp.
- `upl`/`markPrice` đổi **không** tăng `Version` (record không mang profit — `Profit = 0`, app tự tính từ giá).
- `BuildResyncedOpenSlots` không cần sửa: nhánh cTrader chỉ là log cảnh báo `TimeMsc=0`; PrimeXBT có `openTime` thật.
  `LogHedgeVolumeConsistency` thêm nhánh PrimeXBT (oz), nhánh cTrader giữ nguyên.

### Bất biến (copy từ R2/R3 cTrader)

- **P4**: chưa `PositionsSynced` ⇒ `MapNotFound`, **không** phải "map rỗng". Map rỗng giả = recovery đóng nhầm chân A.
- **P8**: `positionMode != HEDGE` ⇒ `MapNotFound` + log ERROR + Telegram.
- **P3**: chỉ sub-position; dòng gộp không bao giờ thành record.

## Test mới

- `PrimeXbtPositionCacheTests`: snapshot hedge ⇒ 2 record; snapshot y hệt ⇒ `Version` không đổi; đổi `upl` thôi ⇒ quyết định rõ có tăng Version hay không (ghi lý do); rớt kết nối ⇒ MapNotFound; NETTING ⇒ MapNotFound.
- `PrimeXbtAwareTradesReaderTests`: map khác vẫn đi xuống reader trong; B = ctrader không bị ảnh hưởng.

## Nghiệm thu (Layer 1 — tài khoản rỗng)

| # | Tiêu chí | Kết quả |
|---|---|---|
| 5-A1 | Start khi demo flat: Trades map B rỗng **sau** sync, `MapNotFound` **trước** sync (log thấy thứ tự) | ✅ log PrimeXBT: `connected` 15:11:40.8 → `positions synced v=0 count=0` 15:11:41.97; Start 15:12:21: `PRIMEXBT_B_Trades unknown -> True`, `START_RECONCILE state=BothFlat openB=0 result=PASS`, Position Sync "OK — Pairs 0 \| A 0 \| B 0", `[HEDGE_VOLUME] PrimeXBT ratio=1.00`. Thứ tự MapNotFound trước sync khoá bằng `ReadTrades_MapNotFound_UntilPositionsSnapshot_ThenEmptyMap` |
| 5-A2 | Rút mạng: map về `MapNotFound`, không phải rỗng | ✅ chủ dự án tắt Wi‑Fi (app Running, switch Open tắt): 15:22:07.127 heartbeat quá 16 s ⇒ 15:22:07.130 `PRIMEXBT_B_Trades True -> False` (MapNotFound, không phải rỗng); 15:23:13.6 nối lại ⇒ `positions synced` 15:23:14.69 ⇒ `False -> True` 15:23:14.74. 0 lệnh router/recovery, 0 ERROR |

## Nghiệm thu (Layer 2 — user mở tay trên web demo, app chỉ đọc)

| # | Tiêu chí | Kết quả |
|---|---|---|
| 5-B1 | User mở Buy 0.01: record xuất hiện trong ≤ 2.5 s với ticket decode ra đúng sub id | ✅ (vị thế mở bằng spike ngoài app — laptop demo; app chỉ đọc) Buy 10688718: app thấy 15:13:46.911 cùng lúc spike (~1.7 s sau lệnh); ticket `2305843009224382670` = bit 61 \| 10688718 |
| 5-B2 | User mở thêm Sell 0.01 (hedge): **2** record, không phải 0 | ✅ Sell 10688728 ⇒ `positions changed v=2 count=2`, Physical B "Buy 1 \| Sell 1 \| Total 2", Position Sync WARNING (B chưa ghép cặp — đúng) |
| 5-B3 | User đóng Buy trên web: chỉ record Buy biến mất | ✅ `removed=1 [2305843009224382670]`, còn `10688728:Sell` |
| 5-B4 | Restart app khi còn vị thế: resync/recovery không đóng gì, cảnh báo đúng (switch Open/Close Auto **tắt**) | ✅ `START_RECONCILE state=Unpairable openA=0 openB=1 result=PAUSE`, "auto-open paused for recovery", 0 lệnh router. Ghi nhận (hành vi sẵn có, mọi platform): `[WATCHDOG] Invariant cleared after 10 stable polls state=RESUMED` 5 s sau — Auto Open được mở lại dù B còn vị thế mồ côi |
| 5-B5 | Ma trận cũ smoke sạch; test fail = baseline; không warning mới | ✅ mt5/mt5 qua decorator mới: `Local\MT_B_Trades` True, reconcile PASS; recheck 1 278 / 11 baseline, warning 3 |

Kết thúc: đã đóng hết vị thế demo (spike `list` ⇒ count=0).

## Rollback

Đổi `platform_b`; hoặc revert. Decorator nằm ngoài cùng, gỡ ra không ảnh hưởng cTrader/MMF.

## Cổng ra → Phase 6

5-A1, 5-A2, 5-B1…5-B5 ✅.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| 2026-10-09 | df8e971 + Phase 4–5 (chưa commit) | 1 256 / 11 | Recheck vào PASS |
| 2026-10-09 | (chưa commit) | 1 278 / 11 | Recheck ra PASS (lần đầu FAIL: race trong test mới, đã sửa test, 8/8 lần PASS). 5-A1, 5-B1…B5 ✅; 5-A2 chờ tắt Wi‑Fi |
| 2026-10-09 | (chưa commit) | 1 285 / 11 | 5-A2 ✅ ⇒ **Phase 5 đóng** (kèm F4-3 guard, recheck PASS) |
