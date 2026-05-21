using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Rule A — Quota: Max total, Max Buy, Max Sell. Live + pending count toward quota.
public sealed class QuotaRuleTests
{
    private static PortfolioCoordinator CreateCoordinator()
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(42));

    private static GapSignalTriggerResult Trigger(GapSignalSide side)
        => new(
            Triggered: true,
            Action: GapSignalAction.Open,
            TriggerType: side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            PrimarySide: side,
            BuyGaps: Array.Empty<int>(),
            SellGaps: Array.Empty<int>(),
            LastBuyGap: null,
            LastSellGap: null,
            TriggeredAtUtc: new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            LastABid: null, LastAAsk: null, LastBBid: null, LastBAsk: null,
            GapBuySourceBBid: null, GapBuySourceAAsk: null,
            GapSellSourceBAsk: null, GapSellSourceABid: null,
            PointMultiplier: 1);

    [Fact]
    public void CanOpenNewSlot_WhenTotalBelow7_AllowsBuy()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }

    [Fact]
    public void CanOpenNewSlot_WhenTotalAt7_BlocksAllSides()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 2, maxBuy: 2, maxSell: 2);
        coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        coordinator.AllocatePendingOpenSlot("p2", Trigger(GapSignalSide.Sell));

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out var reasonB));
        Assert.Contains("QUOTA_TOTAL_FULL", reasonB);
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reasonS));
        Assert.Contains("QUOTA_TOTAL_FULL", reasonS);
    }

    [Fact]
    public void CanOpenNewSlot_WhenBuyAt4_BlocksBuy_AllowsSellIfNoOppositeLock()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 2, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        coordinator.AllocatePendingOpenSlot("p2", Trigger(GapSignalSide.Buy));

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out var reason));
        Assert.Contains("QUOTA_BUY_FULL", reason);

        // No Buy confirms yet → no opposite-side lock → Sell allowed.
        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out _));
    }

    [Fact]
    public void CanOpenNewSlot_WhenSellAt4_BlocksSell()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 1);
        coordinator.AllocatePendingOpenSlot("p-sell", Trigger(GapSignalSide.Sell));

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("QUOTA_SELL_FULL", reason);
    }

    [Fact]
    public void CanOpenNewSlot_CountsPendingInQuota()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 1, maxBuy: 1, maxSell: 1);
        coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));

        // Slot is PendingOpen (not confirmed). Should still count toward quota.
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }

    [Fact]
    public void CanOpenNewSlot_CountsPendingCloseInQuota()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 1, maxBuy: 1, maxSell: 1);
        coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p1", DateTime.UtcNow);

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }

    [Fact]
    public void CanOpenNewSlot_AfterCloseConfirmed_RestoresQuota()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 1, maxBuy: 1, maxSell: 1);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p1", DateTime.UtcNow);
        coordinator.MarkSlotCloseConfirmed("p1", DateTime.UtcNow);

        // Slot is Closed; CountLiveAndPending no longer counts it.
        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }

    [Fact]
    public void UpdateQuotaConfig_ClampsToMinimumOne()
    {
        var coordinator = CreateCoordinator();

        coordinator.UpdateQuotaConfig(maxTotal: 0, maxBuy: -5, maxSell: 0);

        // Min should be 1.
        var slot = coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        Assert.NotNull(slot);
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }
}
