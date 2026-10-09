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
| 5-A1 | Start khi demo flat: Trades map B rỗng **sau** sync, `MapNotFound` **trước** sync (log thấy thứ tự) | |
| 5-A2 | Rút mạng: map về `MapNotFound`, không phải rỗng | |

## Nghiệm thu (Layer 2 — user mở tay trên web demo, app chỉ đọc)

| # | Tiêu chí | Kết quả |
|---|---|---|
| 5-B1 | User mở Buy 0.01: record xuất hiện trong ≤ 2.5 s với ticket decode ra đúng sub id | |
| 5-B2 | User mở thêm Sell 0.01 (hedge): **2** record, không phải 0 | |
| 5-B3 | User đóng Buy trên web: chỉ record Buy biến mất | |
| 5-B4 | Restart app khi còn vị thế: resync/recovery không đóng gì, cảnh báo đúng (switch Open/Close Auto **tắt**) | |
| 5-B5 | Ma trận cũ smoke sạch; test fail = baseline; không warning mới | |

Kết thúc: user tự đóng vị thế demo trên web.

## Rollback

Đổi `platform_b`; hoặc revert. Decorator nằm ngoài cùng, gỡ ra không ảnh hưởng cTrader/MMF.

## Cổng ra → Phase 6

5-A1, 5-A2, 5-B1…5-B5 ✅.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
