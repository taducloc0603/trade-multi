# Plan Gap Stability Diagnostics tạm thời

## Mục tiêu

- Giảm log Gap Stability spam nhưng tạm thời vẫn hiển thị trên cả UI và file.
- Lưu đủ dữ liệu Cycle để hiệu chỉnh 12 tham số Open/Normal Close trong DB.
- Không thay đổi thuật toán, trigger, guard, TP, SOS, portfolio hoặc execution.
- Cô lập diagnostics để có thể tắt/gỡ sau giai đoạn thử nghiệm.

## Nguyên tắc khóa

- Metadata/log không được tham gia quyết định giao dịch.
- Không thay đổi hành vi mặc định của `ISlotLogger` hoặc các log chức năng khác.
- Không log mỗi tick.
- Danh sách Gap giữ nguyên dấu và thứ tự.
- Mỗi bước phải pass test riêng, toàn bộ test và build trước khi chuyển bước.

## Bước 1 — Khóa hành vi trước thay đổi

**Trạng thái:** Hoàn thành

### Việc làm

- Khóa kết quả Open khi engine có/không có logger.
- Khóa kết quả Normal Close khi engine có/không có logger.
- Khóa TP và SOS không bị diagnostics tác động.
- Xác nhận test Stable/Unstable/Rejected/Delta/reset và multi-slot hiện tại.

### Expected

- Logger chỉ quan sát, không đổi trigger hoặc dữ liệu trigger.
- TP/SOS giữ nguyên legacy path.
- Có baseline test để phát hiện regression ở các bước sau.

### Kết quả

