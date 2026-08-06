using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Phase 6 — display property logic (verified via coordinator state, since
// DashboardViewModel can't be instantiated outside WPF runtime).
public sealed class DisplayPropertyTests
{
    private static PortfolioCoordinator CreateCoordinator()
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(42));

    private static GapSignalTriggerResult OpenTrigger(GapSignalSide side = GapSignalSide.Buy)
        => new(
            true, GapSignalAction.Open,
            side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            side,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    [Fact]
    public void CounterText_EmptyPortfolio_ReturnsZeroes()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        Assert.Equal(0, coordinator.LiveBuyCount);
        Assert.Equal(0, coordinator.LiveSellCount);
        Assert.Equal(0, coordinator.LiveAndPendingTotalCount);
    }

    [Fact]
    public void CounterText_AfterOpens_ReflectsCorrectly()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p2", 3, 4, DateTime.UtcNow);

        Assert.Equal(2, coordinator.LiveBuyCount);
        Assert.Equal(0, coordinator.LiveSellCount);
        Assert.Equal(2, coordinator.LiveAndPendingTotalCount);
    }

    [Fact]
    public void SameSideTransitionLock_RemainingSecondsPositive()
    {
        var coordinator = CreateCoordinator();
        var now = DateTime.UtcNow;
        var first = coordinator.TryAcquireTradeAction(
            now, "OPEN", "test", side: TradingPositionSide.Buy);
        var blocked = coordinator.TryAcquireTradeAction(
            now.AddSeconds(1), "OPEN", "test-2", side: TradingPositionSide.Buy);

        Assert.True(first.Acquired);
        Assert.False(blocked.Acquired);
        Assert.InRange(blocked.Remaining.TotalSeconds, 2, 9);
    }

    [Fact]
    public void OppositeLockWindow_StillActiveWithinSpec()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        coordinator.AllocatePendingOpenSlot("p-buy", OpenTrigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow.AddSeconds(-60));

        Assert.Equal(TradingPositionSide.Buy, coordinator.LastOpenConfirmedSide);
        Assert.NotNull(coordinator.LastOpenConfirmedAtUtc);

        var elapsedSec = (DateTime.UtcNow - coordinator.LastOpenConfirmedAtUtc!.Value).TotalSeconds;
        Assert.InRange(elapsedSec, 59.0, 61.0);
        Assert.True(elapsedSec < coordinator.OppositeSideLockSeconds);
    }

    [Fact]
    public void QuotaFull_AtCap()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 2, maxBuy: 2, maxSell: 2);
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());

        Assert.Equal(2, coordinator.LiveAndPendingTotalCount);
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }
}
