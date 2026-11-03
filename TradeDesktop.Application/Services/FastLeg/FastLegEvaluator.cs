using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.FastLeg;

/// <summary>
/// Hàm thuần: buffer + cấu hình ⇒ verdict. Thứ tự Down → Stale → TooFast → Confirmed (docs/plans/fast-leg/README.md §4.3).
/// </summary>
public static class FastLegEvaluator
{
    // EA ghi map Trades mỗi 50 ms trong OnTimer, kể cả khi không có tick. Im lặng quá ngưỡng này = terminal/EA đã chết.
    public const int HeartbeatMaxAgeMs = 2000;

    public static FastLegVerdict Evaluate(in FastLegBufferSnapshot snapshot, long nowMs, FastLegConfig config, int point)
    {
        var heartbeatState = ResolveHeartbeatState(snapshot.Heartbeat, nowMs);
        var last = snapshot.Last;
        long? ageMs = last.HasValue ? Math.Max(0, nowMs - last.Value.TimestampMs) : null;

        var downReason = ResolveDownReason(snapshot, heartbeatState, config, point);
        if (downReason is not null)
        {
            return Build(FastLegVerdictKind.Down, 0m, 0m, 0, ageMs, last, config, downReason, heartbeatState);
        }

        var reference = snapshot.Reference ?? last;
        var movePts = 0m;
        var velocity = 0m;
        var spanMs = 0L;
        if (last.HasValue && reference.HasValue)
        {
            movePts = (last.Value.Mid - reference.Value.Mid) * point;
            spanMs = last.Value.TimestampMs - reference.Value.TimestampMs;
            // Hai mẫu trong cùng nhịp GetTickCount64 (~15.6 ms) có span = 0.
            velocity = spanMs > 0 ? movePts / spanMs * 1000m : 0m;
        }

        var kind = !ageMs.HasValue || ageMs.Value > config.MaxAgeMs
            ? FastLegVerdictKind.Stale
            : Math.Abs(movePts) >= config.MaxMovePts
                ? FastLegVerdictKind.TooFast
                : FastLegVerdictKind.Confirmed;

        return Build(kind, movePts, velocity, spanMs, ageMs, last, config, null, heartbeatState);
    }

    private static FastLegHeartbeatState ResolveHeartbeatState(FastLegHeartbeat? heartbeat, long nowMs)
    {
        if (!heartbeat.HasValue)
        {
            return FastLegHeartbeatState.NotAvailable;
        }

        return heartbeat.Value.Connected && nowMs - heartbeat.Value.TimestampMs <= HeartbeatMaxAgeMs
            ? FastLegHeartbeatState.Alive
            : FastLegHeartbeatState.Dead;
    }

    private static string? ResolveDownReason(
        in FastLegBufferSnapshot snapshot,
        FastLegHeartbeatState heartbeatState,
        FastLegConfig config,
        int point)
    {
        if (!config.IsActive)
        {
            return "disabled";
        }

        if (point <= 0)
        {
            return "bad_point";
        }

        if (!string.IsNullOrEmpty(snapshot.DownReason))
        {
            return snapshot.DownReason;
        }

        if (heartbeatState == FastLegHeartbeatState.Dead)
        {
            return snapshot.Heartbeat is { Connected: false } ? "terminal_disconnected" : "heartbeat_stale";
        }

        return null;
    }

    private static FastLegVerdict Build(
        FastLegVerdictKind kind,
        decimal movePts,
        decimal velocity,
        long spanMs,
        long? ageMs,
        FastLegTickSample? last,
        FastLegConfig config,
        string? downReason,
        FastLegHeartbeatState heartbeatState)
    {
        var isFilter = config.IsFilter;
        // Fail-open chỉ tha nhóm "không có ý kiến". TooFast là "không đồng ý" nên không bao giờ được tha.
        var blocksOpen = isFilter
            && kind != FastLegVerdictKind.Confirmed
            && !(config.FailOpen && kind is FastLegVerdictKind.Down or FastLegVerdictKind.Stale);
        // Quyết định D6: Close không bao giờ chờ C hỏng, chỉ TooFast mới hoãn.
        var blocksClose = isFilter && config.ApplyToExit && kind == FastLegVerdictKind.TooFast;

        return new FastLegVerdict(
            kind,
            movePts,
            velocity,
            spanMs,
            ageMs,
            last?.Bid,
            last?.Ask,
            config.Mode,
            blocksOpen,
            blocksClose,
            downReason,
            heartbeatState,
            config.MaxDeferMs);
    }
}
