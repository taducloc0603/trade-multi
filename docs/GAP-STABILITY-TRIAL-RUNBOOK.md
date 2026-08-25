# Runbook chạy thử Gap Stability

## Mục tiêu

Thu thập dữ liệu thực tế riêng cho Open và Normal Close trước khi hiệu chỉnh tham số. Không coi cấu hình khởi đầu là cấu hình tối ưu cố định.

## Chuẩn bị

1. Chạy đúng một phiên bản build trên máy thử và ghi lại commit/tag hoặc artifact version.
2. Sao lưu giá trị 12 cột Gap Stability hiện tại của config được chọn.
3. Đặt `limit_max_gap = 0` và `max_gap = 0` để cô lập Stable Cycle. Các guard latency, spread, price-freeze và portfolio giữ nguyên.
4. Ghi lại machine/config ID, symbol, phiên thị trường, múi giờ và thời điểm bắt đầu.
5. Xác nhận file log được tạo tại `Desktop\trade-log` và có dòng `[CONFIG][GAP_STABILITY]`.

Không bật giao dịch thật nếu phiên này chỉ nhằm quan sát thuật toán. Việc chuyển từ paper/demo sang tài khoản thật là một quyết định vận hành riêng.

## Quy trình từng đợt

1. Chạy một bộ tham số không đổi trong suốt đợt quan sát.
2. Không reload config giữa Cycle. Nếu bắt buộc reload, kết thúc đợt hiện tại, reset/restart engine rồi bắt đầu đợt mới; code hiện tại không tự reset Cycle khi policy đổi.
3. Chạy đủ các vùng thị trường cần so sánh, tối thiểu gồm vùng yên và vùng biến động.
4. Kết thúc phiên ứng dụng bình thường để logger flush hết hàng đợi.
5. Tổng hợp một hoặc nhiều file log:

```bash
bash tools/analyze-gap-stability-logs.sh "/path/to/trade-log.log"
```

Có thể truyền nhiều file của cùng một bộ tham số. Không gộp file từ các bộ tham số khác nhau.

## Cách đọc báo cáo

- `cycles`: Cycle bắt đầu mới, gồm Started và tách bởi Delta.
- `stable`, `unstable`, `rejected`: kết quả Cycle.
- `delta_split`: số lần tạo Cycle mới vì Delta vượt Tolerance.
- `dispersion`, `drift`: số Cycle Unstable có lý do tương ứng; một Cycle có thể được tính ở cả hai dòng.
- `avg_stable_duration_ms`: thời gian trung bình để Cycle lần đầu trở thành Stable.
- `triggers`: trigger Open/Close do Stable Cycle phát ra.
- `guard_blocked`: trigger bị guard hiện tại chặn sau Stable.
- `confirmed`, `blocked`, `failed`, `cancelled`: outcome cuối lấy từ signal lifecycle log.

## Nhật ký hiệu chỉnh

Mỗi lần chỉ đổi một nhóm nhỏ tham số Open hoặc Close, rồi tạo một đợt chạy mới. Ghi theo mẫu:

```text
trial_id:
machine/config:
artifact/commit:
symbol/session:
start/end:
open parameters:
close parameters:
report:
nhận xét thị trường:
thay đổi đề xuất:
lý do:
```

Không kết luận chỉ từ số Cycle Stable. Cần đối chiếu outcome, slippage/lợi nhuận thực tế và tỷ lệ guard block sau trigger.
