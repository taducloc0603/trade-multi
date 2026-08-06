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
    public void MarkSlotCloseConfirmed_TransitionsToClosed_WithoutCreatingGlobalCooldown()
    {
        // Phase 8: cooldown kicked tại CLOSE DISPATCH (MarkSlotCloseTriggered), không phải confirm.
        var coordinator = CreateCoordinator();
        coordinator.UpdatePostOpenLockConfig(0);
        var slot = coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);

        var triggerTime = DateTime.UtcNow;
        coordinator.TryAcquireTradeAction(
            triggerTime, "CLOSE", "test", side: TradingPositionSide.Buy, pairId: "p1");
        coordinator.MarkSlotCloseTriggered("p1", triggerTime);

        var confirmedAt = triggerTime.AddSeconds(1);
        coordinator.MarkSlotCloseConfirmed("p1", confirmedAt);

        Assert.Equal(PositionSlotStatus.Closed, slot!.Status);
        Assert.Null(coordinator.GlobalActionLockUntilUtc);
        Assert.Equal(confirmedAt, coordinator.LastCloseConfirmedAtUtc);
    }

    [Fact]
    public void CloseSlotManually_RemovesSlot_AndCountsDrop()
    {
        var coordinator = CreateCoordinator();
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        Assert.Equal(1, coordinator.LiveCount);

        coordinator.CloseSlotManually("p1", DateTime.UtcNow);

        Assert.Null(coordinator.GetSlotByPairId("p1"));
        Assert.Equal(0, coordinator.LiveCount);
        Assert.Equal(0, coordinator.PendingCount);
        Assert.Empty(coordinator.LiveSlots);
    }

    [Fact]
    public void CloseSlotManually_WhenPairIdUnknown_IsNoOp()
    {
        var coordinator = CreateCoordinator();
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);

        coordinator.CloseSlotManually("does-not-exist", DateTime.UtcNow);

        Assert.NotNull(coordinator.GetSlotByPairId("p1"));
        Assert.Equal(1, coordinator.LiveCount);
    }

    [Fact]
    public void TryClaimSlotClose_ManualClaimBlocksAutoClaimForSamePair()
    {
        var coordinator = CreateCoordinator();
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);

        Assert.True(coordinator.TryClaimSlotClose("p1", CloseExecutionOwner.Manual, DateTime.UtcNow));
        Assert.False(coordinator.TryClaimSlotClose("p1", CloseExecutionOwner.Auto, DateTime.UtcNow));
        Assert.Equal(CloseExecutionOwner.Manual, coordinator.GetSlotByPairId("p1")!.CloseOwner);
    }

    [Fact]
    public void AbortPendingClose_ReleasesManualClaimForAuto()
    {
        var coordinator = CreateCoordinator();
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        Assert.True(coordinator.TryClaimSlotClose("p1", CloseExecutionOwner.Manual, DateTime.UtcNow));

        coordinator.AbortPendingClose("p1");

        Assert.Equal(CloseExecutionOwner.None, coordinator.GetSlotByPairId("p1")!.CloseOwner);
        Assert.True(coordinator.TryClaimSlotClose("p1", CloseExecutionOwner.Auto, DateTime.UtcNow));
    }

    [Fact]
    public void TryClaimSlotClose_IsAtomicForConcurrentManualAndAutoCallers()
    {
        var coordinator = CreateCoordinator();
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        var owners = new[] { CloseExecutionOwner.Manual, CloseExecutionOwner.Auto };

        var results = owners
            .AsParallel()
            .Select(owner => coordinator.TryClaimSlotClose("p1", owner, DateTime.UtcNow))
            .ToArray();

        Assert.Single(results.Where(result => result));
        Assert.NotEqual(CloseExecutionOwner.None, coordinator.GetSlotByPairId("p1")!.CloseOwner);
    }

    [Fact]
    public void ManualClaim_DoesNotPreventAutoFromClaimingAnotherPair()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 2, maxBuy: 2, maxSell: 2);
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.AllocatePendingOpenSlot("p2", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 3, 4, DateTime.UtcNow);

        Assert.True(coordinator.TryClaimSlotClose("p1", CloseExecutionOwner.Manual, DateTime.UtcNow));
        Assert.True(coordinator.TryClaimSlotClose("p2", CloseExecutionOwner.Auto, DateTime.UtcNow));
        Assert.Equal(CloseExecutionOwner.Manual, coordinator.GetSlotByPairId("p1")!.CloseOwner);
        Assert.Equal(CloseExecutionOwner.Auto, coordinator.GetSlotByPairId("p2")!.CloseOwner);
    }

    [Fact]
    public void ManualClaim_ActivatesBarrier_AndAbortReleasesIt()
    {
        var coordinator = CreateCoordinator();
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);

        Assert.True(coordinator.TryClaimSlotClose("p1", CloseExecutionOwner.Manual, DateTime.UtcNow));
        Assert.True(coordinator.HasNonAutoCloseInFlight);

        coordinator.AbortPendingClose("p1");

        Assert.False(coordinator.HasNonAutoCloseInFlight);
        Assert.Equal(PositionSlotStatus.Live, coordinator.GetSlotByPairId("p1")!.Status);
    }

    [Fact]
    public void CompletedManualClose_ReleasesBarrier_WithoutCreatingAutoCooldown()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(0, 0);
        coordinator.UpdatePostCloseLockConfig(300);
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        Assert.True(coordinator.TryClaimSlotClose("p1", CloseExecutionOwner.Manual, DateTime.UtcNow));

        coordinator.CloseSlotManually("p1", DateTime.UtcNow);

        Assert.False(coordinator.HasNonAutoCloseInFlight);
        Assert.Null(coordinator.LastCloseConfirmedAtUtc);
        Assert.Null(coordinator.GetSlotByPairId("p1"));
    }

    [Fact]
    public void SequentialManualCloses_AreAllowedWithoutCooldownBetweenSlots()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(2, 2, 2);
        coordinator.UpdatePostCloseLockConfig(300);
        coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);
        coordinator.AllocatePendingOpenSlot("p2", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 3, 4, DateTime.UtcNow);

        Assert.True(coordinator.TryClaimSlotClose("p1", CloseExecutionOwner.Manual, DateTime.UtcNow));
        coordinator.CloseSlotManually("p1", DateTime.UtcNow);

        Assert.True(coordinator.TryClaimSlotClose("p2", CloseExecutionOwner.Manual, DateTime.UtcNow));
    }

    [Fact]
    public void PartialOpenRecoverySlot_CannotBeClaimedByAutoClose()
    {
        var coordinator = CreateCoordinator();
        var slot = coordinator.AllocatePendingOpenSlot("partial-open", CreateOpenTrigger());

        Assert.NotNull(slot);
        Assert.Equal(PositionSlotStatus.PendingOpen, slot!.Status);
        Assert.False(coordinator.TryClaimSlotClose(
            "partial-open",
            CloseExecutionOwner.Auto,
            DateTime.UtcNow));
        Assert.Equal(CloseExecutionOwner.None, slot.CloseOwner);
    }

    [Fact]
    public void TradeGate_WhenSlotAlreadyPendingClose_StillProtectsDispatch()
    {
        // Regression Phase 8: ProcessSnapshot (line ~179) pre-mark slot PendingClose
        // via slot.MarkCloseTriggered trước khi caller dispatch close. Khi
        // AutoCloseOrderAsync pre-marks PendingClose trước router. Transition gate vẫn phải
        // chấp nhận đúng slot context và commit Close reservation atomically.
        var coordinator = CreateCoordinator();
        coordinator.UpdatePostOpenLockConfig(0);
        var triggerTime = new DateTime(2026, 5, 21, 10, 0, 30, DateTimeKind.Utc);
        var slot = coordinator.AllocatePendingOpenSlot("p1", CreateOpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, triggerTime.AddMinutes(-1));

        // Mô phỏng ProcessSnapshot line 179: slot pre-mark PendingClose.
        slot!.MarkCloseTriggered(triggerTime);
        Assert.Equal(PositionSlotStatus.PendingClose, slot.Status);

        var gate = coordinator.TryAcquireTradeAction(
            triggerTime, "CLOSE", "test", side: TradingPositionSide.Buy, pairId: "p1");
        coordinator.MarkSlotCloseTriggered("p1", triggerTime);

        Assert.True(gate.Acquired);
        Assert.InRange(gate.CooldownSeconds, 3, 10);
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
        coordinator.UpdateProfit(666, 1.5);

        Assert.Equal(14.0, slot!.LastProfitSnapshot);
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
