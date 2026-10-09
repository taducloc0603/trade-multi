namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Quyết định thuần: một pending-close đã xác nhận đóng đủ chưa. Chân không có ticket (rollback partial-open,
/// đóng chân còn lại sau external close — chỉ một chân được đăng ký) coi như đã xác nhận; nếu đòi cả hai cờ thì
/// pending đó được đánh dấu resolved mà KHÔNG nhả non-auto barrier (docs/plans/primexbt phase-4 F4-1).
/// Cặp đủ hai ticket: giữ nguyên hành vi cũ (phải xác nhận cả hai).
/// </summary>
public static class PendingCloseConfirmation
{
    public static bool AreAllLegsConfirmed(bool hasTicketA, bool closeConfirmedA, bool hasTicketB, bool closeConfirmedB)
        => (closeConfirmedA || !hasTicketA) && (closeConfirmedB || !hasTicketB);
}
