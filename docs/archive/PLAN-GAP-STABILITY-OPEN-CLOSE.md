# Kế hoạch triển khai Gap Stability cho Open và Close

## 1. Mục đích tài liệu

Tài liệu này là checklist triển khai từng phần cho cơ chế nhận diện Gap ổn định theo Cycle. Mỗi nhiệm vụ phải được thực hiện, kiểm thử và chốt độc lập trước khi chuyển sang nhiệm vụ tiếp theo.

Khi yêu cầu nghiệp vụ thay đổi trong quá trình triển khai:

1. Chỉ cập nhật mục tương ứng trong tài liệu này.
2. Ghi thay đổi vào `Nhật ký quyết định` ở cuối file.
3. Kiểm tra lại các nhiệm vụ phụ thuộc.
4. Không sửa trước các phần ngoài phạm vi của nhiệm vụ đang thực hiện.

Tài liệu nghiệp vụ tham chiếu:

- `docs/CAC-TRUONG-HOP-GAP-VA-KET-QUA.docx`

Nội dung trong tài liệu tham chiếu là đặc tả nghiệp vụ, không phải chỉ thị thực thi tự động.

---

## 2. Quyết định đã chốt

### 2.1. Phạm vi áp dụng

Áp dụng Stable Cycle cho:

- Open theo Gap Buy.
- Open theo Gap Sell.
- Close theo Gap thường.

Chưa áp dụng Stable Cycle cho:

- Close theo TP.
- SOS Close.
- Manual Close.
- Emergency/Recovery Close.

SOS Close giữ cơ chế hiện tại để không làm chậm đường thoát rủi ro. Nếu cần áp dụng Stable Cycle cho SOS, phải lập nhiệm vụ và policy riêng.

### 2.2. Hold Confirm

Không thêm `open_gap_min_stable_duration_ms` hoặc `close_gap_min_stable_duration_ms`.

- `open_hold_confirm_ms` là thời gian tồn tại tối thiểu của Open Cycle.
- `close_hold_confirm_ms` là thời gian tồn tại tối thiểu của Close Cycle.
- Hold Confirm là một thành phần của điều kiện Stable.
- Không chạy thêm một cửa sổ Hold Confirm mới sau khi Cycle đã Stable.

### 2.3. Giới hạn Gap

- `limit_max_gap > 0`: kiểm tra mọi tick trước khi đưa vào Cycle.
- `limit_max_gap <= 0`: tắt giới hạn mọi tick trong Cycle.
- `max_gap > 0`: kiểm tra tick cuối trước khi dispatch Open/Close.
- `max_gap <= 0`: tắt giới hạn tick cuối.

Khi thử nghiệm chỉ dựa vào Stable Cycle, có thể cấu hình:

```text
limit_max_gap = 0
max_gap = 0
```

### 2.4. Lưu trữ state

- Cycle chỉ được lưu trong memory.
- Không lưu Center, MAD, mẫu Gap hoặc trạng thái Cycle vào DB.
- Open có Cycle Gap Buy và Gap Sell độc lập.
- Close có Cycle riêng theo từng slot.
- State của slot này không được ảnh hưởng slot khác.

### 2.5. Dữ liệu thiếu

- Không thay Gap `null` bằng `0`.
- Không thêm tick thiếu Bid/Ask cần thiết vào Cycle.
- Không được phát tín hiệu từ tick thiếu dữ liệu.
- Phiên bản đầu reset Cycle khi dữ liệu cần thiết bị thiếu để bảo đảm tính liên tục của Hold Confirm.

---

## 3. Điều kiện phát tín hiệu

### 3.1. Open

```text
Mọi mẫu trong Cycle đạt ConfirmGapPts
AND SampleCount >= open_gap_min_stable_samples
AND Duration >= open_hold_confirm_ms
AND Dispersion <= open_gap_max_dispersion
AND Drift <= open_gap_max_drift
AND tick cuối đạt OpenPts
AND vượt qua các guard và giới hạn giao dịch hiện tại
```

### 3.2. Close Gap thường

```text
Mọi mẫu trong Cycle đạt CloseConfirmGapPts
AND SampleCount >= close_gap_min_stable_samples
AND Duration >= close_hold_confirm_ms
AND Dispersion <= close_gap_max_dispersion
AND Drift <= close_gap_max_drift
AND tick cuối đạt ClosePts
AND vượt qua holding, min-profit, cooldown và các điều kiện Close hiện tại
```

Stable chỉ cho phép tín hiệu đi tiếp; Stable không tự dispatch lệnh.

---

## 4. Cấu hình DB dự kiến

### 4.1. Open policy

| Cột | Giá trị khởi đầu | Ý nghĩa |
|---|---:|---|
| `open_gap_absolute_floor` | `10` | `[OPEN GAP STABILITY]` Biên tuyệt đối tối thiểu A của Tolerance, đơn vị Gap point |
| `open_gap_relative_tolerance` | `0.50` | `[OPEN GAP STABILITY]` Tỷ lệ R theo Center; `0.50` tương đương 50% |
| `open_gap_mad_multiplier` | `4.0` | `[OPEN GAP STABILITY]` Hệ số K nhân với MAD |
| `open_gap_min_stable_samples` | `3` | `[OPEN GAP STABILITY]` Số mẫu tối thiểu trước khi xét Stable |
| `open_gap_max_dispersion` | `0.35` | `[OPEN GAP STABILITY]` Dispersion tối đa trước khi phát Open signal |
| `open_gap_max_drift` | `0.40` | `[OPEN GAP STABILITY]` Drift tối đa trước khi phát Open signal |

