-- fast_leg migration for public.configs (docs/plans/fast-leg).
--
-- Sàn C "Fast Leg": nguồn giá thứ ba (terminal MT5 riêng, EA DataExporter kênh "C") CHỈ dùng để lọc lệnh theo quy tắc
-- "quiet" (tránh xung lượng): giá giữa sàn C đi >= maxMovePts trong windowMs ⇒ không mở lệnh (và hoãn Normal Close / TP
-- nếu applyToExit). Sàn C KHÔNG BAO GIỜ đặt lệnh.
--
-- Một cột JSONB riêng, KHÔNG nhét vào `sans`: cửa sổ Config dựng lại `sans` từ đầu mỗi lần lưu và sẽ xoá key lạ.
-- NULL (mặc định sau migration) hoặc "enabled": false ⇒ F tắt ⇒ hành vi giống hệt bản chưa có Fast Leg.
-- Code đọc config bằng select=*, cột thiếu được coi là NULL, nên deploy code trước hay migration trước đều an toàn.
--
-- Khoá JSON (thiếu khoá ⇒ dùng mặc định trong ngoặc):
--   enabled           bool    (false)              bật F
--   mapName           string  (Local\MT_C_Tick)    map tick của EA kênh C; KHÔNG được trùng map A/B
--   heartbeatMapName  string  (suy từ mapName: _Tick ⇒ _Trades)  heartbeat 50 ms để tách Down khỏi Stale
--   symbol            string  ("" = không kiểm)    symbol phải khớp record tick của EA
--   mode              string  (observe)            observe = chỉ ghi verdict | filter = chặn/hoãn
--   rule              string  (quiet)              chỉ hỗ trợ quiet
--   maxMovePts        number  (10)                 |move| >= ngưỡng ⇒ TooFast (point theo cột `point`)
--   windowMs          int     (200)                cửa sổ đo, 1..5000
--   maxAgeMs          int     (1000)               tick C cũ hơn ⇒ Stale
--   maxDeferMs        int     (3000)               trần hoãn một signal trong engine, 500..10000
--   failOpen          bool    (false)              tha Down/Stale cho Open
--   applyToExit       bool    (false)              hoãn cả Normal Close / TP khi TooFast (SOS luôn bỏ qua F)
-- Khoá sai kiểu / ngoài khoảng ⇒ F TẮT + log [FAST_LEG][ERROR]; phần config còn lại vẫn chạy.

begin;

alter table public.configs
    add column if not exists fast_leg jsonb null;

comment on column public.configs.fast_leg is
    'San C Fast Leg (docs/plans/fast-leg): JSON {enabled,mapName,heartbeatMapName,symbol,mode,rule,maxMovePts,windowMs,maxAgeMs,maxDeferMs,failOpen,applyToExit}. NULL = tat. Chi loc lenh, khong bao gio dat lenh.';

commit;

-- Kiểm tra sau khi chạy:
-- select hostname, fast_leg from public.configs order by hostname;

-- Mẫu bật theo thông số Long (có hiệu lực sau Reconnect). CHỈ host đã được chủ dự án duyệt:
-- update public.configs
-- set fast_leg = '{"enabled":true,"mapName":"Local\\MT_C_Tick","heartbeatMapName":"Local\\MT_C_Trades","symbol":"XAUUSD","mode":"filter","rule":"quiet","maxMovePts":5,"windowMs":200,"maxAgeMs":1000,"maxDeferMs":3000,"failOpen":false,"applyToExit":true}'::jsonb
-- where lower(hostname) = 'laptop-eoj2n95d';

-- Chỉ quan sát (không chặn):
-- update public.configs
-- set fast_leg = jsonb_set(fast_leg, '{mode}', '"observe"'::jsonb)
-- where lower(hostname) = 'laptop-eoj2n95d';

-- Tắt (kill switch, xem docs/plans/fast-leg/KILL-SWITCH.md):
-- update public.configs set fast_leg = null where lower(hostname) = 'laptop-eoj2n95d';
