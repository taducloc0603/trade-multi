namespace TradeDesktop.Application.Services.Portfolio;

public static class ManualPairClosePolicy
{
    public static ManualPairClosePolicyResult Validate(ManualPairClosePolicyInput input)
    {
        if (!string.Equals(input.Source, "manual-close-slot", StringComparison.Ordinal))
        {
            return ManualPairClosePolicyResult.Blocked("MANUAL_PAIR_SOURCE_INVALID");
        }
        if (string.IsNullOrWhiteSpace(input.ContextPairId) || !input.ContextSlotId.HasValue)
        {
            return ManualPairClosePolicyResult.Blocked("MANUAL_PAIR_TARGET_REQUIRED");
        }
        if (!input.HasLegA || !input.HasLegB)
        {
            return ManualPairClosePolicyResult.Blocked("MANUAL_PAIR_BOTH_LEGS_REQUIRED");
        }
        if (!input.SlotExists
            || !string.Equals(input.ContextPairId, input.SlotPairId, StringComparison.Ordinal)
            || input.ContextSlotId != input.SlotId)
        {
            return ManualPairClosePolicyResult.Blocked("MANUAL_PAIR_SLOT_MISMATCH");
        }
        if (input.SlotStatus != PositionSlotStatus.PendingClose
            || !input.IsCloseExecutionPending
            || input.CloseOwner != CloseExecutionOwner.Manual)
        {
            return ManualPairClosePolicyResult.Blocked("MANUAL_PAIR_NOT_CLAIMED");
        }
        if (input.TicketA == 0
            || input.TicketB == 0
            || input.TicketA != input.SlotTicketA
            || input.TicketB != input.SlotTicketB)
        {
            return ManualPairClosePolicyResult.Blocked("MANUAL_PAIR_TICKET_MISMATCH");
        }

        return ManualPairClosePolicyResult.AllowedResult;
    }
}

public sealed record ManualPairClosePolicyInput(
    string Source,
    string? ContextPairId,
    int? ContextSlotId,
    bool HasLegA,
    bool HasLegB,
    ulong TicketA,
    ulong TicketB,
    bool SlotExists,
    string? SlotPairId,
    int? SlotId,
    ulong? SlotTicketA,
    ulong? SlotTicketB,
    PositionSlotStatus SlotStatus,
    bool IsCloseExecutionPending,
    CloseExecutionOwner CloseOwner);

public sealed record ManualPairClosePolicyResult(bool Allowed, string Code)
{
    public static ManualPairClosePolicyResult AllowedResult { get; } = new(true, "ALLOWED");
    public static ManualPairClosePolicyResult Blocked(string code) => new(false, code);
}