### 4.2. Close policy

| Cột | Giá trị khởi đầu | Ý nghĩa |
|---|---:|---|
| `close_gap_absolute_floor` | `10` | `[NORMAL CLOSE GAP STABILITY]` Biên tuyệt đối tối thiểu A của Tolerance, đơn vị Gap point |
| `close_gap_relative_tolerance` | `0.60` | `[NORMAL CLOSE GAP STABILITY]` Tỷ lệ R theo Center; `0.60` tương đương 60% |
| `close_gap_mad_multiplier` | `4.0` | `[NORMAL CLOSE GAP STABILITY]` Hệ số K nhân với MAD |
| `close_gap_min_stable_samples` | `3` | `[NORMAL CLOSE GAP STABILITY]` Số mẫu tối thiểu trước khi xét Stable |
| `close_gap_max_dispersion` | `0.45` | `[NORMAL CLOSE GAP STABILITY]` Dispersion tối đa trước khi phát Close Gap signal |
| `close_gap_max_drift` | `0.60` | `[NORMAL CLOSE GAP STABILITY]` Drift tối đa trước khi phát Close Gap signal |

Các giá trị trên là giá trị khởi đầu để kiểm thử, không phải giá trị tối ưu cố định cho mọi thị trường.

---

## 5. Quy tắc thay đổi theo từng nhiệm vụ

Trước khi bắt đầu một nhiệm vụ:

- Xác nhận nhiệm vụ trước đó đã đạt tiêu chí nghiệm thu.
- Ghi trạng thái thành `Đang thực hiện`.
- Ghi rõ các file dự kiến sửa.
- Nếu cần sửa file ngoài phạm vi dự kiến, cập nhật phạm vi trước khi sửa.

Sau khi hoàn thành:

- Chạy đúng nhóm test của nhiệm vụ.
- Ghi lệnh test và kết quả vào mục `Kết quả thực hiện`.
- Chỉ đánh dấu `Hoàn thành` khi toàn bộ Expected đạt.
- Nếu thay đổi interface hoặc nghiệp vụ, đánh dấu lại các nhiệm vụ phụ thuộc là cần rà soát.

Trạng thái hợp lệ:

```text
Chưa thực hiện
Đang thực hiện
Tạm dừng
Hoàn thành
```

---

# 6. Danh sách nhiệm vụ

## Nhiệm vụ 1 — Migration DB

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Không

### Phạm vi

- Thêm 12 cột cấu hình Open/Close Stability.
- Khởi tạo giá trị cho các record hiện tại.
- Không thêm application fallback âm thầm.
- Thêm comment mô tả cột.
- Thêm constraint dữ liệu.

Validation:

```text
absolute_floor >= 0
relative_tolerance >= 0
mad_multiplier >= 0
min_stable_samples >= 3
max_dispersion >= 0
max_drift >= 0
```

### Không thuộc phạm vi

- Chưa sửa signal engine.
- Chưa thay đổi hành vi Open/Close.
- Chưa thêm Cycle state.

### Expected

- Mọi record hiện tại có đủ 12 giá trị.
- Open và Close có thể cấu hình độc lập.
- Constraint chặn được dữ liệu không hợp lệ.
- Hành vi giao dịch chưa thay đổi.

### Kết quả thực hiện

- File/SQL đã thay đổi: `docs/GAP-STABILITY-CONFIG-MIGRATION.sql`
- Lệnh kiểm tra: truy vấn kiểm tra null và truy vấn liệt kê constraint trên `public.configs`.
- Kết quả: Migration đã chạy trên Supabase; `total_configs = 19`, `configs_with_missing_values = 0`; đủ 12 constraint Open/Close Gap Stability.
- Ghi chú: Migration không thay đổi `max_gap`, `limit_max_gap`, `current_slots` hoặc trạng thái giao dịch hiện tại. Nhiệm vụ 1 được xác nhận hoàn thành ngày 2026-08-25.

---

## Nhiệm vụ 2 — Mapping và validation cấu hình

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Nhiệm vụ 1

### Phạm vi

- Cập nhật Supabase `ConfigRow`.
- Cập nhật `ConfigRecord`.
- Cập nhật `RuntimeConfigState`.
- Cập nhật luồng reload config.
- Tạo model `GapStabilityConfig` dùng chung.
- Tạo hai instance policy: Open và Close.
- Log cấu hình khi khởi động/reload.
- Config không hợp lệ phải fail-safe và có log rõ.

### File dự kiến

- `TradeDesktop.Infrastructure/Supabase/SupabaseConfigRepository.cs`
- `TradeDesktop.Application/Models/ConfigRecord.cs`
- `TradeDesktop.Application/Models/GapSignalModels.cs`
- `TradeDesktop.App/State/RuntimeConfigState.cs`
- `TradeDesktop.App/ViewModels/DashboardViewModel.cs`
- Các test config liên quan.

### Không thuộc phạm vi

