-- ============================================================================
--  Kiểm tra điều kiện tiên quyết cho NHÁNH TIME (confirmation_mode =
--  TIME_AND_MIN_SAMPLES) trước khi cho app chạy tiền thật.
--
--  Bối cảnh: từ commit 2ce1bb1 (31/08/2026) tới trước nhánh này, KHÔNG code nào
--  đọc/ghi 4 cột hold/max-tick. Vì vậy giá trị trên DB rất có thể đang là 0
--  hoặc NULL. Nhánh TIME dùng chúng làm điều kiện quyết định signal:
--
--    * open_hold_confirm_ms  = 0 -> Open Cycle chốt NGAY khi đủ
--                                   open_gap_min_stable_samples mẫu (tối thiểu 3).
--    * close_hold_confirm_ms = 0 -> Normal Close / SOS Close tương tự, và TP
--                                   trigger NGAY tick đầu tiên đạt close_tp_profit.
--
--  Cả hai trường hợp đều làm bản TIME vào/ra lệnh dày hơn hẳn nhánh TICK và
--  khiến phép so sánh A/B mất ý nghĩa.
--
--  Script này CHỈ ĐỌC. Phần UPDATE ở cuối là mẫu, đã comment sẵn — tự bỏ comment
--  và thay giá trị theo ý bạn.
--
--  ⚠️ KHÔNG chạy docs/DROP-DEPRECATED-SIGNAL-COLUMNS.sql trên nhánh này.
-- ============================================================================


-- 1) Bốn cột phải TỒN TẠI. Kỳ vọng: trả về đúng 4 dòng.
--    Nếu trả về ít hơn 4 -> script DROP đã bị chạy, phải ADD COLUMN lại
--    trước khi làm bất cứ việc gì khác.
select column_name, data_type, column_default, is_nullable
from information_schema.columns
where table_schema = 'public'
  and table_name = 'configs'
  and column_name in (
      'open_hold_confirm_ms',
      'close_hold_confirm_ms',
      'open_max_times_tick',
      'close_max_times_tick')
order by column_name;


-- 2) Giá trị hiện tại theo từng host. Kỳ vọng: hai cột hold đều > 0.
--    Cột hold_status nêu thẳng hệ quả nếu đang bằng 0/NULL.
select
    machine_host_name,
    open_hold_confirm_ms,
    close_hold_confirm_ms,
    open_max_times_tick,
    close_max_times_tick,
    signal_cycle_size,            -- chỉ để đối chiếu với nhánh TICK, không quyết định gì
    open_gap_min_stable_samples,
    close_gap_min_stable_samples,
    case
        when coalesce(open_hold_confirm_ms, 0) = 0
         and coalesce(close_hold_confirm_ms, 0) = 0
            then 'NGUY HIEM: ca hai hold = 0 -> Open chot ngay khi du min_stable_samples, TP trigger ngay tick dau du loi'
        when coalesce(open_hold_confirm_ms, 0) = 0
            then 'CANH BAO: open_hold_confirm_ms = 0 -> Open chot ngay khi du open_gap_min_stable_samples'
        when coalesce(close_hold_confirm_ms, 0) = 0
            then 'CANH BAO: close_hold_confirm_ms = 0 -> TP trigger ngay tick dau dat close_tp_profit'
        else 'OK'
    end as hold_status
from public.configs
order by machine_host_name;


-- 3) Chỉ những host đang ở trạng thái nguy hiểm (rỗng = tất cả đều OK).
select machine_host_name, open_hold_confirm_ms, close_hold_confirm_ms
from public.configs
where coalesce(open_hold_confirm_ms, 0) = 0
   or coalesce(close_hold_confirm_ms, 0) = 0
order by machine_host_name;


-- ----------------------------------------------------------------------------
-- 4) MẪU cập nhật — bỏ comment và thay giá trị trước khi chạy.
--
--    Gợi ý đặt giá trị khởi điểm: chọn hold sao cho số tick gom được trong cửa
--    sổ xấp xỉ signal_cycle_size của nhánh TICK, để hai nhánh so sánh công bằng.
--    Snapshot về khoảng mỗi 50ms, nên signal_cycle_size = 10 ~ hold ≈ 500ms.
--    Đo lại bằng log thực tế: khoảng cách STARTED -> COMPLETED trong [OPEN_CYCLE].
--
--    max_times_tick = 0 nghĩa là KHÔNG giới hạn độ dài chu kỳ. Lưu ý guard này
--    chỉ chạy SAU khi mẫu cuối đã đạt ngưỡng (đúng như logic gốc), nên nó không
--    chặn được chu kỳ phình khi gap không bao giờ chạm ngưỡng.
-- ----------------------------------------------------------------------------

-- update public.configs
-- set open_hold_confirm_ms  = 500,
--     close_hold_confirm_ms = 500,
--     open_max_times_tick   = 0,
--     close_max_times_tick  = 0
-- where machine_host_name = '<hostname-cua-ban-viet-thuong>';


-- 5) Cập nhật lại comment cột cho đúng nhánh TIME (an toàn, chạy được nhiều lần).
comment on column public.configs.open_hold_confirm_ms is
'[NHANH TIME] Thoi gian giu toi thieu cua mot Open Cycle (ms). Cycle chi Stable khi dat CA cot nay lan open_gap_min_stable_samples, roi moi xet Dispersion/Drift. 0 = tat dieu kien thoi gian.';

comment on column public.configs.close_hold_confirm_ms is
'[NHANH TIME] Thoi gian giu toi thieu cua Close Cycle (ms), dung chung cho Normal Close, SOS Close va TP. Rieng TP chot CHI theo cot nay (khong co so mau dich). 0 = tat dieu kien thoi gian.';

comment on column public.configs.open_max_times_tick is
'[NHANH TIME] Chan Open Cycle dai qua N mau: kiem tra SAU khi Cycle da Stable va mau cuoi da dat open_pts, vuot thi reset Cycle. 0 = tat.';

comment on column public.configs.close_max_times_tick is
'[NHANH TIME] Nhu open_max_times_tick nhung cho Normal Close, SOS Close va TP. 0 = tat.';

comment on column public.configs.signal_cycle_size is
'KHONG tham gia quyet dinh signal tren nhanh TIME. Van duoc load, validate (>= 1) va ghi kem log de doi chieu voi nhanh TICK (FIXED_SIZE).';
