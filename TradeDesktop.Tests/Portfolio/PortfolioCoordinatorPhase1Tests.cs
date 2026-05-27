using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Phase 1: multi-slot pipeline activated, cap=1 (production behavior identical).
public sealed class PortfolioCoordinatorPhase1Tests
{
    private static PortfolioCoordinator CreateCoordinator(int seed = 42)
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(seed));

    private static GapSignalTriggerResult OpenTrigger(GapSignalSide side = GapSignalSide.Buy)
        => new(
            Triggered: true,
            Action: GapSignalAction.Open,
            TriggerType: side == GapSignalSide.Buy
                ? GapSignalTriggerType.OpenByGapBuy
                : GapSignalTriggerType.OpenByGapSell,
            PrimarySide: side,
            BuyGaps: Array.Empty<int>(),
            SellGaps: Array.Empty<int>(),
            LastBuyGap: null,
            LastSellGap: null,
            TriggeredAtUtc: new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            LastABid: null,
            LastAAsk: null,
            LastBBid: null,
            LastBAsk: null,
            GapBuySourceBBid: null,
            GapBuySourceAAsk: null,
            GapSellSourceBAsk: null,
            GapSellSourceABid: null,
            PointMultiplier: 1);

    [Fact]
    public void AllocatePendingOpenSlot_WhenQuotaFull_ReturnsNull()
    {
        var coordinator = CreateCoordinator();
        // Default cap=1.
        var first = coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        var second = coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void UpdateProfit_StoresValueInSlot()
    {
        var coordinator = CreateCoordinator();
        var slot = coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", ticketA: 100, ticketB: 200, DateTime.UtcNow);

        coordinator.UpdateProfit(100, 1.50);
        coordinator.UpdateProfit(200, 2.50);

        Assert.NotNull(slot);
        Assert.Equal(4.00, slot!.LastProfitSnapshot);
    }

    [Fact]
    public void UpdateProfit_IgnoresUnknownTicket()
    {
        var coordinator = CreateCoordinator();

        coordinator.UpdateProfit(99999, 999.0); // no-op
        Assert.Equal(0, coordinator.LiveCount);
    }

    [Fact]
    public void LiveAndPendingTotalCount_TracksPendingAndLive()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 3, maxBuy: 3, maxSell: 3);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        Assert.Equal(1, coordinator.LiveAndPendingTotalCount);

        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        Assert.Equal(1, coordinator.LiveAndPendingTotalCount);

        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        Assert.Equal(2, coordinator.LiveAndPendingTotalCount);
    }

    [Fact]
    public void UpdateQuotaConfig_AppliesNewLimits()
    {
        var coordinator = CreateCoordinator();
        // Default cap=1. Bump to cap=2.
        coordinator.UpdateQuotaConfig(maxTotal: 2, maxBuy: 2, maxSell: 2);

        var s1 = coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        var s2 = coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        var s3 = coordinator.AllocatePendingOpenSlot("p3", OpenTrigger());

        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.Null(s3); // quota full
    }

    [Fact]
    public void MarkSlotCloseConfirmed_RemovesFromLiveCount()
    {
        var coordinator = CreateCoordinator();
        var slot = coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p1", DateTime.UtcNow);

        coordinator.MarkSlotCloseConfirmed("p1", DateTime.UtcNow);

        Assert.Equal(PositionSlotStatus.Closed, slot!.Status);
        Assert.Equal(0, coordinator.LiveCount);
    }
}
