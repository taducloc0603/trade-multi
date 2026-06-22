using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Regression: with 2+ live slots, finalizing a close must remove EXACTLY the slot whose pairId
// just closed — never an arbitrary Live slot. Pre-fix BeginWaitAfterClose used
// `PendingCloseSlots.First() ?? LiveSlots.First()`, which removed a still-live pair when the
// closed slot had already left PendingCloseSlots, leaving that pair untracked & unclosable by hand.
public sealed class BeginWaitAfterCloseMultiSlotTests
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

    private static (PortfolioCoordinator coordinator, PortfolioCoordinatorAdapter adapter) CreateWithTwoLiveSlots()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        coordinator.AllocatePendingOpenSlot("AUTO-0000-aaa", OpenTrigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("AUTO-0000-aaa", 100, 200, DateTime.UtcNow);
        coordinator.AllocatePendingOpenSlot("AUTO-0001-bbb", OpenTrigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("AUTO-0001-bbb", 101, 201, DateTime.UtcNow);

        return (coordinator, new PortfolioCoordinatorAdapter(coordinator));
    }

    [Fact]
    public void BeginWaitAfterClose_WithPairId_RemovesOnlyThatSlot_KeepsOtherLive()
    {
        var (coordinator, adapter) = CreateWithTwoLiveSlots();

        // Close the SECOND slot. Its leg may already have left PendingClose by finalize time.
        adapter.BeginWaitAfterClose(
            DateTime.UtcNow, startWaitSeconds: 0, endWaitSeconds: 0, closingPairId: "AUTO-0001-bbb");

        // The closed slot is gone; the other slot must still be live and resolvable for manual close.
        Assert.Null(coordinator.GetSlotByPairId("AUTO-0001-bbb"));
        var survivor = coordinator.GetSlotByPairId("AUTO-0000-aaa");
        Assert.NotNull(survivor);
        Assert.Equal(PositionSlotStatus.Live, survivor!.Status);
        Assert.Equal(1, coordinator.LiveCount);
    }

    [Fact]
    public void BeginWaitAfterClose_WithUnknownPairId_RemovesNoSlot_StillFinalizes()
    {
        var (coordinator, adapter) = CreateWithTwoLiveSlots();
        var closedAt = DateTime.UtcNow;

        // Slot already removed by another path: pairId resolves to nothing.
        adapter.BeginWaitAfterClose(
            closedAt, startWaitSeconds: 0, endWaitSeconds: 0, closingPairId: "AUTO-9999-zzz");

        // No live slot touched.
        Assert.Equal(2, coordinator.LiveCount);
        Assert.NotNull(coordinator.GetSlotByPairId("AUTO-0000-aaa"));
        Assert.NotNull(coordinator.GetSlotByPairId("AUTO-0001-bbb"));

        // Cycle still finalizes its bookkeeping so the flow can transition to WaitingOpen.
        Assert.Equal(closedAt, adapter.ClosedAtUtc);
    }
}
