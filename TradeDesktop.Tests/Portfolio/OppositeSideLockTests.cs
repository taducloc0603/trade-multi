using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Rule C — Opposite-side OPEN lock: 300 seconds hardcoded.
// Blocks ONLY opposite-side OPEN; same-side OPEN refreshes timer; CLOSE unaffected.
public sealed class OppositeSideLockTests
{
    private static PortfolioCoordinator CreateCoordinator()
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(42));

    private static GapSignalTriggerResult Trigger(GapSignalSide side)
        => new(true, GapSignalAction.Open,
            side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            side,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    [Fact]
    public void OppositeSideLockSeconds_IsHardcoded300()
    {
        Assert.Equal(300, PortfolioCoordinator.OppositeSideLockSeconds);
    }

    [Fact]
    public void OpenBuy_BlocksOpenSellFor300Seconds()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
        Assert.Contains("last=Buy", reason);
    }

    [Fact]
    public void OpenSell_BlocksOpenBuyFor300Seconds()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-sell", Trigger(GapSignalSide.Sell));
        coordinator.MarkSlotOpenConfirmed("p-sell", 1, 2, DateTime.UtcNow);

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
        Assert.Contains("last=Sell", reason);
    }

    [Fact]
    public void SameSideOpen_NotBlockedByOppositeLock()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);

        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }

    [Fact]
    public void SameSideOpen_RefreshesLockTimerForOpposite()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow.AddSeconds(-200));

        coordinator.AllocatePendingOpenSlot("p2", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p2", 3, 4, DateTime.UtcNow); // refreshes

        // Sell now should still be blocked — lock window moved forward.
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
    }

    [Fact]
    public void OppositeLock_AfterLockExpired_AllowsOpposite()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow.AddSeconds(-400));

        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out _));
    }

    [Fact]
    public void OppositeLock_DoesNotBlockClose()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);

        Assert.True(coordinator.CanCloseNow(out _));
    }

    [Fact]
    public void OppositeLock_StateRetained_AfterAllSlotsClosed()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p-buy", DateTime.UtcNow);
        coordinator.MarkSlotCloseConfirmed("p-buy", DateTime.UtcNow);

        // Slot fully closed, but LastOpenConfirmedSide=Buy retained → Sell still blocked.
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
    }
}
