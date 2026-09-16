# Plan: Nhập volume từ tool và thực thi lệnh One-Click Trading trên MT5

## 1. Mục tiêu

Cho phép người dùng nhập một giá trị volume tại tool. Khi điều kiện giao dịch `X` được thỏa mãn, tool phải gửi lệnh BUY/SELL tới đúng terminal MT5 và sử dụng chính xác volume đã nhận.

Luồng mục tiêu:

```text
Người dùng nhập volume
    -> Tool validate dữ liệu cơ bản
    -> Điều kiện X được thỏa mãn
    -> Tạo request bất biến gồm terminal + symbol + side + volume
    -> Chuẩn hóa/validate volume theo broker
    -> Ghi volume vào One-Click Trading của MT5
    -> Đọc lại và xác minh volume
    -> Click BUY hoặc SELL
    -> Tìm deal phát sinh trên MT5
    -> So sánh requested volume với executed volume
    -> Trả kết quả về tool
```

## 2. Phạm vi và giả định

- Phạm vi chính là lệnh mở qua giao diện One-Click Trading (OCT), không phải thay đổi volume của position đã mở.
- Mỗi request giao dịch phải chứa volume riêng; không dựa vào volume đang còn hiển thị từ lệnh trước.
- Nếu không ghi hoặc không xác minh được volume trên OCT, tuyệt đối không click BUY/SELL.
- Việc giao dịch và kiểm thử end-to-end phải thực hiện trên Windows có MT5, trước tiên bằng tài khoản demo.
- Nếu không bắt buộc lệnh phải đi qua OCT, cần cân nhắc dùng trực tiếp Python `MetaTrader5.order_send()`. Luồng này đã hỗ trợ volume và đơn giản, ổn định hơn thao tác giao diện.

## 3. Hiện trạng code cần re-check

### 3.1. Python đã truyền volume

- `ea-monitor/src/core/data_hub.py`: `send_oct_order()` nhận `volume` và đưa vào command queue.
- `ea-monitor/src/core/terminal_worker.py`: worker nhận volume và gửi request qua shared memory tới `OCTBridge`.
- Luồng `CMD_ORDER_SEND` dùng trực tiếp volume trong request `mt5.order_send()`; đây là phương án thay thế nếu không cần OCT.

### 3.2. OCTBridge nhận nhưng chưa áp dụng volume

- `ea-monitor/MQL5/OCTBridge.mq5` parse trường `volume`.
- `ProcessRequest()` truyền volume vào `ExecuteOCTClick(symbol, side, volume)`.
- `ExecuteOCTClick()` hiện chỉ tìm chart, tính tọa độ và click BUY/SELL.
- Tham số `volume` hiện chưa được ghi vào control volume của One-Click Trading.

### 3.3. Native DLL chưa có API set volume

- `native/mt5engine-capi/engine.cpp` hiện có logic thao tác cửa sổ MT4/MT5 và click.
- `native/mt5engine-capi/c_api.h` và `c_api.cpp` chưa expose API ghi/đọc volume OCT.
- `TradeDesktop.App/Native/NativeMethodsMt5.cs` chỉ expose validate HWND, context, close position và click BUY/SELL.

## 4. Quyết định kiến trúc cần xác nhận

### Phương án A — Python MetaTrader5 API

Sử dụng:

```python
mt5.order_send({
    "action": mt5.TRADE_ACTION_DEAL,
    "symbol": symbol,
    "volume": volume,
    "type": order_type,
    # ...
})
```

Ưu điểm:

- Volume là trường chính thức của request.
- Không phụ thuộc DPI, theme hoặc layout MT5.
- Dễ validate, test và xác nhận kết quả.

Nhược điểm:

- Lệnh không đi qua thao tác click OCT.
- Có thể không đáp ứng yêu cầu nghiệp vụ nếu broker hoặc hệ thống cần lệnh mang đặc tính manual/OCT.

### Phương án B — Ghi volume vào OCT rồi click

Ưu điểm:

- Giữ đúng luồng One-Click Trading hiện tại.
- Phù hợp khi lệnh cần được thực hiện qua giao diện MT5.

Nhược điểm:

- Phụ thuộc implementation của UI MT5.
- Control volume có thể là custom-drawn, không phải Win32 `Edit`.
- Cần khảo sát bằng Spy++, Inspect.exe hoặc Accessibility Insights.
- Cần xử lý DPI, focus, timeout và thay đổi giữa các build MT5.

Khuyến nghị: chọn phương án A nếu không có yêu cầu bắt buộc về OCT. Nếu bắt buộc OCT, tiếp tục các phase bên dưới.

## 5. Kế hoạch triển khai phương án OCT

### Phase 1 — Thêm volume vào giao diện tool

Thêm trường nhập volume:

```text
Volume: [ 0.05 ]
```