- Chưa thay thuật toán signal.
- Chưa thay Close/SOS/TP.

### Expected

- Đọc đúng 12 giá trị từ Supabase.
- Reload cập nhật đúng hai policy.
- Không hard-code giá trị đề xuất trong engine.
- Config sai không được phép phát auto trade.
- Test mapping và validation pass.

### Kết quả thực hiện

- File đã thay đổi: `TradeDesktop.Application/Models/GapSignalModels.cs`, `TradeDesktop.Application/Models/ConfigRecord.cs`, `TradeDesktop.Application/Services/ConfigService.cs`, `TradeDesktop.Application/Abstractions/IRuntimeConfigProvider.cs`, `TradeDesktop.Infrastructure/Supabase/SupabaseConfigRepository.cs`, `TradeDesktop.App/State/RuntimeConfigState.cs`, `TradeDesktop.App/ViewModels/DashboardViewModel.cs`, `TradeDesktop.App/ViewModels/ConfigViewModel.cs`, `TradeDesktop.Tests/Config/GapStabilityConfigMappingTests.cs`.
- Lệnh test: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: 392/392 test pass; nhóm test Gap Stability mapping/validation 12/12 pass; build `TradeDesktop.App` thành công, 0 warning và 0 error.
- Ghi chú: Chỉ mapping, runtime state, validation và startup/reload log được thay đổi. Thuật toán Open/Close chưa sử dụng các policy mới. Config thiếu, sai miền hoặc không hữu hạn sẽ fail-safe ở `ConfigService`.

---

## Nhiệm vụ 3 — GapStabilityCalculator

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Có thể thực hiện độc lập với Nhiệm vụ 1–2

### Phạm vi

Xây dựng bộ tính toán thuần, không phụ thuộc Open/Close/UI/slot:

```text
Median
Center
MAD
Tolerance
Delta
Dispersion
EarlyCenter
LateCenter
Drift
```

Công thức:

```text
Center = Median(|Gap|)
MAD = Median(abs(|Gap| - Center))
Tolerance = max(AbsoluteFloor, RelativeTolerance × Center, MadMultiplier × MAD)
Delta = abs(|NewGap| - Center)
Dispersion = MAD / max(Center, 1)
Drift = abs(LateCenter - EarlyCenter) / max(Center, 1)
```

### Test bắt buộc

- Số mẫu chẵn/lẻ.
- Gap Buy dương và Gap Sell âm.
- Center bằng 0.
- MAD bằng 0.
- Một và hai mẫu.
- Giữ dấu Gap gốc để xác định hướng.

### Expected

```text
[100,120,150,110,160]
Center = 120
MAD = 20
Tolerance = 80 với A=10, R=0.5, K=4
Dispersion ≈ 0.167
```

```text
[20,30,40,50,60,70]
Center = 45
EarlyCenter = 30
LateCenter = 60
Drift ≈ 0.667
```

### Kết quả thực hiện

- File đã thay đổi: `TradeDesktop.Application/Services/GapStabilityCalculator.cs`, `TradeDesktop.Tests/GapStabilityCalculatorTests.cs`.
- Lệnh test: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: 14/14 test riêng của calculator pass; toàn bộ 406/406 test pass; build `TradeDesktop.App` thành công, 0 warning và 0 error.
- Ghi chú: Với số mẫu lẻ lớn hơn 1, mẫu giữa không thuộc nửa đầu hoặc nửa sau khi tính Drift. Calculator dùng trị tuyệt đối để tính thống kê nhưng không sửa dấu/danh sách Gap đầu vào. Thuật toán signal chưa sử dụng calculator ở nhiệm vụ này.

---

## Nhiệm vụ 4 — GapCycleState

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Nhiệm vụ 3

### Phạm vi

State lưu trong memory:

```text
StartedAtUtc
LastTickUtc
OriginalGaps
Center
MAD
Tolerance
Dispersion
Drift
Status
LastTransitionReason
```

Trạng thái:

```text
Collecting
Stable
Unstable
Rejected
```

Quy tắc:

- Mẫu đầu tiên hợp lệ tạo Cycle.
- `Delta <= Tolerance`: thêm mẫu và tính lại thống kê.
- `Delta > Tolerance`: kết thúc Cycle cũ và tạo Cycle mới từ mẫu hiện tại.
- Khi đủ mẫu/thời gian nhưng Dispersion hoặc Drift không đạt: đánh dấu Cycle cũ `Unstable`, không phát tín hiệu và bắt đầu lại từ mẫu gần nhất.
- Gap không đạt Confirm reset Cycle tương ứng.
- Dữ liệu cần thiết bị thiếu reset Cycle; không thêm Gap 0 giả.
- Tick vượt `limit_max_gap` khi giới hạn bật: reject, reset, không dùng làm tâm Cycle mới.

Việc reset Cycle Unstable và bắt đầu lại từ mẫu gần nhất là chính sách phiên bản đầu nhằm tránh danh sách mẫu tăng vô hạn. Nếu chính sách này thay đổi, phải cập nhật test ở Nhiệm vụ 5 trước khi tích hợp.

### Expected

- Nhận từng tick và trả được trạng thái/thống kê/lý do chuyển trạng thái.
- Không phụ thuộc engine giao dịch.
- Không tăng bộ nhớ vô hạn với Cycle Unstable.
- Unit test state transition pass.

