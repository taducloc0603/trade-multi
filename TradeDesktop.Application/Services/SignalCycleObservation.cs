namespace TradeDesktop.Application.Services;

internal sealed record SignalCycleEventSnapshot(
    string EventName = "",
    string CycleId = "",
    int Count = 0,
    double? Value = null,
    string Reason = "",
    DateTime? AtUtc = null,
    string TerminalEventName = "",
    string TerminalCycleId = "",
    int TerminalCount = 0,
    double? TerminalValue = null,
    string TerminalReason = "",
    DateTime? TerminalAtUtc = null)
{
    internal string DisplayEventName => TerminalEventName.Length > 0 ? TerminalEventName : EventName;
    internal string DisplayCycleId => TerminalEventName.Length > 0 ? TerminalCycleId : CycleId;
    internal int DisplayCount => TerminalEventName.Length > 0 ? TerminalCount : Count;
    internal double? DisplayValue => TerminalEventName.Length > 0 ? TerminalValue : Value;
    internal string DisplayReason => TerminalEventName.Length > 0 ? TerminalReason : Reason;
    internal DateTime? DisplayAtUtc => TerminalEventName.Length > 0 ? TerminalAtUtc : AtUtc;
}

internal static class SignalCycleObservation
{
    internal static SignalCycleEventSnapshot ObserveGap(
        SignalCycleEventSnapshot previous,
        GapCycleUpdateResult update,
        int? value,
        DateTime atUtc)
    {
        var eventName = update.Transition switch
        {
            GapCycleTransition.Started => "Started",
            GapCycleTransition.Joined => "Progress",
            GapCycleTransition.BecameStable => "Completed",
            GapCycleTransition.NewCycle
                or GapCycleTransition.BecameUnstable
                or GapCycleTransition.Rejected
                or GapCycleTransition.ResetMissingData
                or GapCycleTransition.ResetConfirmNotSatisfied
                or GapCycleTransition.ResetTimestamp
                or GapCycleTransition.ResetExplicitly => "Reset",
            _ => string.Empty
        };
        if (eventName.Length == 0)
        {
            return previous;
        }

        var snapshot = eventName == "Reset"
            ? update.CompletedCycle ?? update.CurrentCycle
            : update.CurrentCycle;
        return Record(
            previous,
            eventName,
            snapshot.CycleId,
            snapshot.SampleCount,
            value,
            eventName == "Reset" ? update.CurrentCycle.Reason : snapshot.Reason,
            atUtc);
    }

    internal static SignalCycleEventSnapshot Record(
        SignalCycleEventSnapshot previous,
        string eventName,
        string cycleId,
        int count,
        double? value,
        string reason,
        DateTime atUtc)
    {
        var isTerminal = eventName is "Triggered" or "Reset" or "Completed";
        return new SignalCycleEventSnapshot(
            eventName,
            cycleId,
            count,
            value,
            reason,
            atUtc,
            isTerminal ? eventName : previous.TerminalEventName,
            isTerminal ? cycleId : previous.TerminalCycleId,
            isTerminal ? count : previous.TerminalCount,
            isTerminal ? value : previous.TerminalValue,
            isTerminal ? reason : previous.TerminalReason,
            isTerminal ? atUtc : previous.TerminalAtUtc);
    }
}
