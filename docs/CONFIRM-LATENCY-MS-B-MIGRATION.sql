-- ctrader_confirm_latency_b migration for public.configs (task R8-B).
--
-- Tách ngưỡng guard LATENCY của chân B khỏi chân A:
--   * confirm_latency       -> chân A (và chân B khi cột dưới NULL) — không đổi.
--   * ctrader_confirm_latency_b  -> CHỈ chân B. NULL (hoặc platform_b là MT) = dùng chung confirm_latency (hành vi trước task này).
--   * 0 = tắt guard latency riêng cho chân B; số âm không dùng.
--
-- Vì sao cần: MT đo EA->app (dưới 1 ms) còn cTrader đo "bao lâu rồi chưa có tick". Đo trên live 8220816
-- ngày 2026-09-21 (141 phút sau khi mở cửa): tuổi tick chân B p50 317 ms, p95 2 043 ms — với ngưỡng chung
-- 100 ms thì 76 % số lần đọc bị chặn LATENCY, tức chân cTrader gần như không vào được lệnh.
--
-- Migration KHÔNG backfill: cột mới mặc định NULL nên hành vi hiện tại giữ nguyên 100 % cho tới khi bạn
-- đặt giá trị. Chạy TRƯỚC khi deploy bản app mới (app đọc thiếu cột cũng chỉ hiểu là NULL = dùng chung).

begin;

alter table public.configs
    add column if not exists ctrader_confirm_latency_b integer;

comment on column public.configs.ctrader_confirm_latency_b is
    'Nguong guard LATENCY rieng cho san B khi platform_b = ctrader (ms). NULL hoac B la MT -> dung chung confirm_latency. 0 = tat rieng cho san B.';

commit;

-- Sau khi chạy migration, đặt giá trị cho máy đang dùng cTrader ở sàn B (giá trị đã chốt 2026-09-21: 3000 ms):
--
--   update public.configs
--   set ctrader_confirm_latency_b = 3000
--   where hostname = 'laptop-eoj2n95d';
--
-- Kiểm tra: mở app, bấm Reconnect, log [DB] phải hiện `CTraderConfirmLatencyB: 3000` (khi NULL sẽ hiện `SHARED`).

-- ĐÃ CHẠY 2026-09-21 với tên cũ `confirm_latency_ms_b`. Nếu DB của bạn đang ở tên cũ thì đổi tên (giữ nguyên dữ liệu):
--
--   alter table public.configs rename column confirm_latency_ms_b to ctrader_confirm_latency_b;
--
-- Tên có tiền tố `ctrader_` vì ngưỡng này CHỈ áp dụng khi platform_b = ctrader; cặp MT-MT luôn dùng confirm_latency.
