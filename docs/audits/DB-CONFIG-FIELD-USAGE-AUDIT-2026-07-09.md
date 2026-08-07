# Giải thích các thông số (field) trong bảng cấu hình `configs`

> Ngày: 2026-07-09
> Dành cho người đọc **không chuyên kỹ thuật**. Mục tiêu: hiểu **mỗi thông số trong DB
> dùng để làm gì**, và **thông số nào đang thực sự có tác dụng, thông số nào không**.
>
> Cập nhật trạng thái: 2026-08-07. Audit kỹ thuật đầy đủ và code findings nằm tại
> `docs/audits/SYSTEM-AUDIT-2026-08-07.md`.

---

## Đọc nhanh trong 30 giây

- Bảng `configs` là **bảng cài đặt** của app — mỗi máy chạy có một dòng cài đặt riêng (nhận theo tên máy).
- App đọc các thông số này để quyết định: **khi nào mở lệnh, khi nào đóng lệnh, mở tối đa bao nhiêu
  lệnh, chốt lời ở mức nào, chạy trên sàn nào**, v.v.
- **Đa số thông số đang có tác dụng thật.**
- Có **một nhóm nhỏ thông số "để đó cho có" — chỉnh cũng không thay đổi gì** (xem mục ⚠️).
- Có **vài thông số bị app ghi đè bằng giá trị cố định trong code** — nghĩa là dù bạn sửa trong DB,
  app vẫn chạy theo con số cố định (xem mục 🔒).

---

## 1. Nhóm thông số MỞ lệnh (khi nào vào lệnh)

| Thông số | Ý nghĩa dễ hiểu |
|---|---|
| `open_pts` | Mức chênh lệch giá (gap) tối thiểu để được xem là "có cơ hội mở lệnh". |
| `open_confirm_gap_pts` | Mức gap cần đạt để **xác nhận** tín hiệu mở (lọc bớt tín hiệu ảo). |
| `open_hold_confirm_ms` | Tín hiệu phải **giữ ổn định trong bao nhiêu mili-giây** thì mới tính là thật, rồi mới mở. |
| `open_max_times_tick` | Số lần giá nhảy (tick) tối đa được phép trong cửa sổ xác nhận mở. |
| `open_number_of_qualifying_times` | Phải đủ điều kiện **bao nhiêu lần** rồi app mới thực sự bấm mở. |
| `point` | Hệ số quy đổi giá thành "điểm" (pts) để tính gap cho đồng nhất giữa các sàn. |

---

## 2. Nhóm bộ lọc an toàn (chặn vào lệnh khi thị trường bất thường)

| Thông số | Ý nghĩa dễ hiểu |
|---|---|
| `confirm_latency` | Giá bị **trễ** quá mức này thì bỏ qua (dữ liệu cũ, không đáng tin). |
| `max_gap` | Gap **vượt** mức này coi là bất thường → không mở. |
| `limit_max_gap` | Trần gap tuyệt đối — chặn các cú "nhảy giá" bất thường (spike). |
| `max_spread` | Chênh lệch mua–bán (spread) **rộng** quá mức này thì không vào (phí trượt giá cao). |
| `open_price_freeze_ms` | Nếu giá **đứng im** (không nhảy) trong khoảng thời gian này thì coi là feed bị "đơ" → không mở. |
| `close_price_freeze_ms` | Tương tự trên, nhưng áp dụng cho lúc **đóng** lệnh. |

> Lưu ý: nếu để `open_price_freeze_ms` / `close_price_freeze_ms` = **0**, app **không hiểu là "tắt"**,
> mà tự lấy theo thời gian giữ xác nhận (`*_hold_confirm_ms`). Muốn tính năng này hoạt động, hãy để một số > 0.

---

## 3. Nhóm thông số ĐÓNG lệnh & chốt lời (khi nào thoát lệnh)