### Kết quả thực hiện

- File đã thay đổi: `TradeDesktop.Application/Services/GapCycleState.cs`, `TradeDesktop.Tests/GapCycleStateTests.cs`.
- Lệnh test: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: 13/13 test riêng của GapCycleState pass; toàn bộ 419/419 test pass; build `TradeDesktop.App` thành công, 0 warning và 0 error.
- Ghi chú: Kết quả mỗi tick chứa snapshot Cycle hiện tại và tùy chọn snapshot Cycle vừa kết thúc. Khi Cycle đủ mẫu/thời gian nhưng Unstable, mẫu cuối được giữ trong snapshot Cycle cũ đồng thời trở thành mẫu đầu Cycle mới. State chưa được tích hợp vào Open/Close engine.

---

## Nhiệm vụ 5 — Chuyển các trường hợp tài liệu thành test

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Nhiệm vụ 3–4

### Stable

```text
[2,5,6,5,2,7,4]
[100,120,150,110,160]
[-100,-120,-150,-110,-160]
```

### Tách Cycle và spike

```text
[2,4,2,6,1,7,3,20]
[2,4,2,6,1,7,3,20,2]
[2,4,3,5,100,110,120,115]
[100,120,150,110,160,400]
[100,120,150,110,160,400,130]
```

### Unstable

```text
Drift: [20,30,40,50,60,70]
Đổi vùng liên tục: [100,250,80,300,70,280]
```

Quyết định ưu tiên cho trường hợp `[100,250,80,300,70,280]`:

- Áp dụng Delta/Tolerance trước khi một mẫu được thêm vào Cycle.
- Chuỗi được tách thành `[100]`, `[250]`, `[80]`, `[300]`, `[70]`, `[280]`.
- Không Cycle nào đủ `MinStableSamples`, vì vậy không giao dịch.
- Không gom cưỡng chế toàn bộ chuỗi thành một Cycle chỉ để tính Dispersion.
- Phép tính `Center = 175`, `MAD = 100`, `Dispersion = 0.571` được giữ như test thuần toán học cho toàn chuỗi, không phải kết quả phân chia Cycle runtime.
- Quyết định trong plan này thay thế mô tả phân chia Cycle của trường hợp H trong tài liệu Word nếu hai nội dung mâu thuẫn.

### Dữ liệu lỗi

- Gap null.
- Thiếu Bid/Ask.
- Feed đóng băng.
- Vượt `limit_max_gap` khi bật.
- Không giới hạn khi `limit_max_gap = 0`.

### Expected

Mỗi test xác minh:

- Số Cycle và mẫu của từng Cycle.
- Center/MAD/Tolerance.
- Dispersion/Drift.
- Trạng thái cuối.
- Lý do reset/reject.

Không tích hợp Cycle vào luồng giao dịch trước khi nhóm test này pass.

### Kết quả thực hiện

- File đã thay đổi: `TradeDesktop.Tests/GapStabilityDocumentScenariosTests.cs`.
- Lệnh test: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: 13/13 nhóm test theo tài liệu pass theo hành vi code hiện tại; toàn bộ 432/432 test pass; build `TradeDesktop.App` thành công, 0 warning và 0 error.
- Ghi chú: Trường hợp H đã chốt ưu tiên Delta/Tolerance và tách thành sáu Cycle đơn. Kết quả cuối vẫn là không giao dịch vì không Cycle nào đủ mẫu. `Dispersion = 0.571` chỉ còn là phép kiểm tra calculator trên toàn chuỗi. Nhiệm vụ 5 hoàn thành và cho phép bắt đầu Nhiệm vụ 6.

---

## Nhiệm vụ 6 — Tích hợp Open Engine

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Nhiệm vụ 2–5

### Phạm vi

Open engine quản lý độc lập:

```text
Open GapBuy Cycle
Open GapSell Cycle
```

Luồng:

1. Kiểm tra Gap/Bid/Ask.
2. Kiểm tra ngưỡng Confirm theo hướng.
3. Cập nhật Cycle.
4. Kiểm tra số mẫu và `open_hold_confirm_ms`.
5. Kiểm tra Dispersion/Drift.
6. Kiểm tra tick cuối đạt `OpenPts`.
7. Tạo `GapSignalTriggerResult`.
8. Reset Cycle sau trigger.

### Không thuộc phạm vi

- Không sửa hành vi Close trong nhiệm vụ này.
- Không sửa SOS/TP.

### Expected

- Gap lớn ổn định có thể phát Open trigger.
- Spike đơn không phát Open trigger.
- Chuyển vùng thật phải xác nhận lại.
- Buy/Sell không trộn state.
- Không phát lặp trigger từ cùng Cycle.
- Guard và portfolio flow hiện tại vẫn hoạt động.

### Kết quả thực hiện

