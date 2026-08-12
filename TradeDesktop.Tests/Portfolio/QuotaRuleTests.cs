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

        // Timestamps đặt trong quá khứ (>300s) để post-close lock (Rule C) đã hết hạn —
        // test này chỉ verify quota restoration, không bị post-close lock che.
        var past = DateTime.UtcNow.AddSeconds(-400);
        coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, past);
        coordinator.MarkSlotCloseTriggered("p1", past);
        coordinator.MarkSlotCloseConfirmed("p1", past);

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

    [Fact]
    public void RandomQuota_UsesConfiguredSideCaps_AndKeepsTotalFixed()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 5, maxBuy: 5, maxSell: 5);
        coordinator.EnableRandomQuota();
        coordinator.RestoreRandomQuotaState(new(true, 5, 5, 0, 5, 1));

        var state = coordinator.RandomQuotaState;
        Assert.True(state.IsEnabled);
        Assert.InRange(state.EffectiveMaxBuy, 1, 5);
        Assert.InRange(state.EffectiveMaxSell, 1, 5);
        Assert.InRange(state.RandomAfterOpens, 2, 5);

        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(coordinator.AllocatePendingOpenSlot($"total-{i}", Trigger(GapSignalSide.Buy)));
        }
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("QUOTA_TOTAL_FULL (5/5)", reason);
    }

    [Fact]
    public void RandomQuota_RerollsOnlyAfterConfiguredNumberOfConfirmedPairs()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 20, maxBuy: 20, maxSell: 20);
        coordinator.EnableRandomQuota();
        var selectedThreshold = coordinator.RandomQuotaState.RandomAfterOpens;
        coordinator.RestoreRandomQuotaState(new(true, 20, 20, 0, selectedThreshold, 1));
        var initial = coordinator.RandomQuotaState;

        for (var i = 1; i < initial.RandomAfterOpens; i++)
        {
            var pairId = $"confirmed-{i}";
            coordinator.AllocatePendingOpenSlot(pairId, Trigger(GapSignalSide.Buy));
            coordinator.MarkSlotOpenConfirmed(pairId, (ulong)(i * 2), (ulong)(i * 2 + 1), DateTime.UtcNow);
            Assert.Equal(initial.CycleNumber, coordinator.RandomQuotaState.CycleNumber);
        }

        const string finalPair = "confirmed-final";
        coordinator.AllocatePendingOpenSlot(finalPair, Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed(finalPair, 100, 101, DateTime.UtcNow);

        Assert.Equal(initial.CycleNumber + 1, coordinator.RandomQuotaState.CycleNumber);
        Assert.Equal(initial.EffectiveMaxBuy, coordinator.RandomQuotaState.PreviousMaxBuy);
        Assert.Equal(initial.EffectiveMaxSell, coordinator.RandomQuotaState.PreviousMaxSell);
        Assert.Equal(0, coordinator.RandomQuotaState.OpenCountSinceRandom);
        Assert.InRange(coordinator.RandomQuotaState.RandomAfterOpens, 2, 5);
    }

    [Fact]
    public void RandomQuota_DuplicateConfirmation_CountsOnlyOnce()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(10, 10, 10);
        coordinator.EnableRandomQuota();
        coordinator.RestoreRandomQuotaState(new(true, 10, 10, 0, 5, 7));
        coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));

        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);

        Assert.Equal(1, coordinator.RandomQuotaState.OpenCountSinceRandom);
        Assert.Equal(7, coordinator.RandomQuotaState.CycleNumber);
    }

    [Fact]
    public void RandomQuota_RestoreClampsToLatestConfiguredCaps()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 5, maxBuy: 2, maxSell: 3);

        coordinator.RestoreRandomQuotaState(new(true, 9, 8, 2, 4, 12));

        Assert.Equal(new(true, 2, 3, 2, 4, 12), coordinator.RandomQuotaState);
    }

    [Fact]
    public void RandomQuota_RestorePreservesPreviousQuotaAndClampsItToCurrentCaps()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 5, maxBuy: 2, maxSell: 3);

        coordinator.RestoreRandomQuotaState(new(true, 2, 3, 1, 4, 9, 8, 7));

        Assert.Equal(new(true, 2, 3, 1, 4, 9, 2, 3), coordinator.RandomQuotaState);
    }
}
