# System audit — 2026-08-07

## Phạm vi và nguyên tắc

- Nguồn sự thật: code trên branch `dev-4` tại thời điểm audit.
- Phạm vi: config DB/runtime, signal, portfolio, transition locks, execution router,
  persistence/recovery, watchdog, logging/UI và tests.
- Audit chỉ cập nhật tài liệu. Không thay đổi source code hoặc business rule.

## Kết luận nhanh

- Pipeline config mới đã được nối xuyên suốt từ Supabase đến runtime/coordinator/router.
- Multi-slot, quota DB, persistence `current_slots`, startup recovery, manual/recovery barrier,
  physical dispatch mutex, ticket-precise close và watchdog self-heal đều đã triển khai.
- Các lock kết hợp theo AND/deadline muộn nhất, không cộng duration.
- Có một finding code mức **High** ở Gap Close và 13 tests đang fail; chưa sửa trong audit này.
- Tài liệu cũ còn nhiều mô tả Phase 0–5/deferred/cap=1; `README.md` và `CLAUDE.md` đã được
  cập nhật theo trạng thái hiện tại.

## Finding cần thay đổi code (chưa thực hiện)

### AUD-001 — High — Gap Close chọn sai collection ở cuối confirm window

Luồng hiện tại trong `CloseSignalEngine`:

- Vị thế `GapBuy` phải đóng bằng `gapSell` âm. Lời gọi `ProcessSide` truyền
  `primaryGap: snapshot.GapSell`, nhưng truyền `side: GapSignalSide.Buy`.
- Vị thế `GapSell` phải đóng bằng `gapBuy` dương. Lời gọi truyền
  `primaryGap: snapshot.GapBuy`, nhưng truyền `side: GapSignalSide.Sell`.
- `GapSignalConfirmationEngine.ProcessSide` dùng `primaryGap` cho kiểm tra từng tick, nhưng khi
  đủ hold time lại chọn collection bằng `side`:
  `Buy => state.BuyGaps`, `Sell => state.SellGaps`.

Hậu quả:

- GapBuy Close có thể kiểm tra `BuyGaps` thay vì chuỗi `SellGaps` đã kích hoạt window.
- GapSell Close có thể kiểm tra `SellGaps` thay vì chuỗi `BuyGaps`.
- Với dữ liệu thực tế `gapBuy != gapSell`, close signal hợp lệ có thể không trigger.

Test evidence:

- Toàn suite: 292 tests, 279 pass, 13 fail.
- Failures tập trung ở `TradingFlowEngineTests` và `PortfolioCoordinatorAdapterTests`, nơi test
  dùng gapBuy/gapSell khác nhau đúng với flow thực tế.
- `CloseSignalEngineTests` đang pass vì các test Gap Close chính truyền `gapBuy == gapSell`, vô
  tình che khuất mismatch.
- Chạy riêng `ProcessSnapshot_RunsSequentialFlow_OpenBuyThenCloseBuy` vẫn fail, nên không phải
  lỗi do test chạy song song.

Đề xuất sửa sau khi được duyệt:

1. Tách `trigger/position side` khỏi `primary gap collection`, hoặc truyền rõ collection cần dùng
   vào `ProcessSide`.
2. Thêm regression tests với `gapBuy` và `gapSell` khác nhau cho cả GapBuy Close và GapSell Close.
3. Chạy lại toàn bộ 292 tests trên runtime .NET 8 chính thức và trên CI Windows.

### AUD-002 — Low — Platform analyzer warnings

Build App thành công nhưng có 3 cảnh báo `CA1416` ở các lệnh
`MemoryMappedFile.OpenExisting(..., MemoryMappedFileRights)`. Ứng dụng là WPF/Windows nên đây có thể
là cảnh báo expected, nhưng project Infrastructure chưa annotate/guard platform đủ để analyzer hiểu.
Không ảnh hưởng build; không sửa trong audit này.

### AUD-003 — Low — Một số source comments còn ngôn ngữ Phase 0/5

