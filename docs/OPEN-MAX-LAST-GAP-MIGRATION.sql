-- open_max_last_gap_pts migration for public.configs (nhánh TIME).
--
-- Thêm trần cho GAP CUỐI của một Open Cycle.
--   * So sánh giữ nguyên dấu, đối xứng: Buy cần lastGap < C, Sell cần lastGap > -C.
--   * NULL = tắt gate (mặc định sau migration -> hành vi Open không đổi).
--   * 0 và số âm là giá trị HỢP LỆ, không bị clamp (giống 4 cột ngưỡng gap thường).
--   * Vi phạm -> engine reset Cycle ngay, mở Cycle mới. KHÔNG tạo open/close nào (Rule E).
--
-- Migration này KHÔNG backfill và KHÔNG đặt default: cột để NULL cho tới khi bạn set giá trị
-- cho từng host. Nó cũng không đụng bất kỳ cột nào khác hay trade state.

begin;

alter table public.configs
    add column if not exists open_max_last_gap_pts integer;

comment on column public.configs.open_max_last_gap_pts is
    'Tran cho GAP CUOI cua Open Cycle (signed, doi xung). Buy: lastGap < C. Sell: lastGap > -C. NULL = tat gate; 0 va so am van hieu luc. Vi pham -> reset Cycle.';

commit;

-- Kiểm tra sau khi chạy:
-- select hostname, open_pts, open_confirm_gap_pts, open_max_times_tick, open_max_last_gap_pts
-- from public.configs
-- order by hostname;

-- Mẫu bật gate cho một host (đặt C lớn hơn open_pts, nếu không mọi trigger sẽ bị chặn sạch):
-- update public.configs
-- set open_max_last_gap_pts = 150
-- where hostname = 'win-vps-01';

-- Tắt lại gate:
-- update public.configs
-- set open_max_last_gap_pts = null
-- where hostname = 'win-vps-01';
