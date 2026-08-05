using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class ManualPairClosePolicyTests
{
    [Fact]
    public void Validate_AllowsClaimedPairWithMatchingSlotAndTickets()
    {
        var result = ManualPairClosePolicy.Validate(ValidInput());

        Assert.True(result.Allowed);
        Assert.Equal("ALLOWED", result.Code);
    }

    [Fact]
    public void Validate_BlocksRequestThatWasNotClaimedByManualFlow()
    {
        var result = ManualPairClosePolicy.Validate(
            ValidInput() with { CloseOwner = CloseExecutionOwner.Auto });

        Assert.False(result.Allowed);
        Assert.Equal("MANUAL_PAIR_NOT_CLAIMED", result.Code);
    }

    [Fact]
    public void Validate_BlocksTicketMismatch()
    {
        var result = ManualPairClosePolicy.Validate(
            ValidInput() with { TicketB = 999 });

        Assert.False(result.Allowed);
        Assert.Equal("MANUAL_PAIR_TICKET_MISMATCH", result.Code);
    }

    [Fact]
    public void Validate_BlocksMissingLeg()
    {
        var result = ManualPairClosePolicy.Validate(
            ValidInput() with { HasLegB = false });

        Assert.False(result.Allowed);
        Assert.Equal("MANUAL_PAIR_BOTH_LEGS_REQUIRED", result.Code);
    }

    private static ManualPairClosePolicyInput ValidInput()
        => new(
            Source: "manual-close-slot",
            ContextPairId: "p1",
            ContextSlotId: 7,
            HasLegA: true,
            HasLegB: true,
            TicketA: 101,
            TicketB: 202,
            SlotExists: true,
            SlotPairId: "p1",
            SlotId: 7,
            SlotTicketA: 101,
            SlotTicketB: 202,
            SlotStatus: PositionSlotStatus.PendingClose,
            IsCloseExecutionPending: true,
            CloseOwner: CloseExecutionOwner.Manual);
}