- File đã thay đổi: `TradeDesktop.Application/Models/GapSignalModels.cs`, `TradeDesktop.Application/Services/GapSignalConfirmationEngine.cs`, `TradeDesktop.App/ViewModels/DashboardViewModel.cs`, `TradeDesktop.Tests/GapStableOpenIntegrationTests.cs`.
- Lệnh test: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: 9/9 test riêng của Open Stable integration pass; toàn bộ 441/441 test pass; build `TradeDesktop.App` thành công, 0 warning và 0 error.
- Ghi chú: Runtime production truyền Open policy đã được DB validation; nếu policy chưa load, Dashboard fail-safe trước khi gọi trading flow. Các test/caller cũ không truyền policy tiếp tục dùng legacy Open path để giữ regression trong giai đoạn chuyển đổi. Close/SOS/TP chưa đổi. Buy/Sell Cycle độc lập và reset sau trigger.

---

## Nhiệm vụ 7 — Tích hợp Close Gap thường

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Nhiệm vụ 2–6

### Phạm vi

Mỗi slot có Close Cycle riêng:

```text
Position mở từ GapBuy → Close theo GapSell Cycle
Position mở từ GapSell → Close theo GapBuy Cycle
```

Reset Close Cycle khi:

- Slot đóng thành công.
- Slot bị hủy hoặc recovery lại.
- Close mode thay đổi.
- Chuyển Normal sang SOS.
- Dữ liệu không còn liên tục.
- Gap không đạt Close Confirm.

### Không thuộc phạm vi

- Không áp dụng Stable Cycle cho TP.
- Không áp dụng Stable Cycle cho SOS.
- Không thay Manual/Emergency Close.

### Expected

- Một tick đảo chiều không đóng vị thế ngay.
- Gap đảo chiều ổn định mới phát Close trigger.
- Cycle của các slot không ảnh hưởng nhau.
- Holding, min-profit, cooldown giữ nguyên.
- TP Close giữ nguyên hành vi.

### Kết quả thực hiện

- File đã thay đổi: `TradeDesktop.Application/Models/GapSignalModels.cs`, `TradeDesktop.Application/Services/CloseSignalEngine.cs`, `TradeDesktop.App/ViewModels/DashboardViewModel.cs`, `TradeDesktop.Tests/GapStableCloseIntegrationTests.cs`.
- Lệnh test: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: 9/9 test riêng của Normal Close Stable integration pass; toàn bộ 450/450 test pass; build `TradeDesktop.App` thành công, 0 warning và 0 error.
- Ghi chú: Mỗi `CloseSignalEngine` giữ hai Normal Close Cycle riêng và mỗi slot đã có một engine instance riêng qua factory. Normal Close dùng Close policy; SOS và TP tiếp tục đường xử lý cũ. `ResetGapState` xóa cả legacy window và Stable Cycle. Runtime fail-safe nếu thiếu Open hoặc Close policy.

---

## Nhiệm vụ 8 — Ranh giới SOS

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Nhiệm vụ 7

### Phạm vi

- Khi Normal chuyển sang SOS: reset Close Stable Cycle thường.
- SOS tiếp tục dùng logic xác nhận hiện tại.
- Khi SOS quay lại Normal: bắt đầu Close Cycle mới.
- Không dùng lại mẫu trước khi đổi mode.

### Expected

- Stable Cycle không trì hoãn SOS.
- Không rò rỉ mẫu giữa Normal và SOS.
- Toàn bộ test SOS hiện tại pass.

### Kết quả thực hiện

- File đã thay đổi: `TradeDesktop.Tests/GapStableCloseIntegrationTests.cs`.
- Lệnh test: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: nhóm Normal/SOS boundary và coordinator SOS rule 19/19 pass; toàn bộ 453/453 test pass; build `TradeDesktop.App` thành công, 0 warning và 0 error.
- Ghi chú: Production code hiện tại đã gọi `ResetGapState` đúng lúc `PositionSlot.UpdateSosMode` đổi trạng thái nên không cần sửa thêm. Test mới khóa Normal → SOS, SOS → Normal và xác nhận Gap reset không reset TP window. SOS tiếp tục legacy fast path; Normal quay lại Stable Cycle mới.

---

## Nhiệm vụ 9 — `limit_max_gap` và `max_gap`

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Nhiệm vụ 6–8

### Test bắt buộc

1. `limit_max_gap = 0`, `max_gap = 0`:
   - Không loại tick vì độ lớn tuyệt đối.
   - Chuỗi Gap rất lớn nhưng ổn định vẫn có thể trigger.
2. `limit_max_gap = 500`:
   - Tick `550` bị reject và reset.
   - Không dùng `550` tạo Cycle mới.
3. `max_gap = 500`:
   - Cycle có thể Stable.
   - Tick cuối vượt `500` bị chặn trước dispatch.
4. `limit_max_gap = 500`, `max_gap = 450`:
   - Cycle có thể nhận mẫu `480`.
   - Không dispatch nếu tick cuối là `480`.

### Expected

- Hai giới hạn có ý nghĩa độc lập.
- `0 = disabled` nhất quán cho Open và Close.
- Log phân biệt rõ reject tại Cycle và reject tại dispatch guard.

### Kết quả thực hiện

