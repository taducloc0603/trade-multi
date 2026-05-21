using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// End-to-end scenarios combining Rules A/B/C with cap=7/4/4 (Phase 2 §2.5).
public sealed class MultiSlotIntegrationTests
{
    private static PortfolioCoordinator CreateCoordinator(int seed = 42)
    {
        var c = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(seed));
        c.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        c.UpdateCooldownConfig(minSec: 0, maxSec: 0);  // disable cooldown for sequencing tests
        return c;
    }

    private static GapSignalTriggerResult Trigger(GapSignalSide side)
        => new(true, GapSignalAction.Open,
            side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            side,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    [Fact]
    public void Scenario_OpenSevenBuys_BlockedAtEighth()
    {
        var c = CreateCoordinator();
        // Bump Buy quota to 7 for this scenario.
        c.UpdateQuotaConfig(maxTotal: 7, maxBuy: 7, maxSell: 4);

        for (var i = 0; i < 7; i++)
        {
            var pairId = $"p{i + 1}";
            var slot = c.AllocatePendingOpenSlot(pairId, Trigger(GapSignalSide.Buy));
            Assert.NotNull(slot);
            c.MarkSlotOpenConfirmed(pairId, (ulong)(100 + i), (ulong)(200 + i), DateTime.UtcNow);
        }

        Assert.Equal(7, c.LiveCount);

        var eighth = c.AllocatePendingOpenSlot("p8", Trigger(GapSignalSide.Buy));
        Assert.Null(eighth);
    }

    [Fact]
    public void Scenario_FourBuys_FifthBlockedByQuota_SellBlockedByOppositeLock()
    {
        var c = CreateCoordinator();

        // Open 4 Buy.
        for (var i = 0; i < 4; i++)
        {
            var pairId = $"p{i + 1}";
            var slot = c.AllocatePendingOpenSlot(pairId, Trigger(GapSignalSide.Buy));
            Assert.NotNull(slot);
            c.MarkSlotOpenConfirmed(pairId, (ulong)(100 + i), (ulong)(200 + i), DateTime.UtcNow);
        }

        // 5th Buy blocked by quota.
        Assert.False(c.CanOpenNewSlot(TradingPositionSide.Buy, out var buyReason));
        Assert.Contains("QUOTA_BUY_FULL", buyReason);

        // Sell blocked by opposite-lock (Buy was confirmed within 300s).
        Assert.False(c.CanOpenNewSlot(TradingPositionSide.Sell, out var sellReason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", sellReason);
    }

    [Fact]
    public void Scenario_BuyThenSellWithin5min_SellBlocked()
    {
        var c = CreateCoordinator();
        c.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        c.MarkSlotOpenConfirmed("p1", 100, 200, DateTime.UtcNow.AddSeconds(-60));

        Assert.False(c.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
    }

    [Fact]
    public void Scenario_BuyThenSellAfter5min_SellAllowed()
    {
        var c = CreateCoordinator();
        c.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        // Confirm 400s ago — beyond 300s lock.
        c.MarkSlotOpenConfirmed("p1", 100, 200, DateTime.UtcNow.AddSeconds(-400));

        Assert.True(c.CanOpenNewSlot(TradingPositionSide.Sell, out _));
    }

    [Fact]
    public void Scenario_MultipleBuys_OppositeLockRefreshesEachTime()
    {
        var c = CreateCoordinator();
        c.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        c.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow.AddSeconds(-200));

        // Refresh by opening 2nd Buy at "now".
        c.AllocatePendingOpenSlot("p2", Trigger(GapSignalSide.Buy));
        c.MarkSlotOpenConfirmed("p2", 3, 4, DateTime.UtcNow);

        // 200s after first Buy + 0s after second Buy → opposite-lock still active.
        Assert.False(c.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
    }

    [Fact]
    public void Scenario_CloseDuringCooldown_Deferred()
    {
        var c = CreateCoordinator();
        c.UpdateCooldownConfig(minSec: 60, maxSec: 60);

        c.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        c.MarkSlotOpenConfirmed("p1", 100, 200, DateTime.UtcNow);

        // Cooldown 60s active → close blocked.
        Assert.False(c.CanCloseNow(out var reason));
        Assert.Contains("GLOBAL_COOLDOWN", reason);
    }
}
