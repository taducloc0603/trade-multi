# Kiến trúc Trading Logs UI

Ngày cập nhật: 2026-08-12

## 1. Mục tiêu

Cửa sổ **Trading Logs** được chia 50:50:

- Bên trái: **Signal Logs**.
- Bên phải: **System / Execution Logs**.
- `Current Log`, `Log Folder` và `Close` nằm trên thanh công cụ của cửa sổ log.
- UI chính chỉ giữ nút `Open Log`, đặt sau `Reconnect`.

Mục tiêu kỹ thuật là cung cấp dữ liệu chẩn đoán cần thiết mà không thay đổi kết quả signal,
quota, state machine, cooldown, ownership hoặc thao tác MT4/MT5.

## 2. Luồng dữ liệu

```text
Trading / infrastructure code
          |
          +--> TradeSessionFileLogger --> bounded file queue --> session log file
          |              |
          |              +--> accepted realtime event --> bounded UI queue
          |                                              --> System / Execution panel
          |
          +--> structured SignalLogItem --> Signal panel
                                      \--> session log file

Legacy Signal text ------------------------> session log file only
```

File log là nguồn dữ liệu đầy đủ và có thẩm quyền. Hai collection UI chỉ phục vụ quan sát.
UI không tail file và không đọc lại toàn bộ file log.

## 3. Signal Logs

Collection Signal chỉ nhận `SignalLogItem` có cấu trúc. Các event chính:

- `SIGNAL_OPEN`
- `SIGNAL_HEDGE`
- `SIGNAL_CLOSE_TP`
- `SIGNAL_CLOSE_GAP`
- `SIGNAL_CLOSE_SOS`
- Outcome tương ứng: `CONFIRMED`, `BLOCKED`, `CANCELLED`, `FAILED`

Mỗi dòng gồm thời gian, event type, level, mô tả tiếng Việt và các trường truy vết như
`signalId`, `pairId`, `slot`, `side`, `reasonCode`, ticket hoặc trạng thái từng leg.

Signal text cũ không còn được thêm vào collection Signal và không phát realtime sang panel
System. Chúng được ghi file-only để giữ khả năng truy vết trong giai đoạn chuyển đổi.

Màu Signal được gom theo kết quả:

| Kết quả | Ý nghĩa | Màu |
|---|---|---|
| Detected | Đã phát hiện signal | Xanh dương |
| Confirmed | Hoàn tất thành công | Xanh lá |
| Blocked / Cancelled | Bị guard hoặc policy chặn | Cam |
| Failed | Lỗi thực thi hoặc timeout | Đỏ |

## 4. System / Execution Logs

Panel phải chỉ nhận dòng đã vượt qua `LOG_LEVEL` và đã được file logger chấp nhận. Log có
category bắt đầu bằng `SIGNAL_` bị loại khỏi panel này để tránh render trùng.

### 4.1 Phân nhóm UI

| Nhóm | Category tiêu biểu |
|---|---|
| Trading | `FLOW`, `CYCLE`, `SLOT`, `CLOSE_SELECT`, `TRADE_GATE`, `OPPOSITE_OPEN_GUARD` |
| Execution | `ROUTER`, `MT4`, `MT5`, `MANUAL`, `HWND_PROFILE` |
| Recovery | `RECOVERY`, `WATCHDOG`, `PERSIST` |
| Market | `MARKET` |
| Connection | `CONN`, `HWND` |
| Application | `VM`, `UI`, `LOGGER`, `GENERAL` và category chưa ánh xạ |

UI hỗ trợ lọc theo nhóm và level `All`, `Info`, `Warn`, `Error`. `Debug` không được đưa vào
collection UI mặc định. Màu System được tối giản: Info trung tính, Warn cam, Error đỏ.

### 4.2 Giới hạn tài nguyên

| Cơ chế | Giá trị |
|---|---:|
| Chu kỳ flush UI | 200 ms |
| Số dòng tối đa mỗi lần flush | 150 |
| Collection System tối đa | 2.000 dòng |
| Hàng đợi System UI tối đa | 5.000 dòng |
| Collection Signal tối đa | 500 dòng |
| Throttle-key cache tối đa | 4.096 key |