- File thay đổi: `TradeDesktop.Tests/GapDiagnosticsBehaviorBaselineTests.cs`.
- Test riêng: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true --filter FullyQualifiedName~GapDiagnosticsBehaviorBaselineTests -m:1`.
- Test nhóm baseline: Gap Cycle state/logging, Stable Open/Close, multi-slot và SOS 50/50 pass.
- Kết quả: 4/4 baseline logger/no-logger pass; toàn bộ 467/467 test pass; build `TradeDesktop.sln` thành công, 0 warning và 0 error.
- Ghi chú: Có hoặc không có `ISlotLogger` tạo cùng trigger Open, Normal Close, TP và SOS. Bước này chỉ thêm test và tài liệu, chưa sửa production code/log.

## Bước 2 — Cô lập và giảm spam

**Trạng thái:** Hoàn thành

- Đặt công tắc diagnostics nội bộ tại một nơi.
- Bỏ `CYCLE_STARTED` và reset ngắn khỏi log.
- Chỉ phát một `CYCLE_COMPLETED` cho Cycle có giá trị phân tích.
- Giữ Stable, Unstable, Rejected, Delta Split và reset đáng kể.

### Expected

- Giảm mạnh số dòng trên UI/file.
- Không thay đổi kết quả engine.
- Có thể tắt diagnostics tại một nơi.

### Kết quả

- File thay đổi: `TradeDesktop.Application/Services/GapCycleDiagnostics.cs`, `TradeDesktop.Application/Services/GapSignalConfirmationEngine.cs`, `TradeDesktop.Application/Services/CloseSignalEngine.cs`, `TradeDesktop.Tests/GapCycleLoggingTests.cs`.
- Bỏ hoàn toàn `CYCLE_STARTED`; không log `Joined`; reset chỉ log khi Cycle có ít nhất số mẫu cấu hình (tối thiểu 3) và duration từ 250 ms.
- Delta Split, Unstable, Rejected và reset đáng kể dùng một event `[GAP_STABILITY][CYCLE_COMPLETED]` với trường `result` phân biệt nguyên nhân.
- Stable vẫn chỉ log một lần lúc chuyển trạng thái; Trigger vẫn log một lần và không còn dòng reset trùng ngay sau trigger.
- Có công tắc `EnableGapStabilityDiagnostics` cô lập tại `GapCycleDiagnostics`; không thêm DB flag và không sửa logger chung.
- Test riêng logging/baseline 8/8 pass; toàn bộ 468/468 test pass; build solution thành công, 0 warning và 0 error.
- Lưu ý: analyzer hiện tại sẽ được cập nhật ở Bước 5 để đọc định dạng `CYCLE_COMPLETED`; chưa dùng analyzer cũ cho log mới.

## Bước 3 — Bổ sung dữ liệu Cycle và policy

**Trạng thái:** Hoàn thành

- Thêm `cycle_id`, thời gian bắt đầu/kết thúc và `gaps` đúng thứ tự.
- Thêm action, side, slot, symbol/config context khi có.
- Ghi snapshot policy Open/Close và Hold/Gap limits.
- Giới hạn 2.000 Gap mỗi dòng, có cờ truncate và tổng số mẫu.

### Expected

- Có thể tính lại công thức với bộ tham số DB khác.
- Không trộn Open/Close, Buy/Sell hoặc slot.
- Không tăng bộ nhớ theo từng tick chỉ vì diagnostics.

### Kết quả

- File thay đổi: `TradeDesktop.Application/Services/GapCycleState.cs`, `TradeDesktop.Application/Models/GapSignalModels.cs`, `TradeDesktop.Application/Services/GapCycleDiagnostics.cs`, `TradeDesktop.Application/Services/GapSignalConfirmationEngine.cs`, `TradeDesktop.Application/Services/CloseSignalEngine.cs`, `TradeDesktop.App/ViewModels/DashboardViewModel.cs`, `TradeDesktop.Tests/GapCycleStateTests.cs`, `TradeDesktop.Tests/GapCycleLoggingTests.cs`.
- Mỗi Cycle có `cycle_id` 32 ký tự hex; Cycle tách/restart có ID mới, khác ID Cycle vừa hoàn thành.
- Log chứa `started_at`, `ended_at`, `gaps` đúng dấu/thứ tự, tổng số mẫu, số mẫu đã ghi và cờ truncate.
- Tối đa 2.000 Gap cuối được serialize tại thời điểm phát log; không tạo danh sách diagnostics riêng trên mỗi tick.
- Policy snapshot gồm 6 tham số Stability, Hold Confirm, `limit_max_gap`, `max_gap`, `config_id` và cặp symbol A/B. Dashboard truyền context runtime hiện tại; caller/test cũ tiếp tục dùng giá trị mặc định mà không bị phá API.
- Engine giữ policy diagnostics gần nhất để explicit reset/reconnect vẫn ghi đúng snapshot nếu Cycle đủ điều kiện log.
- Test metadata/state/baseline 22/22 pass; toàn bộ 469/469 test pass; build solution thành công, 0 warning và 0 error.

## Bước 4 — Liên kết trigger và outcome

**Trạng thái:** Hoàn thành

- Gắn `cycle_id` với trigger.
- Gắn correlation với signal lifecycle nếu không yêu cầu thay đổi flow sâu.
- Ghi guard/outcome: Confirmed, Blocked, Failed, Cancelled.

### Expected

- Biết Cycle nào tạo tín hiệu và kết quả cuối.
- Không dùng correlation ID để điều khiển logic.

### Kết quả

- File thay đổi: `TradeDesktop.Application/Models/GapSignalModels.cs`, `TradeDesktop.Application/Services/GapCycleDiagnostics.cs`, `TradeDesktop.Application/Services/GapSignalConfirmationEngine.cs`, `TradeDesktop.Application/Services/CloseSignalEngine.cs`, `TradeDesktop.App/ViewModels/DashboardViewModel.cs`, `TradeDesktop.Tests/GapCycleLoggingTests.cs`.
- Stable Open/Normal Close tạo `signal_id` 32 ký tự hex tại thời điểm trigger; `GapSignalTriggerResult` chỉ vận chuyển `cycle_id`/`signal_id` như metadata.
- Dashboard tái sử dụng `signal_id` này cho `SignalLifecycleContext`, đồng thời thêm `cycleId` vào signal detected/outcome hiện có.
- Sau SignalEntryGuard, Gap trigger ghi `[GAP_STABILITY][GUARD]` với PASS/BLOCKED và reason code. Mọi outcome cuối qua `LogSignalOutcome` ghi thêm `[GAP_STABILITY][OUTCOME]` với Confirmed/Blocked/Failed/Cancelled, pair và slot.
- TP/SOS hoặc signal không có `DiagnosticCycleId` không phát thêm GUARD/OUTCOME Gap Stability.
- Hai Close slot có cycle/signal ID độc lập; test correlation/baseline/multi-slot 16/16 pass; toàn bộ 470/470 test pass; build solution thành công, 0 warning và 0 error.

## Bước 5 — Nâng cấp analyzer

**Trạng thái:** Hoàn thành

- Đọc `CYCLE_COMPLETED`, trigger/outcome, gaps và policy.
- Báo cáo riêng theo action, side, slot/symbol/config.
- Tính phân bố Gap, samples, duration, dispersion, drift và outcome.

### Expected

- Phân tích được nhiều file cùng policy.
- Có đủ dữ liệu đề xuất 12 tham số DB.

### Kết quả

- File thay đổi: `tools/analyze-gap-stability-logs.sh`, `tools/test-analyze-gap-stability-logs.sh`, `docs/GAP-STABILITY-TRIAL-RUNBOOK.md`.
- Analyzer đọc `CYCLE_STABLE`, `CYCLE_COMPLETED`, `TRIGGER_EMITTED`, `GUARD`, `OUTCOME`, ordered gaps và policy snapshot.
- Báo cáo tách theo `action | side | slot_id | config_id | symbol`; một `cycle_id` xuất hiện ở Stable/Trigger chỉ được tính một lần và dùng snapshot mới nhất.
- Báo cáo gồm Cycle result/reset reasons, average duration, trigger/guard/outcome, 8 dải Gap, 6 dải sample count, 5 dải duration, 5 dải Dispersion và 5 dải Drift.
- Gap âm được phân loại band theo độ lớn tuyệt đối; dữ liệu gốc trong file vẫn giữ dấu/thứ tự để phân tích tham số thay thế.
- Test fixture hai config/symbol, Stable/Unstable, Guard/Outcome và Gap bands pass; `bash -n` pass cho cả analyzer/test script.
- Toàn bộ 470/470 test .NET pass; build solution thành công, 0 warning và 0 error.

## Bước 6 — Regression và chạy thử ngắn

**Trạng thái:** Phần kỹ thuật hoàn thành — chờ chạy feed Windows 5–15 phút

- Chạy test logging, Open/Close, TP/SOS, guard, portfolio, recovery.
- Chạy toàn bộ test và build solution.
- Chạy thực tế 5–15 phút, kiểm tra UI, dung lượng file và analyzer.

### Expected

- Không regression hoặc tăng tải bất thường.
- Log ít dòng nhưng đủ dữ liệu phân tích.

### Kết quả kỹ thuật

- Nhóm Gap diagnostics/Open/Close/guard 134/134 pass, gồm Cycle dài 2.001 mẫu và truncate log 2.000 mẫu.
- Nhóm TP/SOS/trading flow/multi-slot/race/stress/recovery 92/92 pass.
- Toàn bộ 470/470 test pass; build `TradeDesktop.sln` thành công, 0 warning và 0 error.
- Analyzer fixture pass; kiểm tra 10.000 Cycle tổng hợp hoàn thành thành công trong khoảng 1,7 giây trên môi trường phát triển.
- `git diff --check` phải pass trước bàn giao build.

### Checkpoint vận hành còn lại

- Build/chạy trên máy Windows có feed thật trong 5–15 phút.
- Xác nhận UI không còn `CYCLE_STARTED`/reset ngắn spam.
- Xác nhận file có ordered gaps, policy, cycle/signal correlation và analyzer đọc được.
- Ghi kích thước file đầu/cuối, CPU/RAM quan sát và gửi file `trade-log` để đánh giá.

## Nhật ký

| Ngày | Bước | Quyết định/kết quả | Ảnh hưởng |
|---|---|---|---|
| 2026-08-25 | Tổng thể | Tạm giữ Gap diagnostics ở cả UI và file; không sửa logger chung | Ít rủi ro với log chức năng khác |
| 2026-08-25 | Bước 1 | Khóa logger là observer-only bằng 4 baseline test; toàn bộ 467 test pass | Cho phép bắt đầu giảm spam mà có kiểm soát regression |
| 2026-08-25 | Bước 2 | Bỏ Started/reset ngắn, gom kết thúc Cycle thành `CYCLE_COMPLETED`, bỏ reset trùng sau trigger | Giảm spam trong khi giữ logger/UI/file hiện tại và không đổi trading behavior |
| 2026-08-25 | Bước 3 | Thêm cycle ID, ordered gaps, timestamps, truncate guard và runtime policy snapshot | Đủ dữ liệu tính lại tham số DB mà không thu thập thêm dữ liệu trên mỗi tick |
| 2026-08-25 | Bước 4 | Truyền cycle/signal ID từ Stable trigger qua guard và signal outcome; hai Close slot dùng ID độc lập | Cho phép ghép Cycle với kết quả cuối mà ID không tham gia quyết định giao dịch |
| 2026-08-25 | Bước 5 | Nâng cấp analyzer theo cycle/signal correlation và policy grouping; thêm fixture tự kiểm tra | Có thể tổng hợp log mới và giữ raw gaps để đề xuất 12 tham số DB |
| 2026-08-25 | Bước 6 | Regression kỹ thuật hoàn thành; analyzer xử lý 10.000 Cycle tổng hợp khoảng 1,7 giây | Chờ checkpoint 5–15 phút trên Windows/feed thật trước khi chạy dài |