Yêu cầu:

- Chỉ nhận số hữu hạn lớn hơn `0`.
- Chuẩn hóa dấu thập phân về `.` khi tạo request.
- Không gửi lệnh ngay khi người dùng sửa volume.
- Hiển thị volume đang được áp dụng.
- Xác định volume được cấu hình chung, theo terminal, hay riêng cho leg A/B.
- Khi tạo request, chụp giá trị volume tại thời điểm điều kiện X đạt.

Ví dụ request:

```json
{
  "cmd": "oct_order",
  "req_id": "1724500000123",
  "terminal": "BrokerA",
  "symbol": "EURUSD",
  "side": "BUY",
  "volume": "0.05"
}
```

### Phase 2 — Validate theo symbol và broker

Đọc các thuộc tính:

- `SYMBOL_VOLUME_MIN`
- `SYMBOL_VOLUME_MAX`
- `SYMBOL_VOLUME_STEP`
- `SYMBOL_VOLUME_LIMIT`

Quy tắc khuyến nghị:

- Volume nhỏ hơn min: reject.
- Volume lớn hơn max: reject.
- Volume không đúng step: reject và trả các giá trị hợp lệ gần nhất.
- Không âm thầm làm tròn trong giao dịch thật.
- Format volume theo số chữ số cần thiết của `SYMBOL_VOLUME_STEP`.

Ví dụ:

```text
Requested volume: 0.057
Broker step:       0.01
Result:            reject
Suggested values:  0.05 hoặc 0.06
```

### Phase 3 — Khảo sát control volume của MT5

Trên đúng build MT5 dự kiến triển khai:

1. Mở chart và bật One-Click Trading.
2. Dùng Spy++, Inspect.exe hoặc Accessibility Insights.
3. Xác định hierarchy bên dưới chart HWND và panel `#32770`.
4. Kiểm tra ô volume có HWND riêng hay không.
5. Ghi lại:
   - Window class.
   - Automation ID/name nếu có.
   - Quan hệ parent/child.
   - Khả năng đọc giá trị.
   - Khả năng set bằng `WM_SETTEXT` hoặc UI Automation ValuePattern.
6. Thử trên các mức DPI 100%, 125% và 150%.
7. Thử trên tất cả build/broker MT5 nằm trong phạm vi hỗ trợ.

Không triển khai dựa trên giả định class là `Edit` trước khi khảo sát thực tế.

### Phase 4 — Thêm native API set-and-verify

Mở rộng `mt5engine_capi` với API dự kiến:

```cpp
MT_API int mt_set_oct_volume(
    uint64_t chartHwnd,
    const wchar_t* volumeText
);
```

API phải thực hiện trọn vẹn:

1. Validate input và HWND.
2. Tìm panel One-Click Trading.
3. Tìm control volume bằng Win32 hoặc Microsoft UI Automation.
4. Ghi giá trị volume.
5. Commit thay đổi nếu control yêu cầu Enter/event.
6. Đọc lại giá trị.
7. Chuẩn hóa text đọc lại và so sánh với giá trị yêu cầu.
8. Trả mã kết quả chi tiết.

Mã lỗi đề xuất:

```cpp
enum OctVolumeResult
{
    OCT_VOLUME_OK               = 1,
    OCT_VOLUME_INVALID_ARGUMENT = -1,
    OCT_CHART_NOT_FOUND         = -2,
    OCT_PANEL_NOT_FOUND         = -3,
    OCT_CONTROL_NOT_FOUND       = -4,
    OCT_SET_FAILED              = -5,
    OCT_COMMIT_FAILED           = -6,
    OCT_VERIFY_FAILED           = -7,
    OCT_TIMEOUT                 = -8
};
```

Yêu cầu an toàn:

- Dùng timeout cho các Win32 message có thể treo.
- Không giữ raw HWND lâu hơn phạm vi một request.
- Tìm lại control khi MT5/chart được restart.
- Log requested text, actual text và error code.
- Không log thông tin đăng nhập tài khoản.

### Phase 5 — Tích hợp native API vào OCTBridge

Sửa `ExecuteOCTClick()` theo thứ tự bắt buộc:

```cpp
bool ExecuteOCTClick(
    const string symbol,
    const string side,
    const double requestedVolume
)
{
    // 1. Tìm chart.
    // 2. Validate/format volume theo symbol.
    // 3. Bật OCT và ChartRedraw().
    // 4. Lấy chart HWND.
    // 5. Gọi mt_set_oct_volume().
    // 6. Nếu set/verify thất bại: return false, không click.
    // 7. Nếu thành công: click BUY/SELL.
    // 8. Trả kết quả để phía Python tiếp tục xác nhận deal.
}
```

Điều kiện bắt buộc:

```cpp
if(setVolumeResult != OCT_VOLUME_OK)
{
    // log lỗi
    return false;
}
```

### Phase 6 — Serialize thao tác theo terminal

Ghi volume và click phải là một critical section:

```text
LOCK terminal/chart
    set volume
    verify volume
    click BUY/SELL
UNLOCK terminal/chart
```

Không được để xảy ra:

```text
Request A set 0.02
Request B set 0.10
Request A click BUY  -> mở sai 0.10
```

Yêu cầu:

- Một terminal chỉ xử lý một `oct_order` tại một thời điểm.
- Request đến sau được xếp queue hoặc reject với trạng thái busy.
- Timeout phải giải phóng lock.
- Dùng `req_id` để chống xử lý trùng.
- `set_volume` và `click_order` không được expose như hai thao tác độc lập cho cùng workflow.

### Phase 7 — Xác nhận deal sau khi click

`PostMessageW()` thành công không có nghĩa broker đã nhận hoặc khớp lệnh.

Sau click:

1. Lưu snapshot position/deal trước khi click.
2. Poll MT5 trong thời gian hữu hạn, ví dụ 1–2 giây.
3. Tìm deal mới theo terminal, symbol, side và khoảng thời gian.
4. Đọc `deal.volume` thực tế.
5. So sánh với requested volume theo tolerance phù hợp với volume step.
6. Trả requested volume và executed volume về tool.

Ví dụ thành công:

```json
{
  "success": true,
  "req_id": "1724500000123",
  "ticket": 12345678,
  "symbol": "EURUSD",
  "side": "BUY",
  "requested_volume": 0.05,
  "executed_volume": 0.05,
  "price": 1.16452
}
```

Nếu không tìm thấy deal hoặc volume không khớp:

- Đánh dấu request thất bại hoặc trạng thái uncertain theo policy.
- Pause auto-trading trên terminal liên quan.
- Hiển thị cảnh báo rõ ràng.
- Không tự động gửi lệnh bù nếu chưa có policy riêng được duyệt.

### Phase 8 — Trạng thái và logging

Tool nên hiển thị tuần tự:

```text
Configured volume: 0.05
Condition X triggered
Sending BUY EURUSD 0.05
Writing OCT volume
OCT volume verified: 0.05
BUY click dispatched
MT5 confirmed ticket #12345678, 0.05 lot
```

Log tối thiểu:

- `req_id`
- terminal
- symbol
- side
- requested volume
- normalized/formatted volume
- volume đọc lại từ OCT
- native result code
- ticket/deal
- executed volume
- elapsed time

## 6. Xử lý lỗi và policy đề xuất

| Lỗi | Hành vi |
|---|---|
| Input không phải số hoặc `<= 0` | Chặn tại UI |
| Sai min/max/step | Reject trước khi thao tác MT5 |
| Không tìm thấy chart | Không click, trả lỗi |
| Không tìm thấy OCT panel | Không click, trả lỗi |
| Không tìm thấy volume control | Không click, trả lỗi |
| Ghi volume thất bại | Không click, trả lỗi |
| Đọc lại volume không khớp | Không click, pause terminal nếu lặp lại |
| Click đã gửi nhưng không tìm thấy deal | Trả trạng thái uncertain và pause |
| Deal có volume khác request | Cảnh báo nghiêm trọng và pause |
| Terminal busy | Queue hoặc reject theo policy cấu hình |

## 7. Kế hoạch kiểm thử

### 7.1. Unit test

- Parse `0.01`, `0.1`, `1`, `1.00`.
- Reject rỗng, text, NaN, Infinity, `0`, số âm.
- Validate min/max/step.
- Format volume theo step `1`, `0.1`, `0.01`, `0.001`.
- So sánh requested/executed volume bằng tolerance theo step.
- Duplicate `req_id` không tạo thêm lệnh.

### 7.2. Native integration test

- Tìm được OCT panel.
- Tìm được volume control.
- Set và đọc lại `0.01`, `0.10`, `1.00`.
- Không click trong test set-only.
- Timeout khi MT5 bị treo không làm treo tool.
- HWND cũ sau khi restart MT5 được phát hiện và không sử dụng.

### 7.3. End-to-end trên tài khoản demo

- BUY và SELL với volume hợp lệ.
- Volume nhỏ nhất và lớn nhất được phép.
- Volume sai step bị chặn.
- Hai request liên tiếp trên cùng terminal không dùng nhầm volume.
- Hai terminal đồng thời với volume khác nhau.
- Người dùng đổi volume trong lúc request đang chạy.
- MT5 bị đóng giữa bước set và click.
- Mất kết nối broker sau khi click.
- DPI 100%, 125%, 150%.
- Theme/layout và các build MT5 thuộc phạm vi hỗ trợ.

## 8. Tiêu chí nghiệm thu

