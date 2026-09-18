-- close_rd_*_same_action_lock_seconds migration for public.configs.
--
-- Tách khoảng random same-action lock thành hai nhóm độc lập:
--   * rd_start/end_same_action_lock_seconds       -> chỉ dùng cho Open (Open→Open cùng chiều, Open→Close).
--   * close_rd_start/end_same_action_lock_seconds -> chỉ dùng cho Close→Close.
--   * Giá trị <= 0 hoặc thiếu cột -> app fallback 3..10 (giống rd_*).
--
-- Migration BACKFILL cột close bằng giá trị rd_* hiện tại của từng host, nên hành vi Close→Close
-- sau khi chạy giữ nguyên cho tới khi bạn chỉnh cột close. Không đụng cột nào khác hay trade state.
-- Chạy migration TRƯỚC khi deploy bản app mới: nếu chưa chạy, app dùng fallback 3..10 cho Close→Close.

begin;

-- Idempotent: chỉ thêm cột + backfill ở LẦN ĐẦU. Chạy lại script không ghi đè giá trị
-- close_rd_* đã được chỉnh riêng sau đó.
do $$
begin
    if not exists (
        select 1 from information_schema.columns
        where table_schema = 'public'
          and table_name = 'configs'
          and column_name = 'close_rd_start_same_action_lock_seconds'
    ) then
        alter table public.configs
            add column close_rd_start_same_action_lock_seconds integer default 3;
        alter table public.configs
            add column if not exists close_rd_end_same_action_lock_seconds integer default 10;

        update public.configs
        set close_rd_start_same_action_lock_seconds = rd_start_same_action_lock_seconds,
            close_rd_end_same_action_lock_seconds   = rd_end_same_action_lock_seconds;
    end if;
end
$$;

comment on column public.configs.close_rd_start_same_action_lock_seconds is
    'Dau duoi khoang random (giay) cho Close->Close. <= 0 -> fallback 3.';

comment on column public.configs.close_rd_end_same_action_lock_seconds is
    'Dau tren khoang random (giay) cho Close->Close. <= 0 -> fallback 10.';

commit;

-- Kiểm tra sau khi chạy:
-- select hostname,
--        rd_start_same_action_lock_seconds, rd_end_same_action_lock_seconds,
--        close_rd_start_same_action_lock_seconds, close_rd_end_same_action_lock_seconds
-- from public.configs
-- order by hostname;
