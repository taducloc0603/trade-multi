using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Phase 7 — monitoring metrics: counters for opens/closes + skip reasons.
public sealed class MetricsTests
{
    private static PortfolioCoordinator CreateCoordinator()
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(42));

    private static GapSignalTriggerResult OpenTrigger(GapSignalSide side = GapSignalSide.Buy)
        => new(true, GapSignalAction.Open,
            side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            side,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    [Fact]
    public void GetMetrics_FreshCoordinator_AllCountersZero()
    {
        var coordinator = CreateCoordinator();

        var m = coordinator.GetMetrics();

        Assert.Equal(0, m.CurrentLiveSlots);
        Assert.Equal(0L, m.TotalOpensAllTime);
        Assert.Equal(0L, m.TotalClosesAllTime);
        Assert.Equal(0L, m.QuotaSkipCount);
        Assert.Equal(0L, m.OppositeLockSkipCount);
        Assert.Equal(0L, m.CooldownSkipCount);
    }

    [Fact]
    public void TotalOpensAllTime_IncrementsOnOpenConfirm()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 7, maxSell: 7);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 3, 4, DateTime.UtcNow);

        Assert.Equal(2L, coordinator.GetMetrics().TotalOpensAllTime);
    }

    [Fact]
    public void TotalClosesAllTime_IncrementsOnCloseConfirm()
    {
        var coordinator = CreateCoordinator();
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p1", DateTime.UtcNow);
        coordinator.MarkSlotCloseConfirmed("p1", DateTime.UtcNow);

        Assert.Equal(1L, coordinator.GetMetrics().TotalClosesAllTime);
    }

    [Fact]
    public void QuotaSkipCount_IncrementsWhenCanOpenBlockedByQuota()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 1, maxBuy: 1, maxSell: 1);
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());

        // 5 more attempts blocked by quota.
        for (var i = 0; i < 5; i++)
        {
            coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _);
        }

        Assert.True(coordinator.GetMetrics().QuotaSkipCount >= 5);
    }

    [Fact]
    public void OppositeLockSkipCount_IncrementsOnLockBlock()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-buy", OpenTrigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);

        // Sell blocked by opposite-side lock 3 times.
        for (var i = 0; i < 3; i++)
        {
            coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out _);
        }

        Assert.True(coordinator.GetMetrics().OppositeLockSkipCount >= 3);
    }

    [Fact]
    public void CooldownSkipCount_IncrementsOnCloseBlock()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 60, maxSec: 60);
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);

        for (var i = 0; i < 3; i++)
        {
            coordinator.CanCloseNow(out _);
        }

        Assert.True(coordinator.GetMetrics().CooldownSkipCount >= 3);
    }
}