- File đã thay đổi: `TradeDesktop.Tests/GapLimitIntegrationTests.cs`.
- Lệnh test riêng: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true --filter FullyQualifiedName~GapLimitIntegrationTests -m:1`.
- Lệnh test hồi quy: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: 7/7 test tích hợp giới hạn Gap pass; toàn bộ 460/460 test pass; build `TradeDesktop.App` thành công, 0 warning và 0 error.
- Ghi chú: Test khóa hành vi `0 = disabled` cho cả Open/Close, `limit_max_gap` reject và reset ngay tại Cycle, `max_gap` chỉ chặn tick cuối tại dispatch guard, hai giới hạn hoạt động độc lập và giá trị đúng bằng `max_gap` vẫn được chấp nhận. Lý do trả về phân biệt reject tại Cycle với reject tại dispatch; việc phát log runtime chi tiết thuộc Nhiệm vụ 10.

---

## Nhiệm vụ 10 — Logging và quan sát

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Nhiệm vụ 6–9

### Sự kiện log

- Cycle started.
- New Cycle created.
- Cycle Stable.
- Cycle Unstable.
- Cycle Rejected.
- Cycle reset.
- Open/Close trigger emitted.

### Trường log

```text
action
side
slot_id
sample_count
duration_ms
center
mad
tolerance
new_gap
delta
dispersion
drift
status
reason
```

Không log mỗi tick nếu trạng thái không thay đổi.

### Expected

- Có thể xác định vì sao Cycle tách/reset.
- Phân biệt bị chặn bởi Stability và guard hiện tại.
- Log không spam trong luồng tick bình thường.

### Kết quả thực hiện

- File đã thay đổi: `TradeDesktop.Application/Services/GapCycleDiagnostics.cs`, `TradeDesktop.Application/Services/GapSignalConfirmationEngine.cs`, `TradeDesktop.Application/Services/CloseSignalEngine.cs`, `TradeDesktop.Application/Services/Portfolio/CloseSignalEngineFactory.cs`, `TradeDesktop.Application/Services/Portfolio/PositionSlot.cs`, `TradeDesktop.Tests/GapCycleLoggingTests.cs`.
- Lệnh test riêng: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true --filter FullyQualifiedName~GapCycleLoggingTests -m:1`.
- Lệnh test hồi quy: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: 3/3 test logging pass; toàn bộ 463/463 test pass; build `TradeDesktop.App` thành công, 0 warning và 0 error.
- Ghi chú: Log đi qua `ISlotLogger` hiện có và có prefix `[GAP_STABILITY]`. Open dùng `slot_id=-`; mỗi Close engine được gắn đúng `slot_id` khi tạo hoặc thay engine trong slot. Chỉ log Started, New Cycle, Stable, Unstable, Rejected, reset có Cycle thực và trigger emitted; không log `Joined` hoặc reset rỗng nên không spam theo tick. Log chứa đủ `action`, `side`, `slot_id`, metrics, Gap mới, trạng thái và lý do.

---

## Nhiệm vụ 11 — Regression toàn hệ thống

**Trạng thái:** Hoàn thành

**Phụ thuộc:** Nhiệm vụ 1–10

### Test cần chạy

- Gap Stability calculator/state.
- Open confirmation.
- Close confirmation.
- TP Close.
- SOS Close.
- Signal guard.
- Trading flow.
- Portfolio multi-slot.
- Recovery.
- Build toàn solution.

### Trường hợp rủi ro

- Reload config giữa Cycle.
- Timestamp bị lùi hoặc trùng.
- Gap null giữa Cycle.
- Hai slot cùng Close.
- Open Buy/Sell cùng nhận dữ liệu.
- Trigger thành công nhưng dispatch thất bại.
- Reset engine hoặc reconnect feed.
- Cycle Unstable kéo dài.

### Expected

- Test mới pass.
- Test cũ không regression.
- Không phát lệnh trùng.
- Không rò rỉ state giữa slot.
- Không tăng bộ nhớ vô hạn.
- TP và SOS giữ nguyên hành vi.

### Kết quả thực hiện