Khi UI queue đầy, dòng mới có thể bị bỏ khỏi UI nhưng file log vẫn giữ vai trò nguồn đầy đủ.
Điều này áp dụng cả với Warn/Error để bảo vệ bộ nhớ trước log storm.

### 4.3 Log được giảm lặp trên UI

Việc throttle sau đây chỉ tác động panel System, không xóa dòng khỏi file:

| Pattern | Khoảng cách hiển thị tối thiểu |
|---|---:|
| `TP_CHECK` | 10 giây |
| `TRADE_GATE/BLOCKED` | 10 giây |
| Market latency spike | 10 giây |
| `MIN_PROFIT/WAITING` | 30 giây |
| `COOLDOWN/BLOCK` | 30 giây |
| `CONN/SKIP` | 30 giây |
| `HWND/SKIP` | 30 giây |
| `OPPOSITE_OPEN_GUARD` | 30 giây |

Key throttle có thêm subject khi tìm được `pairId`, `slot`, `exchange` hoặc `map`, do đó log
của hai pair/slot/sàn khác nhau không vô tình chặn nhau.

## 5. Bất biến an toàn giao dịch

Các thay đổi log phải giữ các bất biến sau:

1. Không thêm hoặc bỏ path OPEN/CLOSE.
2. Không thay đổi công thức Gap, signal confirmation, guard hoặc close selection.
3. Không thay đổi quota/cooldown/ownership và không thay đổi thứ tự dispatch MT4/MT5.
4. `AllocatePendingOpenSlotWithReason` thực hiện cùng kiểm tra và cấp slot nguyên tử dưới
   `_allocateLock`; method cũ vẫn delegate tới cùng implementation. Giá trị bổ sung chỉ là lý do log.
5. `SignalId` vẫn là GUID duy nhất cho mỗi signal. Việc truyền cùng ID vào authorization chỉ liên kết
   detect/outcome và không thay đổi policy result.
6. Hedge chỉ là nhãn quan sát: nhận diện vị thế gần nhất còn tồn tại có side đối ứng. Nó không tạo
   guard, không cấp slot và không tự cho phép hoặc chặn lệnh.
7. Exception từ logger, subscriber realtime hoặc cập nhật Signal collection phải bị cô lập và không
   được thoát ra luồng signal/execution.
8. Throttle, filter, giới hạn queue và giới hạn collection chỉ áp dụng dữ liệu hiển thị UI.

## 6. Kiểm chứng

Đã thực hiện:

- Rà diff các điểm gọi signal, coordinator, router và logger.
- Build solution với Windows targeting: 0 error, 0 warning.
- Chạy toàn bộ test suite: 362/362 test pass.
- Kiểm tra whitespace bằng `git diff --check`.

Smoke test còn cần thực hiện trên Windows production-like:

1. Mở cửa sổ Trading Logs và xác nhận hai panel cuộn độc lập theo cả hai chiều.
2. Chạy một Open thường, một Hedge nếu có trạng thái phù hợp, và một Close.
3. Đối chiếu `signalId`, `pairId`, `slot`, ticket giữa panel Signal và file log.
4. Gây một guard-block và xác nhận không có lệnh vật lý được dispatch.
5. Tạo log lặp và xác nhận UI được throttle trong khi file vẫn chứa log gốc.
6. Xác nhận `Current Log`, `Log Folder`, bộ lọc và status counter hoạt động.

## 7. File liên quan

- `TradeDesktop.App/SignalLogWindow.xaml`
- `TradeDesktop.App/SignalLogWindow.xaml.cs`
- `TradeDesktop.App/ViewModels/DashboardViewModel.cs`
- `TradeDesktop.App/ViewModels/CappedObservableCollection.cs`
- `TradeDesktop.App/Services/TradeSessionFileLogger.cs`
- `TradeDesktop.Application/Models/SignalLogItem.cs`
- `TradeDesktop.Application/Models/SystemLogItem.cs`
- `TradeDesktop.Application/Services/SignalLifecycleLogFormatter.cs`
- `TradeDesktop.Tests/SignalLifecycleLogFormatterTests.cs`
- `TradeDesktop.Tests/SystemLogItemTests.cs`
