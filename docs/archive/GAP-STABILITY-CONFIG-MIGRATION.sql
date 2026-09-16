-- Gap Stability configuration migration for public.configs.
-- Prepared from the schema snapshot reviewed on 2026-08-25.
--
-- This migration:
--   1. Adds six Open and six Close stability settings.
--   2. Backfills existing rows with the agreed initial values.
--   3. Makes the new settings required and validates their ranges.
--   4. Does not change max_gap, limit_max_gap, current_slots, or trade state.
--   5. Does not define column defaults. New config rows must provide values explicitly.

begin;

alter table public.configs
    add column if not exists open_gap_absolute_floor integer,
    add column if not exists open_gap_relative_tolerance double precision,
    add column if not exists open_gap_mad_multiplier double precision,
    add column if not exists open_gap_min_stable_samples integer,
    add column if not exists open_gap_max_dispersion double precision,
    add column if not exists open_gap_max_drift double precision,
    add column if not exists close_gap_absolute_floor integer,
    add column if not exists close_gap_relative_tolerance double precision,
    add column if not exists close_gap_mad_multiplier double precision,
    add column if not exists close_gap_min_stable_samples integer,
    add column if not exists close_gap_max_dispersion double precision,
    add column if not exists close_gap_max_drift double precision;

-- Backfill only missing values so a partially prepared row is not overwritten.
update public.configs
set
    open_gap_absolute_floor = coalesce(open_gap_absolute_floor, 10),
    open_gap_relative_tolerance = coalesce(open_gap_relative_tolerance, 0.50),
    open_gap_mad_multiplier = coalesce(open_gap_mad_multiplier, 4.0),
    open_gap_min_stable_samples = coalesce(open_gap_min_stable_samples, 3),
    open_gap_max_dispersion = coalesce(open_gap_max_dispersion, 0.35),
    open_gap_max_drift = coalesce(open_gap_max_drift, 0.40),
    close_gap_absolute_floor = coalesce(close_gap_absolute_floor, 10),
    close_gap_relative_tolerance = coalesce(close_gap_relative_tolerance, 0.60),
    close_gap_mad_multiplier = coalesce(close_gap_mad_multiplier, 4.0),
    close_gap_min_stable_samples = coalesce(close_gap_min_stable_samples, 3),
    close_gap_max_dispersion = coalesce(close_gap_max_dispersion, 0.45),
    close_gap_max_drift = coalesce(close_gap_max_drift, 0.60)
where
    open_gap_absolute_floor is null
    or open_gap_relative_tolerance is null
    or open_gap_mad_multiplier is null
    or open_gap_min_stable_samples is null
    or open_gap_max_dispersion is null
    or open_gap_max_drift is null
    or close_gap_absolute_floor is null
    or close_gap_relative_tolerance is null
    or close_gap_mad_multiplier is null
    or close_gap_min_stable_samples is null
    or close_gap_max_dispersion is null
    or close_gap_max_drift is null;

alter table public.configs
    alter column open_gap_absolute_floor set not null,
    alter column open_gap_relative_tolerance set not null,
    alter column open_gap_mad_multiplier set not null,
    alter column open_gap_min_stable_samples set not null,
    alter column open_gap_max_dispersion set not null,
    alter column open_gap_max_drift set not null,
    alter column close_gap_absolute_floor set not null,
    alter column close_gap_relative_tolerance set not null,
    alter column close_gap_mad_multiplier set not null,
    alter column close_gap_min_stable_samples set not null,
    alter column close_gap_max_dispersion set not null,
    alter column close_gap_max_drift set not null;

