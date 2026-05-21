using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class PortfolioCoordinatorTests
{
    private static PortfolioCoordinator CreateCoordinator(int seed = 42)
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(seed));

    private static GapSignalTriggerResult CreateOpenTrigger(
        GapSignalSide primarySide = GapSignalSide.Buy,
        GapSignalTriggerType triggerType = GapSignalTriggerType.OpenByGapBuy)
        => new(
            Triggered: true,
            Action: GapSignalAction.Open,
            TriggerType: triggerType,
            PrimarySide: primarySide,
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
    public void AllocatePendingOpenSlot_CreatesSlotWithCorrectFields()
    {
        var coordinator = CreateCoordinator();
        var trigger = CreateOpenTrigger();

        var slot = coordinator.AllocatePendingOpenSlot("AUTO-0001-1", trigger);

        Assert.NotNull(slot);
        Assert.Equal(1, slot!.SlotId);
        Assert.Equal("AUTO-0001-1", slot.PairId);
        Assert.Equal(TradingPositionSide.Buy, slot.Side);
        Assert.Equal(TradingOpenMode.GapBuy, slot.OpenMode);
        Assert.Equal(PositionSlotStatus.PendingOpen, slot.Status);
        Assert.Equal(1, coordinator.PendingCount);
    }

    [Fact]
    public void AllocatePendingOpenSlot_WhenQuotaFull_ReturnsNull()
    {
        var coordinator = CreateCoordinator();
        // Default cap=1.
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());

        var second = coordinator.AllocatePendingOpenSlot("p2", CreateOpenTrigger());

        Assert.Null(second);
    }

    [Fact]
    public void MarkSlotOpenConfirmed_TransitionsToLive_AndUpdatesLastOpen()
    {
        var coordinator = CreateCoordinator();
        var slot = coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());

        var confirmedAt = new DateTime(2026, 5, 21, 10, 0, 1, DateTimeKind.Utc);
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, confirmedAt);

        Assert.Equal(PositionSlotStatus.Live, slot!.Status);
        Assert.Equal((ulong)100, slot.TicketA);
        Assert.Equal((ulong)200, slot.TicketB);
        Assert.Equal(confirmedAt, coordinator.LastOpenConfirmedAtUtc);
        Assert.Equal(TradingPositionSide.Buy, coordinator.LastOpenConfirmedSide);
    }

    [Fact]
    public void MarkSlotCloseConfirmed_TransitionsToClosed_AndKicksCooldown()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 5, maxSec: 5);
        var slot = coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p1", DateTime.UtcNow);

        var confirmedAt = new DateTime(2026, 5, 21, 10, 0, 30, DateTimeKind.Utc);
        coordinator.MarkSlotCloseConfirmed("p1", confirmedAt);

        Assert.Equal(PositionSlotStatus.Closed, slot!.Status);
        Assert.NotNull(coordinator.GlobalActionLockUntilUtc);
        Assert.Equal(confirmedAt.AddSeconds(5), coordinator.GlobalActionLockUntilUtc);
    }

    [Fact]
    public void AbortPendingOpen_RemovesSlot()
    {
        var coordinator = CreateCoordinator();
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        Assert.Equal(1, coordinator.PendingCount);

        coordinator.AbortPendingOpen("p1");

        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public void AbortPendingClose_RevertsToLive()
    {
        var coordinator = CreateCoordinator();
        var slot = coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p1", DateTime.UtcNow);
        Assert.Equal(PositionSlotStatus.PendingClose, slot!.Status);

        coordinator.AbortPendingClose("p1");

        Assert.Equal(PositionSlotStatus.Live, slot.Status);
        Assert.False(slot.IsCloseExecutionPending);
    }

    [Fact]
    public void UpdateProfit_StoresValue_OnMatchingTicket()
    {
        var coordinator = CreateCoordinator();
        var slot = coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 555, 666, DateTime.UtcNow);

        coordinator.UpdateProfit(555, 12.5);

        Assert.Equal(12.5, slot!.LastProfitSnapshot);
    }

    [Fact]
    public void UpdateProfit_IgnoresUnknownTicket()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateProfit(9999, 1.0); // no slot
    }

    [Fact]
    public void RegisterSyncedSlot_CreatesLiveSlotDirectly()
    {
        var coordinator = CreateCoordinator();
        var confirmedAt = new DateTime(2026, 5, 21, 14, 0, 0, DateTimeKind.Utc);

        var slot = coordinator.RegisterSyncedSlot(
            "MANUAL-0001-a", TradingPositionSide.Buy, TradingOpenMode.GapBuy,
            ticketA: 100, ticketB: 200, openConfirmedAtUtc: confirmedAt, holdingSeconds: 30);

        Assert.Equal(PositionSlotStatus.Live, slot.Status);
        Assert.Equal(1, coordinator.LiveCount);
        Assert.Equal((ulong)100, slot.TicketA);
        Assert.Equal(30, slot.HoldingSeconds);
    }

    [Fact]
    public void RecoverSlotsFromPersisted_EmptyList_IsNoOp()
    {
        // Phase 5: replaces the Phase 0 NotImplementedException stub.
        // See PortfolioCoordinatorRecoveryTests for full coverage of the recovery path.
        var coordinator = CreateCoordinator();
        coordinator.RecoverSlotsFromPersisted(Array.Empty<TradeDesktop.Application.Abstractions.RecoveredSlotData>());
        Assert.Equal(0, coordinator.LiveCount);
    }
}