- File đã thay đổi: chỉ cập nhật checkpoint trong `docs/PLAN-GAP-STABILITY-OPEN-CLOSE.md`; không cần sửa production code ở bước regression.
- Lệnh test nhóm cốt lõi: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true --filter 'FullyQualifiedName~GapStability|FullyQualifiedName~GapCycle|FullyQualifiedName~GapStable|FullyQualifiedName~GapSignalConfirmationEngineTests|FullyQualifiedName~CloseSignalEngineTests|FullyQualifiedName~SignalEntryGuardTests|FullyQualifiedName~SosClose' -m:1`.
- Lệnh test flow/portfolio: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true --filter 'FullyQualifiedName~TradingFlowEngineTests|FullyQualifiedName~PortfolioCoordinatorAdapterTests|FullyQualifiedName~MultiSlotIntegrationTests|FullyQualifiedName~RaceProtectionTests|FullyQualifiedName~StabilityStressTests|FullyQualifiedName~PortfolioCoordinatorRecoveryTests|FullyQualifiedName~PriorityCloseRuleTests' -m:1`.
- Lệnh test toàn bộ: `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Lệnh build: `dotnet build TradeDesktop.sln --no-restore -p:EnableWindowsTargeting=true -m:1`.
- Kết quả: nhóm Gap/Open/Close/TP/SOS/guard 145/145 pass; nhóm flow/multi-slot/race/stress/recovery 59/59 pass; toàn bộ 463/463 test pass; toàn solution build thành công, 0 warning và 0 error.
- Ghi chú: Các test hiện có đã khóa Gap null, timestamp lùi, reset engine, Open Buy/Sell cùng nhận dữ liệu, Cycle Unstable, TP/SOS, hai slot Close, race allocation và recovery. Không phát hiện regression, lệnh trùng hoặc rò state. Reload config giữa Cycle hiện giữ Cycle và áp dụng policy mới lên các mẫu đang có; bước regression không tự đổi hành vi này vì đây vẫn là điểm nghiệp vụ cần chốt trước khi thay đổi.

---

## Nhiệm vụ 12 — Chạy thử và hiệu chỉnh

**Trạng thái:** Sẵn sàng chạy thử — chờ dữ liệu thị trường thực tế

**Phụ thuộc:** Nhiệm vụ 11

### Cấu hình chạy thử đề xuất

```text
limit_max_gap = 0
max_gap = 0
```

Mục tiêu là cô lập và đánh giá Stable Cycle. Các guard latency, spread, price-freeze và giới hạn portfolio vẫn giữ nguyên.

### Dữ liệu cần thu thập riêng cho Open/Close

- Tổng Cycle.
- Số Cycle Stable/Unstable/Rejected.
- Số Cycle bị Drift hoặc Dispersion.
- Số Cycle tách bởi Delta.
- Thời gian trung bình để Stable.
- Số Open/Close trigger.
- Số trigger bị guard sau Stable chặn.
- Kết quả thực tế sau trigger.

### Expected

- Có dữ liệu thực tế để hiệu chỉnh Open và Close độc lập.
- Mọi thay đổi tham số được ghi lại cùng thời gian và kết quả quan sát.
- Không xem bộ giá trị khởi đầu là cấu hình tối ưu cố định.

### Kết quả thực hiện

- Máy/cấu hình thử: chưa chạy trên máy giao dịch; môi trường phát triển hiện tại không có feed/terminal Windows. Khi chạy phải ghi machine/config ID theo `docs/GAP-STABILITY-TRIAL-RUNBOOK.md`.
- Thời gian thử: chưa bắt đầu phiên quan sát thực tế.
- Chuẩn bị đã hoàn thành: thêm `tools/analyze-gap-stability-logs.sh` để tổng hợp riêng Open/Close từ một hoặc nhiều `trade-log`; thêm `docs/GAP-STABILITY-TRIAL-RUNBOOK.md` hướng dẫn cô lập tham số, thu thập, đọc báo cáo và ghi lịch sử hiệu chỉnh.
- Kết quả kiểm tra công cụ: `bash -n` pass; log mẫu tổng hợp đúng Cycle, Stable/Unstable, Dispersion/Drift, trigger, guard block và signal outcome; `git diff --check` pass.
- Điều chỉnh đề xuất: chưa đề xuất đổi tham số trước khi có dữ liệu thật. Đợt đầu giữ `limit_max_gap = 0`, `max_gap = 0`, không reload config giữa Cycle và giữ nguyên các guard khác.
- Điều kiện hoàn thành Nhiệm vụ 12: chạy ít nhất một đợt trên máy có dữ liệu thị trường, lưu báo cáo cùng bộ tham số/thời gian quan sát, rồi mới đánh giá Open và Close độc lập.

---

## 7. Thứ tự thực hiện và checkpoint

```text
Nhiệm vụ 1: DB
    ↓
Nhiệm vụ 2: Mapping config
    ↓
Nhiệm vụ 3: Calculator
    ↓
Nhiệm vụ 4: Cycle state
    ↓
Nhiệm vụ 5: Test tài liệu         ← checkpoint thuật toán
    ↓
Nhiệm vụ 6: Open integration      ← checkpoint Open
    ↓
Nhiệm vụ 7: Close integration     ← checkpoint Close
    ↓
Nhiệm vụ 8: SOS boundary
    ↓
Nhiệm vụ 9: Gap limits
    ↓
Nhiệm vụ 10: Logging
    ↓
Nhiệm vụ 11: Regression           ← checkpoint release
    ↓
