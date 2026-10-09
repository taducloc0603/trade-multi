using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// F4-1 (docs/plans/primexbt phase-4): pending-close chỉ một chân (rollback partial-open) phải được coi là xác nhận
// đủ ngay khi chân đó đóng — nếu không non-auto barrier không bao giờ được nhả và Auto kẹt tới khi Stop.
public sealed class PendingCloseConfirmationTests
{
    [Theory]
    // Cặp đủ hai ticket: hành vi cũ — phải xác nhận cả hai.
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, false, true, false, false)]
    // Rollback chỉ chân A (chân B chưa từng mở).
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, false, false, false)]
    // Chỉ chân B.
    [InlineData(false, false, true, true, true)]
    [InlineData(false, false, true, false, false)]
    public void AreAllLegsConfirmed_TreatsMissingTicketAsConfirmed(
        bool hasTicketA, bool confirmedA, bool hasTicketB, bool confirmedB, bool expected)
    {
        Assert.Equal(expected, PendingCloseConfirmation.AreAllLegsConfirmed(hasTicketA, confirmedA, hasTicketB, confirmedB));
    }

    [Fact]
    public void NoTicketOnEitherLeg_IsVacuouslyConfirmed()
    {
        // Trạng thái này vốn đã được đánh dấu resolved ở nhánh `!needCheckA && !needCheckB`; hàm không được throw.
        Assert.True(PendingCloseConfirmation.AreAllLegsConfirmed(false, false, false, false));
    }
}
