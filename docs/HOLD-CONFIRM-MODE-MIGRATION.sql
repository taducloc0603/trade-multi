-- hold_confirm_mode / open_hold_confirm_ticks / close_hold_confirm_ticks migration for public.configs.
-- USER TỰ CHẠY trên Supabase (docs/plans/hold-confirm-mode).
--
-- hold_confirm_mode chọn cách đo "thời gian giữ" của Cycle gap, MỘT cột cho cả Open và Close:
--   * 'ms'    = như cũ (mặc định): đủ min_stable_samples mẫu VÀ đủ open/close_hold_confirm_ms.
--   * 'ticks' = đủ min_stable_samples mẫu VÀ đủ open/close_hold_confirm_ticks tick của CHÂN CHẬM HƠN;
--               open_hold_confirm_ms và close_hold_confirm_ms bị bỏ qua cho Open / Normal Close / SOS Close.
--               TP VẪN đo theo close_hold_confirm_ms.
-- Tick của một chân = snapshot có (bid, ask) của chân đó khác snapshot trước (reader poll 50 ms, giá đứng
-- yên không đếm). Tick chân chậm = min(tick A, tick B) trong Cycle; mẫu đầu Cycle (lúc vượt ngưỡng) không đếm.
-- Chờ quá 200 mẫu (~10 s) mà chân chậm chưa đủ tick ⇒ Cycle bị bỏ, bắt đầu lại từ mẫu hiện tại.
-- Dùng 'ticks' thì nên đặt confirm_gap_pts = open_pts và close_confirm_gap_pts = close_pts (app cảnh báo nếu khác).
--
-- Giá trị khác ĐÚNG 'ms' / 'ticks' (vd 'tick', 'MS', 'ms ') hoặc ticks âm ⇒ app TỪ CHỐI config
-- ([CONFIG][ERROR], không chạy signal khi khởi động; lúc Reconnect giữ config cũ).
--
-- Thứ tự triển khai: chạy migration ⇒ nâng app lên bản có hold_confirm_mode ⇒ RỒI MỚI đổi mode.
--   * App mới chạy được khi CHƯA có các cột này (null/thiếu cột = 'ms', 0, 0 = hành vi cũ).
--   * Bản app cũ bỏ qua các cột này: đặt 'ticks' cho host còn chạy bản cũ thì bản đó LẶNG LẼ chạy theo ms.
-- Đổi mode có hiệu lực sau Reconnect (hoặc khởi động lại app).

begin;

alter table public.configs
    add column if not exists hold_confirm_mode text not null default 'ms';

alter table public.configs
    add column if not exists open_hold_confirm_ticks integer not null default 0;

alter table public.configs
    add column if not exists close_hold_confirm_ticks integer not null default 0;

alter table public.configs
    drop constraint if exists configs_hold_confirm_mode_check;

alter table public.configs
    add constraint configs_hold_confirm_mode_check check (hold_confirm_mode in ('ms', 'ticks'));

alter table public.configs
    drop constraint if exists configs_hold_confirm_ticks_check;

alter table public.configs
    add constraint configs_hold_confirm_ticks_check check (open_hold_confirm_ticks >= 0 and close_hold_confirm_ticks >= 0);

comment on column public.configs.hold_confirm_mode is
    'ms = giu Cycle theo *_hold_confirm_ms (mac dinh); ticks = giu theo *_hold_confirm_ticks tick chan cham (min A,B). Ap cho Open, Normal Close, SOS Close; TP luon theo ms.';

comment on column public.configs.open_hold_confirm_ticks is
    'So tick chan cham hon (min A,B) Open Cycle phai giu khi hold_confirm_mode = ticks. 0 = chi con min_stable_samples.';

comment on column public.configs.close_hold_confirm_ticks is
    'So tick chan cham hon (min A,B) Normal/SOS Close Cycle phai giu khi hold_confirm_mode = ticks. 0 = chi con min_stable_samples.';

commit;

-- Kiểm tra sau khi chạy:
-- select hostname, hold_confirm_mode, open_hold_confirm_ms, open_hold_confirm_ticks,
--        close_hold_confirm_ms, close_hold_confirm_ticks, confirm_gap_pts, open_pts, close_confirm_gap_pts, close_pts
-- from public.configs
-- order by hostname;

-- Mẫu bật mode ticks cho một host (sau khi host đã chạy bản app mới; có hiệu lực sau Reconnect):
-- update public.configs
-- set hold_confirm_mode = 'ticks', open_hold_confirm_ticks = 1, close_hold_confirm_ticks = 1
-- where hostname = 'laptop-eoj2n95d';

-- Tắt lại (về hành vi cũ):
-- update public.configs
-- set hold_confirm_mode = 'ms', open_hold_confirm_ticks = 0, close_hold_confirm_ticks = 0
-- where hostname = 'laptop-eoj2n95d';