| Thông số | Ý nghĩa dễ hiểu |
|---|---|
| `close_pts` | Mức gap đảo chiều tối thiểu để cân nhắc đóng lệnh. |
| `close_confirm_gap_pts` | Mức gap cần đạt để **xác nhận** tín hiệu đóng. |
| `close_hold_confirm_ms` | Tín hiệu đóng phải **giữ ổn định** bao lâu mới tính là thật. |
| `close_max_times_tick` | Số lần giá nhảy tối đa trong cửa sổ xác nhận đóng. |
| `close_number_of_qualifying_times` | Phải đủ điều kiện bao nhiêu lần rồi app mới bấm đóng. |
| `close_tp_profit` | **Mức lời (take-profit)** để bắt đầu tính chuyện chốt. |
| `close_confirm_tp_profit` | Mức lời cần **xác nhận** để chốt (tránh chốt hụt do lời chớp nhoáng). |
| `close_max_tp_profit` | **Trần hợp lệ tại trigger TP** — nếu profit vượt trần thì TP bị suspend, không phải chốt cứng. `0` là tắt trần. |
| `limit_max_tp` | Chặn số lời "ảo" bất thường (nếu nhảy vọt thì không tin, tránh chốt nhầm). |
| `sos_trigger_a_open_distance_pts` | Bật SOS khi khoảng cách tuyệt đối giữa giá hiện tại và giá mở chân A đạt số point này; `0` tắt điều kiện khoảng cách. |
| `sos_trigger_after_seconds` | Bật SOS khi tuổi slot đạt số giây này; `0` tắt điều kiện thời gian. |
| `sos_close_confirm_gap_pts` / `sos_close_gap_pts` | Bộ Gap Close khi SOS bật; nếu một đầu bằng `0` thì fallback về bộ Gap thường. |

---

## 4. Nhóm thời gian & nhịp thao tác

| Thông số | Ý nghĩa dễ hiểu |
|---|---|
| `start_time_hold` / `end_time_hold` | Sau khi mở, lệnh phải **giữ tối thiểu một khoảng thời gian ngẫu nhiên** nằm giữa hai giá trị này (chống đóng quá nhanh). |
| `open_pending_time_ms` | Thời gian chờ tối đa để **lệnh mở khớp**. Quá hạn mà một bên chưa khớp thì huỷ/rollback. |
| `close_pending_time_ms` | Thời gian chờ tối đa để **lệnh đóng khớp**. |
| `delay_open_a_ms` / `delay_open_b_ms` | Độ trễ khi mở lệnh ở **sàn A** / **sàn B** (bấm 2 sàn hơi lệch nhau để khớp đều hơn). |
| `delay_close_a_ms` / `delay_close_b_ms` | Tương tự, nhưng cho lúc **đóng** lệnh. |
| `rd_start_same_action_lock_seconds` / `rd_end_same_action_lock_seconds` | Random chờ cho Open cùng chiều→Open cùng chiều và Close→Close. |
| `rd_start_post_open_lock_seconds` / `rd_end_post_open_lock_seconds` | Random riêng cho từng slot từ Open confirm đến khi slot đó được Auto Close. `0..0` là tắt. |
| `rd_start_post_close_lock_seconds` / `rd_end_post_close_lock_seconds` | Random sau Auto Close để chặn Auto Open; mỗi đầu không hợp lệ fallback 300 giây. |
| `opposite_side_lock_seconds` | Chặn Auto Open ngược chiều sau Open confirm; `<=0` fallback 300 giây. |
| `opposite_open_min_distance_pts` | Ngoài time lock, Open đảo chiều phải đạt khoảng cách giá tối thiểu trên chân A; `0` tắt price guard. |
| `schedule_sleeping` | JSONB các khoảng giờ local chặn Auto Open; không chặn Close. |

---

## 5. Nhóm giới hạn số lệnh (quota)

| Thông số | Ý nghĩa dễ hiểu |
|---|---|
| `max_total_opens` | **Tối đa bao nhiêu lệnh** được mở cùng lúc. |
| `max_buy_opens` | Tối đa bao nhiêu lệnh **Mua** cùng lúc. |
| `max_sell_opens` | Tối đa bao nhiêu lệnh **Bán** cùng lúc. |
| `max_life_time_by_second` | Khi nhiều lệnh cùng đủ điều kiện đóng, app **ưu tiên đóng lệnh đã sống lâu hơn**. (Chỉ để chọn thứ tự ưu tiên — **không** tự ép đóng lệnh khi chưa có tín hiệu.) |
| `min_profit_to_close` | Tổng lợi nhuận tối thiểu của hai chân để Auto Close khi tuổi lệnh còn dưới `max_life_time_by_second`. `0` là tắt; khi đạt max lifetime thì bỏ qua ngưỡng nhưng vẫn cần close signal. |

