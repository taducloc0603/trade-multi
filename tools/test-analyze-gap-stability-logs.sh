#!/usr/bin/env bash
set -euo pipefail
tool_dir="$(cd "$(dirname "$0")" && pwd)"
fixture="$(mktemp)"
trap 'rm -f "$fixture"' EXIT
printf '%s\n' \
'[GAP_STABILITY][CYCLE_STABLE] cycle_id=c1 signal_id=- action=OPEN side=BUY slot_id=- config_id=cfg1 symbol=XAUUSD!|XAUUSD gaps="5|10|20" total_sample_count=3 duration_ms=2000 dispersion=0.2 drift=0.3 result=STABLE absolute_floor=10 relative_tolerance=0.5 mad_multiplier=4 min_stable_samples=3 max_dispersion=0.35 max_drift=0.4 hold_confirm_ms=2000 limit_max_gap=40 max_gap=10000' \
'[GAP_STABILITY][TRIGGER_EMITTED] cycle_id=c1 signal_id=s1 action=OPEN side=BUY slot_id=- config_id=cfg1 symbol=XAUUSD!|XAUUSD gaps="5|10|20" total_sample_count=3 duration_ms=2000 dispersion=0.2 drift=0.3 result=TRIGGERED absolute_floor=10 relative_tolerance=0.5 mad_multiplier=4 min_stable_samples=3 max_dispersion=0.35 max_drift=0.4 hold_confirm_ms=2000 limit_max_gap=40 max_gap=10000' \
'[GAP_STABILITY][GUARD] cycle_id=c1 signal_id=s1 action=OPEN side=BUY slot_id=- result=PASS reason=NONE' \
'[GAP_STABILITY][OUTCOME] cycle_id=c1 signal_id=s1 action=OPEN side=BUY slot_id=7 outcome=CONFIRMED reason=NONE' \
'[GAP_STABILITY_RAW][CONFIG] action=CLOSE side=SELL config_id=cfg2 symbol=BTCUSD|BTCUSD absolute_floor=10 relative_tolerance=0.6 mad_multiplier=4 min_stable_samples=3 max_dispersion=0.45 max_drift=0.6 hold_confirm_ms=2000 limit_max_gap=0 max_gap=0' \
'[GAP_STABILITY_RAW][CYCLE_COMPLETED] timestamp=2026-08-26T00:00:03Z cycle_id=c2 action=CLOSE side=SELL gap_type=GAP_BUY slot_ids="8" result=UNSTABLE status_before=Unstable next_status=Collecting duration_ms=3000 total_sample_count=3 logged_sample_count=3 gaps_order=oldest_to_newest gaps_unit=point gaps="-100|-150|-200" gaps_truncated=false center=150 mad=50 tolerance=90 new_gap=-200 delta=50 dispersion=0.5 drift=0.7 config_id=cfg2 symbol=BTCUSD|BTCUSD reason="Dispersion Drift"' > "$fixture"
report="$(bash "$tool_dir/analyze-gap-stability-logs.sh" "$fixture")"
for expected in 'action,OPEN' 'config_id,cfg1' 'symbol,XAUUSD!|XAUUSD' 'limit_max_gap,40' 'max_gap,10000' 'cycles,1' 'stable,1' 'triggers,1' 'guard_pass,1' 'confirmed,1' 'observed_gaps,3' 'gap_0_5,1' 'gap_6_10,1' 'gap_11_20,1' 'action,CLOSE' 'config_id,cfg2' 'unstable,1' 'unstable_dispersion_reason,1' 'unstable_drift_reason,1' 'drift_over_060,1'; do
  if ! grep -Fq "$expected" <<< "$report"; then echo "Missing: $expected" >&2; echo "$report" >&2; exit 1; fi
done
echo "Analyzer fixture test passed"
