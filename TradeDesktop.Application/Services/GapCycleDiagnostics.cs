using System.Globalization;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Application.Services;

internal static class GapCycleDiagnostics
{
    public static void LogTransition(
        ISlotLogger? logger,
        string action,
        string side,
        int? slotId,
        GapCycleUpdateResult update,
        int? newGap)
    {
        if (logger is null || update.Transition is GapCycleTransition.None or GapCycleTransition.Joined)
        {
            return;
        }

        var isReset = update.Transition is GapCycleTransition.ResetMissingData
            or GapCycleTransition.ResetConfirmNotSatisfied
            or GapCycleTransition.ResetTimestamp
            or GapCycleTransition.ResetExplicitly;
        if (isReset && update.CompletedCycle is null)
        {
            // Không có Cycle để reset: đây là trạng thái tick bình thường của phía đối diện,
            // không phải một thay đổi cần quan sát.
            return;
        }

        var eventName = update.Transition switch
        {
            GapCycleTransition.Started => "CYCLE_STARTED",
            GapCycleTransition.NewCycle => "NEW_CYCLE_CREATED",
            GapCycleTransition.BecameStable => "CYCLE_STABLE",
            GapCycleTransition.BecameUnstable => "CYCLE_UNSTABLE",
            GapCycleTransition.Rejected => "CYCLE_REJECTED",
            GapCycleTransition.ResetMissingData
                or GapCycleTransition.ResetConfirmNotSatisfied
                or GapCycleTransition.ResetTimestamp
                or GapCycleTransition.ResetExplicitly => "CYCLE_RESET",
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
            reason));
    }

    public static void LogTrigger(
        ISlotLogger? logger,
        string action,
        string side,
        int? slotId,
        GapCycleSnapshot cycle,
        int newGap,
        string reason)
    {
        if (logger is null)
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
            reason));
    }

    private static string Format(
        string eventName,
        string action,
        string side,
        int? slotId,
        GapCycleSnapshot cycle,
        int? newGap,
        double? delta,
        string reason) =>
        $"[GAP_STABILITY][{eventName}] " +
        $"action={action} side={side} slot_id={Value(slotId)} " +
        $"sample_count={cycle.SampleCount} duration_ms={Number(cycle.DurationMs)} " +
        $"center={Number(cycle.Center)} mad={Number(cycle.Mad)} tolerance={Number(cycle.Tolerance)} " +
        $"new_gap={Value(newGap)} delta={Number(delta)} dispersion={Number(cycle.Dispersion)} " +
        $"drift={Number(cycle.Drift)} status={cycle.Status} reason=\"{Escape(reason)}\"";

    private static string Number(double? value) => value.HasValue
        ? value.Value.ToString("0.####", CultureInfo.InvariantCulture)
        : "-";

    private static string Value(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Escape(string value) => value.Replace("\"", "'", StringComparison.Ordinal);
}
