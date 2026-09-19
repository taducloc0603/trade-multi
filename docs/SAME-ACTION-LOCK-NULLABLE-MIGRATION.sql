-- Cho phép NULL ở rd_start/end_same_action_lock_seconds (nhóm Open) của public.configs.
--
-- Quy ước (từ bản app tách Open/Close same-action lock):
--   * start HOẶC end của một nhóm là NULL -> TẮT nhóm đó (không chờ same-action).
--       - Nhóm Open  (rd_*)       : bỏ Open→Open cùng chiều và Open→Close.
--       - Nhóm Close (close_rd_*) : bỏ Close→Close.
--   * <= 0 vẫn fallback 3..10, KHÔNG phải tắt.
--
-- close_rd_* đã nullable từ docs/CLOSE-SAME-ACTION-LOCK-MIGRATION.sql, không cần đổi.
-- Migration này chỉ bỏ ràng buộc NOT NULL: không đổi giá trị hiện có, không đổi default.
-- Chạy lại nhiều lần vẫn an toàn.

begin;

alter table public.configs
    alter column rd_start_same_action_lock_seconds drop not null;

alter table public.configs
    alter column rd_end_same_action_lock_seconds drop not null;

commit;

-- Kiểm tra sau khi chạy (cả 4 dòng phải là YES):
-- select column_name, is_nullable
-- from information_schema.columns
-- where table_schema = 'public' and table_name = 'configs'
--   and column_name like '%same_action_lock_seconds';

-- Tắt nhóm Open cho một host:
-- update public.configs set rd_start_same_action_lock_seconds = null where hostname = '<host>';