> Nếu để quota = 0, app tự hiểu là **1** (không bao giờ về 0).

---

## 6. Nhóm sàn, nhận diện máy & khôi phục

| Thông số | Ý nghĩa dễ hiểu |
|---|---|
| `platform_a` / `platform_b` | Sàn của **vế A** và **vế B** (MT4 hay MT5) để app bấm lệnh đúng chỗ. |
| `sans` | Chứa thông tin kết nối cửa sổ MT4/MT5 (tên vùng dữ liệu + mã cửa sổ chart/trade). |
| `hostname` | **Tên máy** — dùng để app lấy đúng dòng cài đặt của máy đang chạy. |
| `current_tick_a` / `current_tick_b` | Lưu lại mã lệnh gần nhất để **khôi phục** khi app khởi động lại. |
| `current_slots` | Lưu lại các lệnh đang mở để **khôi phục** khi mở app lại (không mất dấu lệnh). |
| `is_show_config` | Chỉ để **hiện/ẩn bảng cấu hình** trên màn hình. Không ảnh hưởng giao dịch. |

---

## 7. ⚠️ Các thông số "để đó cho có" — chỉnh KHÔNG thay đổi gì

Các cột legacy bên dưới đã được xóa khỏi DB và config pipeline ngày 2026-08-06.
`group_name` và `created_at` vẫn còn trong DB nhưng không tham gia quyết định giao dịch:

| Thông số | Vì sao không có tác dụng |
|---|---|
| `open_gap_tick` | **Đã xóa khỏi DB/source config.** |
| `close_gap_tick` | **Đã xóa khỏi DB/source config.** |
| `cool_down_gap_tick` | **Đã xóa khỏi DB/source config.** |
| `start_wait_time` | **Đã xóa khỏi DB/source config.** |
| `end_wait_time` | **Đã xóa khỏi DB/source config.** |
| `group_name` | Chỉ là **nhãn/tên nhóm** để con người dễ nhìn, app không dùng để xử lý. |
| `created_at` | Chỉ là **ngày tạo dòng cài đặt**, app không dùng để xử lý. |

---

## 8. 🔒 Quy tắc thời gian do transition matrix quản lý

Các cột legacy không còn tồn tại. Thời gian chờ được xác định trực tiếp bởi transition matrix:

| Transition | Config đang dùng | Phạm vi |
|---|---|---|
| Open cùng chiều → Open cùng chiều | `rd_start/end_same_action_lock_seconds` | Toàn Auto transition cùng loại; fallback 3..10 |
| Close → Close | `rd_start/end_same_action_lock_seconds` | Không phụ thuộc side; target vẫn phải hết post-open riêng |
| Open → Close | `rd_start/end_post_open_lock_seconds` | Riêng slot cần đóng, tính từ Open confirm |
| Auto Close → Auto Open | `rd_start/end_post_close_lock_seconds` | Chặn cả Buy và Sell; confirm anchor thường chi phối |
| Open → Open ngược chiều | `opposite_side_lock_seconds` + `opposite_open_min_distance_pts` | Phải hết time lock và pass price guard |

Các lock không cộng duration; action được phép khi tất cả điều kiện áp dụng đều pass.

---

## 9. Gợi ý

- Nếu muốn tránh nhầm lẫn, nên **ghi chú hoặc ẩn** các thông số ở mục ⚠️ (chúng dễ khiến người dùng
  tưởng đang có tác dụng nhưng thực ra không).
- Nếu muốn tự điều chỉnh **thời gian nghỉ** hoặc **thời gian chờ** qua DB thay vì số cố định, cần đội
  phát triển bật lại phần đó (hiện đang khoá cứng trong code).