Nhiệm vụ 12: Chạy thử
```

Không chuyển sang tích hợp Open/Close nếu Nhiệm vụ 5 chưa pass đầy đủ.

---

## 8. Nhật ký quyết định

| Ngày | Nhiệm vụ | Thay đổi/quyết định | Lý do | Ảnh hưởng cần rà soát |
|---|---|---|---|---|
| 2026-08-25 | Tổng thể | Hold Confirm trở thành thời gian tối thiểu của Stable Cycle | Tránh hai cửa sổ thời gian chạy nối tiếp | DB, Open, Close, test |
| 2026-08-25 | Tổng thể | Tách 6 cấu hình Open và 6 cấu hình Close | Cho phép hiệu chỉnh độc lập | DB và mapping config |
| 2026-08-25 | Tổng thể | `limit_max_gap = 0`, `max_gap = 0` nghĩa là disabled | Không dùng ngưỡng cực lớn giả lập vô hạn | Cycle và SignalEntryGuard |
| 2026-08-25 | SOS | Chưa áp dụng Stable Cycle cho SOS | Không trì hoãn đường thoát rủi ro | Close engine và SOS tests |
| 2026-08-25 | Nhiệm vụ 1 | Migration DB hoàn thành cho 19 config, không có giá trị thiếu và đủ 12 constraint | Kết quả xác minh trực tiếp trên Supabase | Cho phép bắt đầu Nhiệm vụ 2 |
| 2026-08-25 | Nhiệm vụ 2 | Hoàn thành mapping hai policy DB → ConfigRecord → ConfigLoadResult → RuntimeConfigState và validation fail-safe | Cô lập thay đổi config trước khi sửa thuật toán | Cho phép bắt đầu Nhiệm vụ 3 |
| 2026-08-25 | Nhiệm vụ 3 | Hoàn thành GapStabilityCalculator và 14 unit test | Khóa độc lập công thức trước khi xây state Cycle | Cho phép bắt đầu Nhiệm vụ 4 |
| 2026-08-25 | Nhiệm vụ 3 | Khi số mẫu lẻ, loại mẫu giữa khỏi cả Early/Late khi tính Drift | Tránh dùng cùng một mẫu cho cả hai nửa | GapCycleState và test tài liệu |
| 2026-08-25 | Nhiệm vụ 4 | Hoàn thành GapCycleState và 13 unit test chuyển trạng thái | Khóa hành vi Cycle trước khi tích hợp signal | Cho phép bắt đầu Nhiệm vụ 5 |
| 2026-08-25 | Nhiệm vụ 4 | Cycle Unstable kết thúc và bắt đầu lại từ mẫu cuối | Tránh bộ nhớ tăng vô hạn và cho vùng mới xác nhận ngay | Test tài liệu và logging |
| 2026-08-25 | Nhiệm vụ 5 | Phát hiện mâu thuẫn trường hợp H: Delta/Tolerance tách từng mẫu trước khi có thể đánh giá Dispersion toàn chuỗi | Kết quả test trực tiếp theo công thức đã chốt | Tạm dừng trước tích hợp Open; cần quyết định nghiệp vụ |
| 2026-08-25 | Nhiệm vụ 5 | Chốt ưu tiên Delta/Tolerance cho trường hợp H; sáu mẫu tạo sáu Cycle đơn và không giao dịch vì thiếu mẫu | Giữ luật phân chia Cycle nhất quán, không gom dữ liệu cưỡng chế | Hoàn thành checkpoint và cho phép tích hợp Open |
| 2026-08-25 | Nhiệm vụ 6 | Tích hợp hai Stable Cycle độc lập vào Open Engine; production fail-safe khi policy chưa load | Áp dụng thuật toán mới mà không thay Close trong cùng bước | Cho phép bắt đầu Nhiệm vụ 7 |
| 2026-08-25 | Nhiệm vụ 7 | Tích hợp Stable Cycle vào Normal Close theo từng CloseSignalEngine/slot; giữ TP và SOS legacy | Cô lập state và không trì hoãn đường Close đặc biệt | Cho phép bắt đầu Nhiệm vụ 8 |
| 2026-08-25 | Nhiệm vụ 8 | Khóa ranh giới Normal ↔ SOS bằng test thực và xác nhận ResetGapState không xóa TP window | Ngăn rò rỉ mẫu giữa mode mà không trì hoãn TP/SOS | Cho phép bắt đầu Nhiệm vụ 9 |
| 2026-08-25 | Nhiệm vụ 9 | Khóa bằng 7 test tích hợp ý nghĩa độc lập của `limit_max_gap`, `max_gap` và quy ước `0 = disabled` trên Open/Close | Tránh dùng ngưỡng giả vô hạn và ngăn nhầm lẫn giữa reject tại Cycle với dispatch guard | Cho phép bắt đầu Nhiệm vụ 10 |
| 2026-08-25 | Nhiệm vụ 10 | Bổ sung structured log cho các chuyển trạng thái Stable Cycle và trigger qua `ISlotLogger`; bỏ qua `Joined` và reset rỗng | Đủ dữ liệu truy vết nguyên nhân mà không spam log mỗi tick | Cho phép bắt đầu Nhiệm vụ 11 |
| 2026-08-25 | Nhiệm vụ 11 | Hoàn thành regression theo nhóm và toàn bộ 463 test; build toàn solution sạch | Xác nhận Stable Cycle không làm regression Open/Close/TP/SOS, flow, multi-slot và recovery | Cho phép bắt đầu Nhiệm vụ 12; reload config giữa Cycle vẫn cần chốt nếu muốn đổi hành vi |
| 2026-08-25 | Nhiệm vụ 12 | Chuẩn bị runbook và công cụ tổng hợp log chạy thử; chưa tạo kết luận hiệu chỉnh khi chưa có feed thật | Tách rõ chuẩn bị kỹ thuật khỏi bằng chứng vận hành thực tế | Chờ chạy trên máy giao dịch và đưa log vào analyzer |

---

## 9. Vấn đề cần chốt nếu phát sinh thay đổi

Các vấn đề sau không được tự thay đổi trong lúc code mà phải cập nhật tài liệu trước:

- Có áp dụng Stable Cycle cho SOS hay không.
- Dữ liệu thiếu reset ngay hay dùng timeout.
- Cycle Unstable bắt đầu lại từ mẫu cuối hay dùng rolling window.
- Reload config có reset Cycle đang chạy hay không.
- Dispatch thất bại có reset Cycle hay cho phép retry.
- Các tham số Open/Close có tiếp tục tách riêng hay hợp nhất.
