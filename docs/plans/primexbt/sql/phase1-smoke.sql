-- PrimeXBT Phase 1 — smoke trên máy dev `laptop-eoj2n95d` (docs/plans/primexbt/phase-1-platform-enum.md § Hướng dẫn smoke).
--
-- App chỉ đọc dòng public.configs có hostname = Environment.MachineName (lowercase) và nút Save chỉ PATCH,
-- KHÔNG tự tạo dòng. Laptop chưa có dòng (2026-10-09) ⇒ BA tạo dòng bằng cách SAO CHÉP win-hfa1234
-- (nguồn chỉ được đọc), giữ platform_b như nguồn; B2 mới đổi sang primexbt.
-- Chạy TỪNG khối trong Supabase SQL Editor. Tắt switch Open Buy/Sell Auto trong suốt bài smoke.
--
-- | Smoke                    | Query                                                          |
-- |--------------------------|----------------------------------------------------------------|
-- | Chuẩn bị                 | BA → BA-check (tạo dòng laptop từ win-hfa1234)                  |
-- | Trước S1                 | B0 (ghi lại kết quả); mở Config nhập HWND của laptop → Save    |
-- | S1: Start 2 phút         | config như nguồn                                                |
-- | Trước S2                 | B1 → B2 (phải trả về đúng 1 dòng)                               |
-- | S2: Config → Save        | B3 (platform_b vẫn 'primexbt')                                  |
-- | S3: Start 2 phút         | khởi động lại app / Reconnect để nạp platform                   |
-- | S4                       | B4 → chạy lại app 1 phút → B5 → (B6 nếu không giữ dòng laptop)  |


-- ===== BA. Tạo dòng cho laptop-eoj2n95d bằng cách SAO CHÉP win-hfa1234 (nguồn chỉ được đọc) =====
-- Tự bỏ cột id (và cột generated) để DB tự sinh; nếu id không có default thì sinh uuid mới.
do $$
declare
    cols text;
    id_has_default boolean;
begin
    if exists (select 1 from public.configs where hostname = 'laptop-eoj2n95d') then
        raise exception 'Đã có dòng laptop-eoj2n95d — không tạo thêm';
    end if;
    if (select count(*) from public.configs where hostname = 'win-hfa1234') <> 1 then
        raise exception 'Dòng nguồn win-hfa1234 phải tồn tại đúng 1 dòng';
    end if;

    create temp table tmp_cfg_laptop on commit drop as
        select * from public.configs where hostname = 'win-hfa1234';

    update tmp_cfg_laptop set
        hostname       = 'laptop-eoj2n95d',
        current_slots  = '[]',     -- máy mới: không có slot nào để recovery
        current_tick_a = '',
        current_tick_b = '';

    select (column_default is not null or is_identity = 'YES')
      into id_has_default
    from information_schema.columns
    where table_schema = 'public' and table_name = 'configs' and column_name = 'id';

    if not id_has_default then
        update tmp_cfg_laptop set id = gen_random_uuid();
    end if;

    select string_agg(quote_ident(column_name), ', ' order by ordinal_position)
      into cols
    from information_schema.columns
    where table_schema = 'public' and table_name = 'configs'
      and is_generated = 'NEVER'
      and (column_name <> 'id' or not id_has_default);

    execute format('insert into public.configs (%1$s) select %1$s from tmp_cfg_laptop', cols);
end $$;


-- ===== BA-check. Nguồn không đổi, dòng mới đúng =====
select id, hostname, platform_a, platform_b, current_slots, current_tick_a, current_tick_b
from public.configs
where hostname in ('win-hfa1234', 'laptop-eoj2n95d')
order by hostname;
-- Kỳ vọng: 2 dòng; id khác nhau; laptop có current_slots = [] và tick rỗng; platform giống nguồn.
-- Lưu ý: sans sao chép từ VPS chứa HWND của VPS — trước S1 mở Config trên laptop, nhập HWND của laptop rồi Save.


