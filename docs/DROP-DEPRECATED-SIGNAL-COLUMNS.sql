-- Gỡ 4 cột signal cũ khỏi public.configs.
--
-- Điều kiện tiên quyết: source code KHÔNG còn tham chiếu 4 cột này.
-- Đã hoàn tất — repository Supabase đọc bằng `select=*` và không map 4 cột này nữa,
-- ConfigRecord / ConfigLoadResult / RuntimeConfigState / IRuntimeConfigProvider đều đã bỏ chúng.
-- Test `PriceFreezeConfigMappingTests.ConfigLoadResult_NoLongerExposesDeprecatedHoldOrTickColumns`
-- khoá lại contract này.
--
-- Confirmation mode duy nhất còn lại là FIXED_SIZE theo signal_cycle_size,
-- dùng chung cho Open / Normal Close / SOS Close / TP.
-- Price-freeze dùng riêng open_price_freeze_ms và close_price_freeze_ms.

begin;

alter table public.configs drop column if exists open_hold_confirm_ms;
alter table public.configs drop column if exists close_hold_confirm_ms;
alter table public.configs drop column if exists open_max_times_tick;
alter table public.configs drop column if exists close_max_times_tick;

-- Cập nhật lại comment của hai cột stability vì mô tả cũ còn nhắc hold_confirm_ms.
comment on column public.configs.open_gap_min_stable_samples is
'[OPEN GAP STABILITY] Số mẫu Gap hợp lệ tối thiểu để Open Cycle có thể được công nhận Stable. Số lượng mẫu của một chu kỳ do signal_cycle_size quyết định; Cycle vẫn phải đạt giới hạn Dispersion/Drift.';

comment on column public.configs.close_gap_min_stable_samples is
'[NORMAL CLOSE GAP STABILITY] Số mẫu Gap hợp lệ tối thiểu để Close Cycle có thể được công nhận Stable. Số lượng mẫu của một chu kỳ do signal_cycle_size quyết định; Cycle vẫn phải đạt giới hạn Dispersion/Drift. Không áp dụng cho TP hoặc SOS.';

comment on column public.configs.signal_cycle_size is
'Số lượng mẫu của một chu kỳ signal. Dùng chung cho Open, Normal Close, SOS Close và TP (mỗi loại giữ chu kỳ riêng). Phải >= 1, nhỏ hơn thì app từ chối load config.';

comment on column public.configs.open_price_freeze_ms is
'Bảo vệ độ mới của giá khi thực thi Open: chặn lệnh nếu Bid/Ask không đổi suốt cửa sổ này. 0 = tắt kiểm tra. Độc lập hoàn toàn với hold-time.';

comment on column public.configs.close_price_freeze_ms is
'Bảo vệ độ mới của giá khi thực thi Close: chặn lệnh nếu Bid/Ask không đổi suốt cửa sổ này. 0 = tắt kiểm tra. Độc lập hoàn toàn với hold-time.';

commit;

-- Verification: 4 cột phải trả về 0 dòng.
select column_name
from information_schema.columns
where table_schema = 'public'
  and table_name = 'configs'
  and column_name in (
      'open_hold_confirm_ms',
      'close_hold_confirm_ms',
      'open_max_times_tick',
      'close_max_times_tick');
