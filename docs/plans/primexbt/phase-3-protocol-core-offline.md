# Phase 3 — Lõi giao thức offline (thuần, test bằng fixture)

> Toàn bộ logic parse/build/khớp lệnh nằm ở `TradeDesktop.Application` (test project tham chiếu được), **không I/O**.
> Test chạy trên JSON thật đã quét ở [fixtures/](fixtures/).

[← Phase 2](phase-2-config-auth.md) · [Index](README.md) · Phase sau: [Phase 4](phase-4-quote-feed.md)

---

## Mục tiêu

Mọi quy tắc dễ sai (dòng gộp vs sub-position, khớp order→position, qty ounce, ticket namespace) được khoá bằng unit
test **trước** khi có kết nối thật.

## Cổng vào (recheck)

| # | Kiểm tra |
|---|---|
| 3.R1 | Bước chung README §8 (1–5) |
| 3.R2 | Nghiệm thu Phase 1 + Phase 2 chạy lại PASS (đặc biệt 2-A2: mở Config không reset) |
| 3.R3 | Fixture còn khớp live: chạy `probe` của Phase 0 — nếu có diff, cập nhật fixture + [00-scan-findings](00-scan-findings.md) **trước** |

## Việc làm (`TradeDesktop.Application/Services/PrimeXbt/`)

| Thành phần | Trách nhiệm | Quy tắc bắt buộc |
|---|---|---|
| `PrimeXbtEnvelope` | Build `{type,rid,action,body}`; parse RESPONSE/EVENT; tách `rid`/`sid`/`aid` | `rid` tăng đơn điệu theo kết nối; frame không parse được ⇒ trả lỗi, không throw |
| `PrimeXbtQuoteParser` | `fx/market` → `(bid, ask, symId)` | `symId` phải == symbolId đã resolve; thiếu `a`/`b` hoặc `a < b` ⇒ bỏ tick + đếm |
| `PrimeXbtSymbolResolver` | Từ `markets2` RESPONSE tìm `symbolId` theo **tên chính xác** (`XAU/USD`, không phải `XAU/USD.24`); digits = `priceScale` | Không khớp đúng 1 ⇒ fail-closed |
| `PrimeXbtPositionsParser` | EVENT/RESPONSE `positions` → danh sách **sub-position** phẳng `(id, side, qty, openPrice, openTime, upl, symbol)` | **Không bao giờ** dùng dòng gộp (`id:0`); `subPositions:null` ở dòng gộp ⇒ log lạ + fail-closed; lọc theo symbol cấu hình |
| `PrimeXbtTicketCodec` | `Encode(long subId)` / `TryDecode` với namespace **bit 61** | `CTraderTicketCodec.TryDecode` phải trả false cho ticket PrimeXBT và ngược lại |
| `PrimeXbtOrderPlanner` | `PlanOpen(cfg, isBuy)` → body `{qty, side, symbol}`; `PlanClose(subId, qty)` → `{positionId, qty}` | `qty` theo `orderStep` (decimal, không float), trong `[min,max]`; **không có** hàm build `positions/close` hay `close/all` |
| `PrimeXbtOrderMatcher` | Khớp order (ack id + `report/orders2`) với sub-position mới (D3) | Khớp 0 hoặc > 1 ⇒ `Ambiguous`, không đoán |
| `PrimeXbtHistoryParser` | `report/orders2` → bản ghi đóng (`openReason=CLOSE_POSITION`, có `positionId`) | Profit tự tính `(close−open)×qty×dấu` (P11), giữ `rpl` để đối chiếu |
| `PrimeXbtErrorMapper` | `body.error` → mã nội bộ + text | Dạng lấy từ A4 Phase 0; lạ ⇒ `Unknown` + giữ raw |

Copy fixture vào `TradeDesktop.Tests/PrimeXbt/Fixtures/` (Copy to output), **đã che danh tính**.

## Test mới (`TradeDesktop.Tests/PrimeXbt/`)

- `PrimeXbtPositionsParserTests`: hedge 2 sub dưới dòng gộp `qty 0` ⇒ **2** vị thế; flat ⇒ 0; symbol khác bị lọc;
  dòng gộp thiếu `subPositions` ⇒ lỗi.
- `PrimeXbtTicketCodecTests`: round-trip id 10680072; không va chạm MT ticket thường; cTrader ⇄ PrimeXBT từ chối lẫn nhau.
- `PrimeXbtOrderMatcherTests`: khớp đúng fixture Buy/Sell; hai ứng viên cùng `openTime` ⇒ Ambiguous; lệch giá ⇒ không khớp.
- `PrimeXbtOrderPlannerTests`: 0.01 oz OK; 0.015 bị từ chối; không có API close-all (reflection test).
- `PrimeXbtSymbolResolverTests`: chọn `XAU/USD` không chọn `.24`.
- `PrimeXbtHistoryParserTests`: lệnh mở bị bỏ qua; lệnh đóng ra profit tính tay, `rpl:0` không ghi đè.

## Rủi ro liên quan

P3, P5, P9, P10, P11.

## Nghiệm thu

| # | Tiêu chí | Kết quả |
|---|---|---|
| 3-A1 | Tất cả test PrimeXbt PASS; mỗi class public có happy + ≥ 2 edge | ✅ 157/157 (2026-10-09) |
| 3-A2 | Không có tham chiếu `System.Net` / WebSocket trong thư mục này | ✅ |
| 3-A3 | Test cũ: fail = baseline, cùng tên; không warning mới | ✅ 1215 / 11, warning 3 |

Ghi chú triển khai: tên file thực tế `PrimeXbtProtocol.cs` (envelope, lỗi, JSON helper), `PrimeXbtMarketParsers.cs`
(symbol, quote), `PrimeXbtPositions.cs` (positions, ticket codec), `PrimeXbtOrders.cs` (trade-settings, planner, report,
matcher, history). Comment trong code không được chứa nguyên văn route bị cấm (recheck A6 grep cả comment).

## Rollback

Revert; chưa có gì chạy runtime.

## Cổng ra → Phase 4

3-A1…3-A3 ✅.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