Một số comment trong `PortfolioState`, `PortfolioCoordinator`, DI và ViewModel vẫn nói cap=1,
Phase 5 deferred hoặc line number cũ. Runtime behavior không bị ảnh hưởng, nhưng comment có thể làm
người bảo trì hiểu sai. Audit không sửa vì đây là thay đổi trong file code; nên dọn riêng sau khi
AUD-001 được xử lý và tests xanh.

## Config pipeline đã xác minh

Luồng chuẩn:

`SupabaseConfigRepository` → `ConfigRecord` → `ConfigService.ConfigLoadResult` →
`RuntimeConfigState` → `DashboardViewModel.SyncPortfolioCoordinatorConfig()` →
`PortfolioCoordinator`/`TradeExecutionRouter`.

| Nhóm | DB/runtime | Normalize/fallback thực tế |
|---|---|---|
| Quota | `max_total_opens`, `max_buy_opens`, `max_sell_opens` | Cập nhật 2026-08-12: Total cố định; Buy/Sell là cận trên random từ 1; X random 2..5 Open confirmed; trạng thái được persist |
| Opposite time | `opposite_side_lock_seconds` | `<=0` cuối cùng fallback 300s ở coordinator |
| Opposite price | `opposite_open_min_distance_pts` | `0` tắt; thiếu MMF/price thì fail closed khi guard bật |
| Same action | `rd_start/end_same_action_lock_seconds` | Mỗi đầu không hợp lệ fallback `3/10`, range tự swap |
| Post-open | `rd_start/end_post_open_lock_seconds` | Cho phép `0..0`; random một lần/slot khi Open confirm |
| Post-close | `rd_start/end_post_close_lock_seconds` | Mỗi đầu `<=0` fallback 300; random một lần/Auto Close |
| SOS distance | `sos_trigger_a_open_distance_pts` | `>=0`; dùng `abs` biến động giá chân A theo point |
| SOS time | `sos_trigger_after_seconds` | `0` tắt |
| SOS gaps | `sos_close_confirm_gap_pts`, `sos_close_gap_pts` | Giữ dấu; một đầu bằng 0 thì fallback bộ Gap thường |
| Pending open/close | `open_pending_time_ms`, `close_pending_time_ms` | Runtime ban đầu 1000ms; DB load normalize `>=0` |
| Schedule | `schedule_sleeping` JSONB | malformed/missing/disabled => không block |

Lưu ý tên nội bộ: `PositionSlot.LastProfitA/B` là biến động giá theo point do
`CalculateTradeProfit` tính, không phải profit tiền. Vì vậy SOS dùng `Abs(LastProfitA)` đúng với
“khoảng cách tuyệt đối từ giá mở chân A”, nhưng tên property dễ gây hiểu nhầm.

## Ma trận action và lock

| Transition | Guard chính | Ghi chú |
|---|---|---|
| Open same-side → Open same-side | Same-action range | Tính từ Auto dispatch gần nhất |
| Open opposite-side | Opposite time + price guard | Phải pass cả hai |
| Open slot X → Close X | Holding + per-slot post-open | Deadline từ Open confirm của X |
| Close → Close | Same-action + post-open của target | Deadline muộn nhất thắng |
| Auto Close → Auto Open | Post-close dispatch + confirm anchor | Confirm anchor thường muộn hơn |
| Manual/Recovery Close | Non-auto barrier + physical mutex | Bypass Auto transition timers |

Post-open được check ở close scan và re-check tại router bằng cùng deadline; không cộng timer.
Partial-open rollback khoảng 2 giây với default open timeout 1 giây + recheck 1 giây là Recovery,
không chịu Auto post-open.

## Signal và close selection

- Open: `GapSignalConfirmationEngine`, toggle, qualifying count, quota/schedule/opposite checks,
  router policy revalidation và transition gate.