- Volume nhập ở tool được đưa vào từng request giao dịch.
- Volume được validate theo đúng terminal và symbol.
- Tool không click nếu không set-and-verify được OCT volume.
- Ghi volume và click không thể bị xen kẽ bởi request khác trên cùng terminal.
- Deal MT5 được xác nhận với đúng requested volume.
- UI hiển thị cả requested volume và executed volume.
- Tất cả lỗi quan trọng có error code và log đủ để điều tra.
- End-to-end test thành công trên tài khoản demo và các mức DPI hỗ trợ.
- Có kill switch/pause khi kết quả giao dịch ở trạng thái uncertain hoặc volume mismatch.

## 9. Thứ tự thực hiện đề xuất

1. Xác nhận có bắt buộc OCT hay có thể dùng `MetaTrader5.order_send()`.
2. Khảo sát control volume trên đúng build MT5.
3. Chốt phương thức Win32 control hay UI Automation.
4. Viết native `mt_set_oct_volume()` theo kiểu set-and-verify.
5. Viết test set-only, chưa cho phép click.
6. Tích hợp API vào `OCTBridge.mq5`.
7. Thêm lock/queue theo terminal.
8. Thêm xác nhận deal và volume sau click.
9. Thêm trường volume và trạng thái vào UI tool.
10. Chạy test end-to-end trên demo.
11. Review safety trước khi bật trên tài khoản thật.

## 10. Checklist dành cho model re-check

Model review cần trả lời từng câu sau bằng `Đạt`, `Không đạt` hoặc `Cần xác minh` kèm dẫn chứng file/dòng:

- [ ] Volume có được chụp vào request tại thời điểm điều kiện X đạt không?
- [ ] Request có chứa terminal, symbol, side, volume và unique `req_id` không?
- [ ] Volume có được validate theo `SYMBOL_VOLUME_MIN/MAX/STEP/LIMIT` không?
- [ ] Có tránh âm thầm làm tròn volume không?
- [ ] Control OCT đã được khảo sát trên MT5 thực tế chưa?
- [ ] Có native API set-and-verify, thay vì chỉ click theo tọa độ không?
- [ ] Nếu verify volume thất bại, code có chắc chắn không click không?
- [ ] Set volume và click có nằm trong cùng critical section không?
- [ ] Có chống request trùng bằng `req_id` không?
- [ ] Có timeout cho Win32/UI Automation calls không?
- [ ] Có xử lý MT5/chart restart và HWND stale không?
- [ ] Sau click có kiểm tra deal mới thay vì chỉ kiểm tra `PostMessageW()` không?
- [ ] Có so sánh requested volume với executed deal volume không?
- [ ] Volume mismatch có làm pause auto-trading không?
- [ ] Có test nhiều terminal và nhiều mức DPI không?
- [ ] Có test trên tài khoản demo trước tài khoản thật không?
- [ ] Logs có đủ `req_id`, terminal, symbol, side và các giá trị volume không?

## 11. Câu hỏi mở cần chốt trước khi code

1. Lệnh có bắt buộc được thực hiện qua One-Click Trading không?
2. Volume dùng chung hay riêng cho terminal A/B?
3. Khi volume sai step: reject hay cho phép người dùng chọn làm tròn?
4. Khi terminal đang bận: queue request hay reject?
5. Timeout xác nhận deal là bao lâu?
6. Khi không tìm thấy deal sau click: pause toàn hệ thống hay chỉ terminal liên quan?
7. Khi executed volume khác requested volume: chỉ cảnh báo hay đóng/bù tự động?
8. Những build MT5, broker, DPI và Windows version nào cần hỗ trợ?
9. Tool có cần lưu volume sau khi restart không?
10. Có cần giới hạn volume nghiệp vụ thấp hơn `SYMBOL_VOLUME_MAX` để giảm rủi ro không?

## 12. Hướng dẫn ngắn cho model khác

Khi re-check, không chỉ review tài liệu này. Cần đọc trực tiếp ít nhất các file:

- `ea-monitor/src/core/data_hub.py`
- `ea-monitor/src/core/terminal_worker.py`
- `ea-monitor/MQL5/OCTBridge.mq5`
- `native/mt5engine-capi/engine.cpp`
- `native/mt5engine-capi/c_api.cpp`
- `native/mt5engine-capi/c_api.h`
- `TradeDesktop.App/Native/NativeMethodsMt5.cs`

Yêu cầu model review phân biệt rõ:

- Volume cho lệnh sắp mở.
- Volume của position đang mở.
- Volume hiển thị trên One-Click Trading.
- Volume thực tế của deal được broker khớp.

Không kết luận tính năng hoàn thành chỉ vì request đã chứa trường `volume`; phải có bằng chứng volume được áp dụng và xác nhận trên MT5.
