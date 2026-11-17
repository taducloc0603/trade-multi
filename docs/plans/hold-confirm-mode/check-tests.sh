#!/usr/bin/env bash
# Cổng test chung (G-common) của hold-confirm-mode: chạy toàn bộ suite, so tập test fail với
# baseline-failures.txt (đo ở Phase 0 trên b154bd7). Exit 0 chỉ khi new_fail = 0.
# Dùng: bash docs/plans/hold-confirm-mode/check-tests.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/../../.." && pwd)"
plan_dir="$root/docs/plans/hold-confirm-mode"
out_dir="${TEMP:-/tmp}/hold-confirm-check"
mkdir -p "$out_dir"
rm -f "$out_dir/run.trx"

export DOTNET_ROLL_FORWARD=Major
dotnet build "$root/TradeDesktop.Tests/TradeDesktop.Tests.csproj" -c Debug >/dev/null
dotnet test "$root/TradeDesktop.Tests/TradeDesktop.Tests.csproj" --no-build \
  --logger "trx;LogFileName=run.trx" --results-directory "$out_dir" | grep -E "Passed!|Failed!" || true

grep -o '<UnitTestResult [^>]*outcome="Failed"[^>]*>' "$out_dir/run.trx" \
  | grep -o 'testName="[^"]*"' | sed 's/testName="//;s/"$//' | sort > "$out_dir/failures.txt" || true

new_fail="$(comm -13 "$plan_dir/baseline-failures.txt" "$out_dir/failures.txt")"
fixed="$(comm -23 "$plan_dir/baseline-failures.txt" "$out_dir/failures.txt")"

echo "baseline_fail=$(wc -l < "$plan_dir/baseline-failures.txt") current_fail=$(wc -l < "$out_dir/failures.txt")"
if [ -n "$fixed" ]; then
  echo "fixed (baseline fail nay pass):"
  echo "$fixed"
fi

if [ -n "$new_fail" ]; then
  echo "new_fail:"
  echo "$new_fail"
  exit 1
fi

echo "new_fail=0"