- Close: per-slot `CloseSignalEngine`, post-open/holding, SOS resolver, qualifying, priority selection,
  router revalidation và ticket/row refresh.
- TP dùng tổng biến động point A+B; SOS chỉ thay bộ Gap Close, không thay TP thresholds.
- Priority hiện tại: nếu có overtime slot, chọn **slot già nhất**, rồi dùng profit làm tie-break;
  nếu không có overtime, chọn profit cao nhất toàn bộ eligible.
- `max_life_time_by_second` chỉ tạo overtime tier, không tự sinh close signal.
- `min_profit_to_close` là gate của Auto Close: trước max lifetime cần tổng profit hai chân đạt ngưỡng; từ đúng mốc max lifetime trở đi gate được bỏ qua. Max lifetime bằng `0` làm gate không hết hạn. Manual/recovery không bị chặn.

## Ownership, recovery và persistence

- Lifecycle: `PendingOpen → Live → PendingClose → Closed`.
- Close owner: Auto, Manual hoặc Recovery; không được steal claim của owner khác.
- Manual/recovery tạo non-auto barrier, pause Auto đến khi MMF xác nhận xong.
- Router dùng physical mutex chung, revalidate policy/signal/MMF sau khi lấy mutex.
- Close lookup lại row theo ticket; không fallback row 0.
- `current_slots` đã load/save; persist tại Open confirm, Close finalize, manual resync và watchdog
  self-heal.
- Startup recovery verify ticket với MMF, discard snapshot stale và persist lại danh sách hợp lệ.
- Watchdog chỉ self-heal under-count trong quota và khi không có action in-flight; over-count thật pause.

## Logging và UI

- File session: `Desktop/trade-log`; coordinator `ISlotLogger` forward vào file logger.
- UI `SignalLogItems` là collection riêng, không tự nhận mọi dòng file log.
- Schedule sleeping chỉ log khi một Open signal bị block (`[SLOT][SKIP]` throttle 30s); không có
  event log riêng đúng thời điểm schedule bắt đầu/kết thúc.
- UI phase ưu tiên non-auto barrier, startup/global cooldown, post-close, opposite lock và quota.
- Critical recovery/watchdog paths có file log; partial-open rollback còn gửi Telegram nếu notifier bật.

## Validation

Ngày audit: 2026-08-07.

- `dotnet build TradeDesktop.App/TradeDesktop.App.csproj --no-restore -p:EnableWindowsTargeting=true`
  - Kết quả: success, 0 errors, 3 CA1416 warnings.
- `dotnet build TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore`
  - Kết quả: success, 0 warnings, 0 errors.
- `DOTNET_ROLL_FORWARD=Major dotnet test ... --no-build --no-restore`
  - Kết quả: 292 total, 279 passed, 13 failed.
  - Máy audit chỉ có .NET 10 runtime; tests target .NET 8, nên đã dùng roll-forward để chạy.

## Test coverage còn thiếu/yếu

- Thiếu direct regression test Gap Close với `gapBuy != gapSell` ở `CloseSignalEngineTests`.
- Chưa có test end-to-end schedule transition log vì code chưa có event transition.
- Router/App tests khó chạy cross-platform do WPF/MMF Windows; CI Windows vẫn là validation cuối.
- Persistence DTO không lưu selected post-open/post-close duration hoặc SOS mode; recovery chủ động
  random post-open lại. Đây là behavior hiện tại cần giữ trong tài liệu hoặc thay đổi bằng quyết định
  business riêng, không nên sửa ngầm.

## Thay đổi tài liệu trong audit

- Cập nhật quota fallback từ mô tả cap=1/7-4-4 cũ sang runtime `5/3/3` + DB dynamic.
- Cập nhật persistence/recovery từ “deferred” sang trạng thái đã triển khai.
- Thay “global action gate” bằng physical mutex + Auto transition gate.
- Ghi rõ phạm vi/kết hợp lock và hai anchor post-close.
- Ghi rõ Gap Close finding, validation và giới hạn test hiện tại.
