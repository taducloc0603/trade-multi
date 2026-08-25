using System.Globalization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

internal static class GapCycleDiagnostics
{
    // Diagnostics tạm thời phục vụ hiệu chỉnh DB. Không tham gia quyết định giao dịch
    // và có thể tắt/gỡ tại một nơi sau giai đoạn thu thập dữ liệu.
    private const bool EnableGapStabilityDiagnostics = true;
    private const double MinimumResetDurationToLogMs = 250d;
    private const int MaxLoggedGaps = 2_000;

    internal sealed record PolicyContext(
        GapStabilityConfig Stability,
        int HoldConfirmMs,
        int LimitMaxGap,
        int MaxGap,
        string ConfigId,
        string Symbol);

    public static void LogTransition(
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
        if (isReset || update.Transition == GapCycleTransition.Rejected)
        {
            snapshot = snapshot with { Status = update.CurrentCycle.Status };
        }
        var reason = update.CurrentCycle.Reason;
        var delta = newGap.HasValue && snapshot.Center.HasValue
            ? GapStabilityCalculator.CalculateDelta(newGap.Value, snapshot.Center.Value)
            : (double?)null;

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
            policy));
    }

    public static void LogTrigger(
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
            signalId));
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
        string signalId = "")
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
        $"drift={Number(cycle.Drift)} status={cycle.Status} result={result} " +
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

    private static string Number(double? value) => value.HasValue
        ? value.Value.ToString("0.####", CultureInfo.InvariantCulture)
        : "-";

    private static string Value(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string Timestamp(DateTime? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "-";

    private static string Escape(string value) => value.Replace("\"", "'", StringComparison.Ordinal);
}
