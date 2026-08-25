#!/usr/bin/env bash
set -euo pipefail

if [[ $# -eq 0 ]]; then
  echo "Usage: $0 <trade-log.log> [more-log-files...]" >&2
  exit 2
fi

awk '
function value(key,    pattern, start, text) {
    pattern = key "=[^ ]+"
    if (match($0, pattern)) {
        start = RSTART + length(key) + 1
        text = substr($0, start, RLENGTH - length(key) - 1)
        return text
    }
    return "-"
}

function add(action, metric, amount) {
    count[action SUBSEP metric] += amount
}

/\[GAP_STABILITY\]\[/ {
    action = value("action")
    if (action != "OPEN" && action != "CLOSE") next

    if ($0 ~ /\[CYCLE_STARTED\]/ || $0 ~ /\[NEW_CYCLE_CREATED\]/) add(action, "cycles", 1)
    if ($0 ~ /\[NEW_CYCLE_CREATED\]/) add(action, "delta_split", 1)
    if ($0 ~ /\[CYCLE_STABLE\]/) {
        add(action, "stable", 1)
        duration = value("duration_ms")
        if (duration != "-") {
            stable_duration[action] += duration + 0
            stable_duration_count[action]++
        }
    }
    if ($0 ~ /\[CYCLE_UNSTABLE\]/) {
        add(action, "unstable", 1)
        if ($0 ~ /Dispersion/) add(action, "dispersion", 1)
        if ($0 ~ /Drift/) add(action, "drift", 1)
    }
    if ($0 ~ /\[CYCLE_REJECTED\]/) add(action, "rejected", 1)
    if ($0 ~ /\[TRIGGER_EMITTED\]/) add(action, "triggers", 1)
}

/\[GUARD\]\[WARN\] Auto trade rejected:/ {
    action = toupper(value("action"))
    if (action == "OPEN" || action == "CLOSE") add(action, "guard_blocked", 1)
}

/SIGNAL_(OPEN|HEDGE)_(CONFIRMED|BLOCKED|FAILED|CANCELLED)/ {
    if ($0 ~ /_(CONFIRMED)/) add("OPEN", "confirmed", 1)
    else if ($0 ~ /_(BLOCKED)/) add("OPEN", "blocked", 1)
    else if ($0 ~ /_(FAILED)/) add("OPEN", "failed", 1)
    else if ($0 ~ /_(CANCELLED)/) add("OPEN", "cancelled", 1)
}

/SIGNAL_CLOSE_(CONFIRMED|BLOCKED|FAILED|CANCELLED)/ {
    if ($0 ~ /_(CONFIRMED)/) add("CLOSE", "confirmed", 1)
    else if ($0 ~ /_(BLOCKED)/) add("CLOSE", "blocked", 1)
    else if ($0 ~ /_(FAILED)/) add("CLOSE", "failed", 1)
    else if ($0 ~ /_(CANCELLED)/) add("CLOSE", "cancelled", 1)
}

END {
    print "Gap Stability trial summary"
    print "metric,OPEN,CLOSE"
    metrics[1] = "cycles"
    metrics[2] = "stable"
    metrics[3] = "unstable"
    metrics[4] = "rejected"
    metrics[5] = "delta_split"
    metrics[6] = "dispersion"
    metrics[7] = "drift"
    metrics[8] = "avg_stable_duration_ms"
    metrics[9] = "triggers"
    metrics[10] = "guard_blocked"
    metrics[11] = "confirmed"
    metrics[12] = "blocked"
    metrics[13] = "failed"
    metrics[14] = "cancelled"

    for (i = 1; i <= 14; i++) {
        metric = metrics[i]
        if (metric == "avg_stable_duration_ms") {
            open_value = stable_duration_count["OPEN"] ? sprintf("%.2f", stable_duration["OPEN"] / stable_duration_count["OPEN"]) : "0.00"
            close_value = stable_duration_count["CLOSE"] ? sprintf("%.2f", stable_duration["CLOSE"] / stable_duration_count["CLOSE"]) : "0.00"
        } else {
            open_value = count["OPEN" SUBSEP metric] + 0
            close_value = count["CLOSE" SUBSEP metric] + 0
        }
        print metric "," open_value "," close_value
    }
}
' "$@"