-- Add constraints idempotently so the script can recover from a partially
-- completed manual migration without duplicating constraint names.
do $$
begin
    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_open_gap_absolute_floor_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_open_gap_absolute_floor_valid
            check (open_gap_absolute_floor >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_open_gap_relative_tolerance_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_open_gap_relative_tolerance_valid
            check (open_gap_relative_tolerance >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_open_gap_mad_multiplier_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_open_gap_mad_multiplier_valid
            check (open_gap_mad_multiplier >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_open_gap_min_stable_samples_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_open_gap_min_stable_samples_valid
            check (open_gap_min_stable_samples >= 3);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_open_gap_max_dispersion_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_open_gap_max_dispersion_valid
            check (open_gap_max_dispersion >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_open_gap_max_drift_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_open_gap_max_drift_valid
            check (open_gap_max_drift >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_close_gap_absolute_floor_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_close_gap_absolute_floor_valid
            check (close_gap_absolute_floor >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_close_gap_relative_tolerance_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_close_gap_relative_tolerance_valid
            check (close_gap_relative_tolerance >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_close_gap_mad_multiplier_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_close_gap_mad_multiplier_valid
            check (close_gap_mad_multiplier >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_close_gap_min_stable_samples_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_close_gap_min_stable_samples_valid
            check (close_gap_min_stable_samples >= 3);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_close_gap_max_dispersion_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_close_gap_max_dispersion_valid
            check (close_gap_max_dispersion >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'configs_close_gap_max_drift_valid'
          and conrelid = 'public.configs'::regclass
    ) then
        alter table public.configs
            add constraint configs_close_gap_max_drift_valid
            check (close_gap_max_drift >= 0);
    end if;
end
$$;

comment on column public.configs.open_gap_absolute_floor is
'[OPEN GAP STABILITY] A - Biên sai lệch tuyệt đối tối thiểu, đơn vị Gap point. Dùng trong Tolerance = max(A, R * Center, K * MAD). Giá trị lớn hơn giúp các Gap nhỏ dễ ở cùng Open Cycle hơn.';
comment on column public.configs.open_gap_relative_tolerance is
'[OPEN GAP STABILITY] R - Tỷ lệ sai lệch cho phép theo Center của Open Cycle. Dùng trong Tolerance = max(A, R * Center, K * MAD); 0.50 nghĩa là biên bằng 50% Center. Giá trị lớn hơn làm việc tách Open Cycle ít nhạy hơn.';
comment on column public.configs.open_gap_mad_multiplier is
'[OPEN GAP STABILITY] K - Hệ số nhân độ lệch trung vị MAD của Open Cycle. Dùng trong Tolerance = max(A, R * Center, K * MAD). Giá trị lớn hơn chấp nhận dao động tự nhiên rộng hơn trong cùng Cycle.';
comment on column public.configs.open_gap_min_stable_samples is
'[OPEN GAP STABILITY] Số mẫu Gap hợp lệ tối thiểu để Open Cycle có thể được công nhận Stable. Cycle vẫn phải đồng thời đủ open_hold_confirm_ms và đạt giới hạn Dispersion/Drift.';
comment on column public.configs.open_gap_max_dispersion is
'[OPEN GAP STABILITY] Độ phân tán tối đa của Open Cycle, tính bằng MAD / max(Center, 1). Ví dụ 0.35 cho phép MAD tối đa bằng 35% Center. Vượt ngưỡng thì không phát Open signal.';
comment on column public.configs.open_gap_max_drift is
'[OPEN GAP STABILITY] Độ dịch chuyển tâm tối đa của Open Cycle, tính bằng abs(LateCenter - EarlyCenter) / max(Center, 1). Ví dụ 0.40 cho phép drift tối đa 40%. Vượt ngưỡng thì không phát Open signal.';

comment on column public.configs.close_gap_absolute_floor is
'[NORMAL CLOSE GAP STABILITY] A - Biên sai lệch tuyệt đối tối thiểu, đơn vị Gap point. Dùng trong Tolerance = max(A, R * Center, K * MAD). Giá trị lớn hơn giúp các Gap nhỏ dễ ở cùng Close Cycle hơn. Không áp dụng cho TP, SOS, Manual hoặc Emergency Close.';
comment on column public.configs.close_gap_relative_tolerance is
'[NORMAL CLOSE GAP STABILITY] R - Tỷ lệ sai lệch cho phép theo Center của Close Cycle. Dùng trong Tolerance = max(A, R * Center, K * MAD); 0.60 nghĩa là biên bằng 60% Center. Giá trị lớn hơn làm việc tách Close Cycle ít nhạy hơn. Không áp dụng cho TP hoặc SOS.';
comment on column public.configs.close_gap_mad_multiplier is
'[NORMAL CLOSE GAP STABILITY] K - Hệ số nhân độ lệch trung vị MAD của Close Cycle. Dùng trong Tolerance = max(A, R * Center, K * MAD). Giá trị lớn hơn chấp nhận dao động tự nhiên rộng hơn. Không áp dụng cho TP hoặc SOS.';
comment on column public.configs.close_gap_min_stable_samples is
'[NORMAL CLOSE GAP STABILITY] Số mẫu Gap hợp lệ tối thiểu để Close Cycle có thể được công nhận Stable. Cycle vẫn phải đồng thời đủ close_hold_confirm_ms và đạt giới hạn Dispersion/Drift. Không áp dụng cho TP hoặc SOS.';
comment on column public.configs.close_gap_max_dispersion is
'[NORMAL CLOSE GAP STABILITY] Độ phân tán tối đa của Close Cycle, tính bằng MAD / max(Center, 1). Ví dụ 0.45 cho phép MAD tối đa bằng 45% Center. Vượt ngưỡng thì không phát Close Gap signal. Không áp dụng cho TP hoặc SOS.';
comment on column public.configs.close_gap_max_drift is
'[NORMAL CLOSE GAP STABILITY] Độ dịch chuyển tâm tối đa của Close Cycle, tính bằng abs(LateCenter - EarlyCenter) / max(Center, 1). Ví dụ 0.60 cho phép drift tối đa 60%. Vượt ngưỡng thì không phát Close Gap signal. Không áp dụng cho TP hoặc SOS.';

commit;

-- Verification query. Run after the migration transaction succeeds.
select
    id,
    group_name,
    hostname,
    open_hold_confirm_ms,
    open_gap_absolute_floor,
    open_gap_relative_tolerance,
    open_gap_mad_multiplier,
    open_gap_min_stable_samples,
    open_gap_max_dispersion,
    open_gap_max_drift,
    close_hold_confirm_ms,
    close_gap_absolute_floor,
    close_gap_relative_tolerance,
    close_gap_mad_multiplier,
    close_gap_min_stable_samples,
    close_gap_max_dispersion,
    close_gap_max_drift,
    limit_max_gap,
    max_gap
from public.configs
order by group_name, hostname;
