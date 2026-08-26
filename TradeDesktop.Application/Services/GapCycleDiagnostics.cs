using System.Globalization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public static class GapCycleDiagnostics
{
    // Diagnostics tạm thời phục vụ hiệu chỉnh DB. Không tham gia quyết định giao dịch
    // và có thể tắt/gỡ tại một nơi sau giai đoạn thu thập dữ liệu.
    private const bool EnableGapStabilityDiagnostics = true;
    private const double MinimumResetDurationToLogMs = 250d;
    private const int MaxLoggedGaps = 256;
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(60);
    private static readonly object SummarySync = new();
    private static readonly Dictionary<string, SummaryBucket> Summaries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> RawPolicySignatures = new(StringComparer.Ordinal);

    private sealed class SummaryBucket(
        DateTime startedAtUtc,
        string action,
        string side,
        PolicyContext? policy)
    {
        public DateTime StartedAtUtc { get; } = startedAtUtc;
        public string Action { get; } = action;
        public string Side { get; } = side;
        public PolicyContext? Policy { get; } = policy;
        public int CycleEvents { get; set; }
        public int Stable { get; set; }
        public int Unstable { get; set; }
        public int ResetConfirm { get; set; }
        public int DeltaSplit { get; set; }
        public int Rejected { get; set; }
        public int ResetMissingData { get; set; }
        public int ResetTimestamp { get; set; }
        public int ResetExplicit { get; set; }
        public int? GapMin { get; set; }
        public int? GapMax { get; set; }
        public double DurationTotalMs { get; set; }
        public double DurationMaxMs { get; set; }
        public double DispersionMax { get; set; }
        public double DriftMax { get; set; }
    }

    internal sealed record PolicyContext(
        GapStabilityConfig Stability,
        int HoldConfirmMs,
        int LimitMaxGap,
        int MaxGap,
        string ConfigId,
        string Symbol);

    internal static void LogTransition(
        ISlotLogger? logger,
        string action,
        string side,
        int? slotId,
        GapCycleUpdateResult update,
        int? newGap,
        int minimumSamplesToLog = 3,
        PolicyContext? policy = null)
    {
        if (!EnableGapStabilityDiagnostics
            || logger is null
            || update.Transition is GapCycleTransition.None
                or GapCycleTransition.Joined
                or GapCycleTransition.Started)
        {
            return;
        }

        var isReset = update.Transition is GapCycleTransition.ResetMissingData
            or GapCycleTransition.ResetConfirmNotSatisfied
            or GapCycleTransition.ResetTimestamp
            or GapCycleTransition.ResetExplicitly;
        if (isReset
            && (update.CompletedCycle is null
                || update.CompletedCycle.SampleCount < Math.Max(3, minimumSamplesToLog)
                || update.CompletedCycle.DurationMs < MinimumResetDurationToLogMs))
        {
            // Reset Cycle rỗng/ngắn là nhiễu bình thường quanh Confirm threshold.
            return;
        }

        var eventName = update.Transition switch
        {
            GapCycleTransition.BecameStable => "CYCLE_STABLE",
            GapCycleTransition.NewCycle
                or GapCycleTransition.BecameUnstable
                or GapCycleTransition.Rejected
                or GapCycleTransition.ResetMissingData
                or GapCycleTransition.ResetConfirmNotSatisfied
                or GapCycleTransition.ResetTimestamp
                or GapCycleTransition.ResetExplicitly => "CYCLE_COMPLETED",
            _ => update.Transition.ToString().ToUpperInvariant()
        };
        var result = update.Transition switch
        {
            GapCycleTransition.BecameStable => "STABLE",
            GapCycleTransition.NewCycle => "DELTA_SPLIT",
            GapCycleTransition.BecameUnstable => "UNSTABLE",
            GapCycleTransition.Rejected => "REJECTED",
            GapCycleTransition.ResetMissingData => "RESET_MISSING_DATA",
            GapCycleTransition.ResetConfirmNotSatisfied => "RESET_CONFIRM",
            GapCycleTransition.ResetTimestamp => "RESET_TIMESTAMP",
            GapCycleTransition.ResetExplicitly => "RESET_EXPLICIT",
            _ => update.Transition.ToString().ToUpperInvariant()
        };

        var snapshot = update.CompletedCycle ?? update.CurrentCycle;
        var reason = update.CurrentCycle.Reason;
        var delta = newGap.HasValue && snapshot.Center.HasValue
            ? GapStabilityCalculator.CalculateDelta(newGap.Value, snapshot.Center.Value)
            : (double?)null;

        if (update.Transition == GapCycleTransition.BecameStable)
        {
            RecordSummary(
                logger,
                action,
                side,
                snapshot,
                newGap,
                result,
                policy);
            logger.Log(Format(
                eventName,
                action,
                side,
                slotId,
                snapshot,
                newGap,
                delta,
                reason,
                result,
                policy,
                nextStatus: update.CurrentCycle.Status));
            return;
        }

        var rawMessage = FormatRawCycle(
            action,
            side,
            slotId,
            snapshot,
            newGap,
            delta,
            reason,
            result,
            policy,
            update.CurrentCycle.Status);
        if (logger is IGapStabilityRawLogger rawLogger)
        {
            LogRawPolicyIfNeeded(rawLogger, action, side, policy);
            rawLogger.LogGapStabilityRaw(rawMessage);
        }
        else
        {
            // Logger dùng trong test hoặc host cũ vẫn nhận được diagnostics đầy đủ.
            logger.Log(rawMessage);
        }

        RecordSummary(
            logger,
            action,
            side,
            snapshot,
            newGap,
            result,
            policy);
    }

    internal static void LogTrigger(
        ISlotLogger? logger,
        string action,
        string side,
        int? slotId,
        GapCycleSnapshot cycle,
        int newGap,
        string reason,
        PolicyContext? policy = null,
        string signalId = "")
    {
        if (!EnableGapStabilityDiagnostics || logger is null)
        {
            return;
        }

        var delta = cycle.Center.HasValue
            ? GapStabilityCalculator.CalculateDelta(newGap, cycle.Center.Value)
            : (double?)null;
        logger.Log(Format(
            "TRIGGER_EMITTED",
            action,
            side,
            slotId,
            cycle,
            newGap,
            delta,
            reason,
            "TRIGGERED",
            policy,
            signalId,
            nextStatus: cycle.Status));
    }

    private static string Format(
        string eventName,
        string action,
        string side,
        int? slotId,
        GapCycleSnapshot cycle,
        int? newGap,
        double? delta,
        string reason,
        string result,
        PolicyContext? policy,
        string signalId = "",
        GapCycleStatus? nextStatus = null)
    {
        var loggedGaps = cycle.Gaps.Count <= MaxLoggedGaps
            ? cycle.Gaps
            : cycle.Gaps.Skip(cycle.Gaps.Count - MaxLoggedGaps).ToArray();
        var gaps = string.Join('|', loggedGaps.Select(gap => Value(gap)));
        var truncated = cycle.Gaps.Count > loggedGaps.Count;

        return
        $"[GAP_STABILITY][{eventName}] " +
        $"cycle_id={Text(cycle.CycleId)} signal_id={Text(signalId)} " +
        $"action={action} side={side} slot_id={Value(slotId)} " +
        $"config_id={Text(policy?.ConfigId)} symbol={Text(policy?.Symbol)} " +
        $"started_at={Timestamp(cycle.StartedAtUtc)} ended_at={Timestamp(cycle.LastTickUtc)} " +
        $"gaps=\"{gaps}\" gaps_truncated={truncated.ToString().ToLowerInvariant()} " +
        $"total_sample_count={cycle.SampleCount} logged_sample_count={loggedGaps.Count} " +
        $"sample_count={cycle.SampleCount} duration_ms={Number(cycle.DurationMs)} " +
        $"center={Number(cycle.Center)} mad={Number(cycle.Mad)} tolerance={Number(cycle.Tolerance)} " +
        $"new_gap={Value(newGap)} delta={Number(delta)} dispersion={Number(cycle.Dispersion)} " +
        $"drift={Number(cycle.Drift)} status={cycle.Status} " +
        $"next_status={nextStatus?.ToString() ?? cycle.Status.ToString()} result={result} " +
        $"absolute_floor={Value(policy?.Stability.AbsoluteFloor)} " +
        $"relative_tolerance={Number(policy?.Stability.RelativeTolerance)} " +
        $"mad_multiplier={Number(policy?.Stability.MadMultiplier)} " +
        $"min_stable_samples={Value(policy?.Stability.MinStableSamples)} " +
        $"max_dispersion={Number(policy?.Stability.MaxDispersion)} " +
        $"max_drift={Number(policy?.Stability.MaxDrift)} " +
        $"hold_confirm_ms={Value(policy?.HoldConfirmMs)} " +
        $"limit_max_gap={Value(policy?.LimitMaxGap)} max_gap={Value(policy?.MaxGap)} " +
        $"reason=\"{Escape(reason)}\"";
    }

    private static string FormatRawCycle(
        string action,
        string side,
        int? slotId,
        GapCycleSnapshot cycle,
        int? newGap,
        double? delta,
        string reason,
        string result,
        PolicyContext? policy,
        GapCycleStatus nextStatus)
    {
        var loggedGaps = cycle.Gaps.Count <= MaxLoggedGaps
            ? cycle.Gaps
            : cycle.Gaps.Skip(cycle.Gaps.Count - MaxLoggedGaps).ToArray();
        var gaps = string.Join('|', loggedGaps.Select(gap => Value(gap)));
        var truncated = cycle.Gaps.Count > loggedGaps.Count;

        return
            "[GAP_STABILITY_RAW][CYCLE_COMPLETED] " +
            $"timestamp={Timestamp(cycle.LastTickUtc)} cycle_id={Text(cycle.CycleId)} " +
            $"action={action} side={side} gap_type={ResolveGapType(action, side)} " +
            $"slot_ids=\"{Value(slotId)}\" result={result} " +
            $"status_before={cycle.Status} next_status={nextStatus} " +
            $"duration_ms={Number(cycle.DurationMs)} total_sample_count={cycle.SampleCount} " +
            $"logged_sample_count={loggedGaps.Count} gaps_order=oldest_to_newest gaps_unit=point " +
            $"gaps=\"{gaps}\" gaps_truncated={truncated.ToString().ToLowerInvariant()} " +
            $"center={Number(cycle.Center)} mad={Number(cycle.Mad)} tolerance={Number(cycle.Tolerance)} " +
            $"new_gap={Value(newGap)} delta={Number(delta)} dispersion={Number(cycle.Dispersion)} " +
            $"drift={Number(cycle.Drift)} config_id={Text(policy?.ConfigId)} " +
            $"symbol={Text(policy?.Symbol)} reason=\"{Escape(reason)}\"";
    }

    private static void LogRawPolicyIfNeeded(
        IGapStabilityRawLogger rawLogger,
        string action,
        string side,
        PolicyContext? policy)
    {
        if (policy is null)
        {
            return;
        }

        var signature =
            $"{action}|{side}|{policy.ConfigId}|{policy.Symbol}|" +
            $"{policy.Stability}|{policy.HoldConfirmMs}|{policy.LimitMaxGap}|{policy.MaxGap}";
        lock (SummarySync)
        {
            if (!RawPolicySignatures.Add(signature))
            {
                return;
            }
        }

        rawLogger.LogGapStabilityRaw(
            "[GAP_STABILITY_RAW][CONFIG] " +
            $"action={action} side={side} config_id={Text(policy.ConfigId)} " +
            $"symbol={Text(policy.Symbol)} absolute_floor={Value(policy.Stability.AbsoluteFloor)} " +
            $"relative_tolerance={Number(policy?.Stability.RelativeTolerance)} " +
            $"mad_multiplier={Number(policy?.Stability.MadMultiplier)} " +
            $"min_stable_samples={Value(policy?.Stability.MinStableSamples)} " +
            $"max_dispersion={Number(policy?.Stability.MaxDispersion)} " +
            $"max_drift={Number(policy?.Stability.MaxDrift)} " +
            $"hold_confirm_ms={Value(policy?.HoldConfirmMs)} " +
            $"limit_max_gap={Value(policy?.LimitMaxGap)} max_gap={Value(policy?.MaxGap)}");
    }

    private static void RecordSummary(
        ISlotLogger logger,
        string action,
        string side,
        GapCycleSnapshot cycle,
        int? newGap,
        string result,
        PolicyContext? policy)
    {
        var timestampUtc = cycle.LastTickUtc ?? DateTime.UtcNow;
        var key = $"{action}|{side}|{Text(policy?.ConfigId)}|{Text(policy?.Symbol)}";
        string? summaryToLog = null;

        lock (SummarySync)
        {
            if (!Summaries.TryGetValue(key, out var bucket)
                || timestampUtc < bucket.StartedAtUtc)
            {
                bucket = new SummaryBucket(timestampUtc, action, side, policy);
                Summaries[key] = bucket;
            }
            else if (timestampUtc - bucket.StartedAtUtc >= SummaryInterval)
            {
                summaryToLog = FormatSummary(action, side, bucket, policy, timestampUtc);
                bucket = new SummaryBucket(timestampUtc, action, side, policy);
                Summaries[key] = bucket;
            }

            AddToSummary(bucket, cycle, newGap, result);
        }

        if (summaryToLog is not null)
        {
            logger.Log(summaryToLog);
        }
    }

    private static void AddToSummary(
        SummaryBucket bucket,
        GapCycleSnapshot cycle,
        int? newGap,
        string result)
    {
        bucket.CycleEvents++;
        switch (result)
        {
            case "STABLE": bucket.Stable++; break;
            case "UNSTABLE": bucket.Unstable++; break;
            case "RESET_CONFIRM": bucket.ResetConfirm++; break;
            case "DELTA_SPLIT": bucket.DeltaSplit++; break;
            case "REJECTED": bucket.Rejected++; break;
            case "RESET_MISSING_DATA": bucket.ResetMissingData++; break;
            case "RESET_TIMESTAMP": bucket.ResetTimestamp++; break;
            case "RESET_EXPLICIT": bucket.ResetExplicit++; break;
        }

        foreach (var gap in cycle.Gaps)
        {
            bucket.GapMin = bucket.GapMin.HasValue ? Math.Min(bucket.GapMin.Value, gap) : gap;
            bucket.GapMax = bucket.GapMax.HasValue ? Math.Max(bucket.GapMax.Value, gap) : gap;
        }
        if (newGap.HasValue)
        {
            bucket.GapMin = bucket.GapMin.HasValue
                ? Math.Min(bucket.GapMin.Value, newGap.Value)
                : newGap.Value;
            bucket.GapMax = bucket.GapMax.HasValue
                ? Math.Max(bucket.GapMax.Value, newGap.Value)
                : newGap.Value;
        }

        bucket.DurationTotalMs += cycle.DurationMs;
        bucket.DurationMaxMs = Math.Max(bucket.DurationMaxMs, cycle.DurationMs);
        bucket.DispersionMax = Math.Max(bucket.DispersionMax, cycle.Dispersion ?? 0d);
        bucket.DriftMax = Math.Max(bucket.DriftMax, cycle.Drift ?? 0d);
    }

    private static string FormatSummary(
        string action,
        string side,
        SummaryBucket bucket,
        PolicyContext? policy,
        DateTime endedAtUtc)
    {
        var durationSeconds = Math.Max(1d, (endedAtUtc - bucket.StartedAtUtc).TotalSeconds);
        var averageDuration = bucket.CycleEvents > 0
            ? bucket.DurationTotalMs / bucket.CycleEvents
            : 0d;

        return
            "[GAP_STABILITY][SUMMARY] " +
            $"window_seconds={Number(durationSeconds)} action={action} side={side} " +
            $"count_scope={(action == "CLOSE" ? "slot_cycle_events" : "cycle_events")} " +
            $"cycle_events={bucket.CycleEvents} stable={bucket.Stable} unstable={bucket.Unstable} " +
            $"reset_confirm={bucket.ResetConfirm} delta_split={bucket.DeltaSplit} " +
            $"rejected={bucket.Rejected} reset_missing_data={bucket.ResetMissingData} " +
            $"reset_timestamp={bucket.ResetTimestamp} reset_explicit={bucket.ResetExplicit} " +
            $"gap_min={Value(bucket.GapMin)} gap_max={Value(bucket.GapMax)} " +
            $"duration_avg_ms={Number(averageDuration)} duration_max_ms={Number(bucket.DurationMaxMs)} " +
            $"dispersion_max={Number(bucket.DispersionMax)} drift_max={Number(bucket.DriftMax)} " +
            $"config_id={Text(policy?.ConfigId)} symbol={Text(policy?.Symbol)}";
    }

    public static void ResetSummaries()
    {
        lock (SummarySync)
        {
            Summaries.Clear();
            RawPolicySignatures.Clear();
        }
    }

    public static void FlushSummaries(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        List<string> summaries;
        var endedAtUtc = DateTime.UtcNow;

        lock (SummarySync)
        {
            summaries = Summaries.Values
                .Where(bucket => bucket.CycleEvents > 0)
                .Select(bucket => FormatSummary(
                    bucket.Action,
                    bucket.Side,
                    bucket,
                    bucket.Policy,
                    endedAtUtc))
                .ToList();
            Summaries.Clear();
        }

        foreach (var summary in summaries)
        {
            log(summary);
        }
    }

    private static string ResolveGapType(string action, string side) =>
        (action, side) switch
        {
            ("OPEN", "BUY") => "GAP_BUY",
            ("OPEN", "SELL") => "GAP_SELL",
            ("CLOSE", "BUY") => "GAP_SELL",
            ("CLOSE", "SELL") => "GAP_BUY",
            _ => "UNKNOWN"
        };

    private static string Number(double? value) => value.HasValue
        ? value.Value.ToString("0.####", CultureInfo.InvariantCulture)
        : "-";

    private static string Value(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string Timestamp(DateTime? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "-";

    private static string Escape(string value) => value.Replace("\"", "'", StringComparison.Ordinal);
}
