namespace TradeDesktop.Application.Services.CTrader;

public sealed record PlatformBSwitchDecision(bool Allowed, string? Message);

// Phase 2 câu 4: đổi nền tảng sàn B có liên quan cTrader khi còn slot → ticket cTrader mã hoá namespace (R4)
// sẽ không khớp nguồn mới, recovery discard và position thành mồ côi. Đổi mt4 ↔ mt5 giữ hành vi cũ (không chặn).
// openSlots phải đếm PendingOpen + Live + PendingClose (IPortfolioCoordinator.LiveAndPendingTotalCount).
public static class PlatformBSwitchGuard
{
    public static PlatformBSwitchDecision Evaluate(string? runningPlatformB, string? newPlatformB, int openSlots)
    {
        var running = (runningPlatformB ?? string.Empty).Trim();
        var next = (newPlatformB ?? string.Empty).Trim();

        var changed = !string.Equals(running, next, StringComparison.OrdinalIgnoreCase);
        var involvesCTrader =
            CTraderRoutingRules.IsCTraderPlatform(running) || CTraderRoutingRules.IsCTraderPlatform(next);

        return changed && involvesCTrader && openSlots > 0
            ? new PlatformBSwitchDecision(false, $"Đang có {openSlots} slot mở — đóng hết trước khi đổi nền tảng sàn B.")
            : new PlatformBSwitchDecision(true, null);
    }
}
