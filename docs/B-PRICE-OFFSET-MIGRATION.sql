-- b_bid_offset_pts / b_ask_offset_pts migration for public.configs.
--
-- Offset (POINT, có thể âm) cộng vào giá sàn B sau khi đổi sang point, RIÊNG cho Bid và Ask:
--   B.Bid' = (B.Bid * point + b_bid_offset_pts) / point
--   B.Ask' = (B.Ask * point + b_ask_offset_pts) / point
--   * Áp ở reader cho MỌI nguồn B (mt4/mt5 MMF, cTrader FIX, PrimeXBT) trước mọi xử lý:
--     gap/signal/router, profit chân B (TP, priority close), slippage chân B, UI, log đều thấy giá đã dịch.
--   * Spread B giữ giá trị thật của nguồn (guard spread không đổi).
--   * 0 = không dịch (default sau migration -> hành vi không đổi). Số âm hợp lệ, KHÔNG clamp.
--
-- Migration này không đụng cột nào khác hay trade state. Code đọc config bằng select=*, cột thiếu được
-- coi là 0, nên deploy code trước rồi chạy migration sau vẫn an toàn.

begin;

alter table public.configs
    add column if not exists b_bid_offset_pts integer not null default 0;

alter table public.configs
    add column if not exists b_ask_offset_pts integer not null default 0;

comment on column public.configs.b_bid_offset_pts is
    'Offset (point, signed) cong vao gia Bid san B sau khi doi sang point. 0 = khong dich. Anh huong gap, profit chan B, slippage.';

comment on column public.configs.b_ask_offset_pts is
    'Offset (point, signed) cong vao gia Ask san B sau khi doi sang point. 0 = khong dich. Anh huong gap, profit chan B, slippage.';

commit;

-- Kiểm tra sau khi chạy:
-- select hostname, point, b_bid_offset_pts, b_ask_offset_pts
-- from public.configs
-- order by hostname;

-- Mẫu đặt offset cho một host (có hiệu lực sau Reconnect):
-- update public.configs
-- set b_bid_offset_pts = 20, b_ask_offset_pts = -10
-- where hostname = 'win-vps-01';

-- Tắt lại (không dịch):
-- update public.configs
-- set b_bid_offset_pts = 0, b_ask_offset_pts = 0
-- where hostname = 'win-vps-01';