-- ===== S1-FAST. Số test cho S1 (2 MT5 demo cùng broker): mở 1 cặp Buy ~1 s, đóng ~30 s sau, không mở cặp thứ 2 =====
-- Feed trùng broker: GapBuy = B.Bid - A.Ask ≈ -spread (-20..-30), GapSell = B.Ask - A.Bid ≈ +spread
-- ⇒ ngưỡng âm bắn ngay (Buy thắng khi cả hai chiều cùng đúng). TP KHÔNG bắn được trên feed trùng → đóng theo gap.
-- Sau khi chạy: trong app bấm Reconnect, bật switch Open Auto, rồi Start. Hai terminal phải flat trước.
update public.configs set
    open_pts                         = -100,  -- GapBuy ≈ -25 >= -100 → mở Buy
    open_confirm_gap_pts             = -100,
    close_pts                        = -100,  -- slot Buy: GapSell ≈ +25 <= 100 → đóng theo gap
    close_confirm_gap_pts            = -100,
    start_time_hold                  = 30,    -- giữ slot 30 s rồi mới xét đóng (đơn vị: giây/slot)
    end_time_hold                    = 30,
    rd_start_post_open_lock_seconds  = 0,
    rd_end_post_open_lock_seconds    = 0,
    min_profit_to_close              = 0,     -- tắt cổng |profit A| >= 100 point
    rd_start_post_close_lock_seconds = 3600,  -- chặn mở cặp thứ 2 sau khi đóng
    rd_end_post_close_lock_seconds   = 3600,
    open_price_freeze_ms             = 0,     -- tránh retry khi thị trường lặng
    close_price_freeze_ms            = 0
where hostname = 'laptop-eoj2n95d'
returning hostname, open_pts, close_pts, start_time_hold, min_profit_to_close;


-- ===== S1-RESTORE. Trả đúng giá trị trước test (chạy sau khi Stop, rồi Reconnect) =====
update public.configs set
    open_pts = 10, open_confirm_gap_pts = 5,
    close_pts = 10, close_confirm_gap_pts = 5,
    start_time_hold = 125, end_time_hold = 125,
    rd_start_post_open_lock_seconds = 10, rd_end_post_open_lock_seconds = 125,
    min_profit_to_close = 100,
    rd_start_post_close_lock_seconds = 10, rd_end_post_close_lock_seconds = 20,
    open_price_freeze_ms = 3000, close_price_freeze_ms = 2000
where hostname = 'laptop-eoj2n95d'
returning hostname, open_pts, close_pts, start_time_hold, min_profit_to_close;


-- ===== B0. Xem hiện trạng =====
select id, hostname, platform_a, platform_b,
       current_slots, current_tick_a, current_tick_b
from public.configs
where hostname = 'laptop-eoj2n95d';
-- Kỳ vọng: đúng 1 dòng; current_slots rỗng ([] / null) và không có ticket mở.


-- ===== B1. Sao lưu cả dòng sang bảng tạm =====
create table if not exists public.configs_backup_primexbt_smoke as
select now() as backed_up_at, c.* from public.configs c where false;

insert into public.configs_backup_primexbt_smoke
select now(), c.* from public.configs c
where c.hostname = 'laptop-eoj2n95d';

select backed_up_at, hostname, platform_a, platform_b
from public.configs_backup_primexbt_smoke
where hostname = 'laptop-eoj2n95d'
order by backed_up_at desc;


-- ===== B2. Đổi platform_b -> primexbt (S2) — chỉ khi không còn slot mở =====
update public.configs
set platform_b = 'primexbt'
where hostname = 'laptop-eoj2n95d'
  and platform_b is distinct from 'primexbt'
  and coalesce(current_slots::text, '[]') in ('[]', 'null')
returning id, hostname, platform_a, platform_b;
-- Phải trả về ĐÚNG 1 dòng. 0 dòng ⇒ còn slot mở hoặc đã là primexbt → dừng, xem lại B0.


-- ===== B3. Kiểm tra sau khi mở Config và bấm Save trong app (nghiệm thu 1-A1) =====
select c.platform_a, c.platform_b,
       c.sans::jsonb = b.sans::jsonb as sans_unchanged
from public.configs c
join lateral (
    select * from public.configs_backup_primexbt_smoke b
    where b.hostname = c.hostname
    order by b.backed_up_at desc
    limit 1
) b on true
where c.hostname = 'laptop-eoj2n95d';
-- Kỳ vọng: platform_b = 'primexbt' (KHÔNG bị thành 'mt5'); sans_unchanged = true.


-- ===== B4. Trả lại (S4) — platform_b và sans từ bản sao lưu mới nhất =====
update public.configs c
set platform_b = b.platform_b,
    sans       = b.sans
from (
    select * from public.configs_backup_primexbt_smoke
    where hostname = 'laptop-eoj2n95d'
    order by backed_up_at desc
    limit 1
) b
where c.hostname = b.hostname
returning c.hostname, c.platform_a, c.platform_b;
-- Phải trả về đúng 1 dòng với platform_b = giá trị cũ ở B0. Chạy lại B0 để so.


-- ===== B5. Dọn bảng tạm (sau khi S4 chạy lại app ổn) =====
-- drop table public.configs_backup_primexbt_smoke;


-- ===== B6. (Tuỳ chọn, sau khi xong smoke) xoá dòng laptop vì trước đây không có =====
-- delete from public.configs where hostname = 'laptop-eoj2n95d' returning id, hostname;
