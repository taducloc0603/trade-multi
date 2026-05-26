using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Phase 0 contract test: PortfolioCoordinatorAdapter behavior MUST mirror TradingFlowEngine.
// Mirrors TradingFlowEngineTests 1:1 with adapter as SUT.
public sealed class PortfolioCoordinatorAdapterTests
{
    private static readonly GapSignalConfirmationConfig Config = new(
        ConfirmGapPts: 5,
        OpenPts: 8,
        HoldConfirmMs: 500,
        CloseConfirmGapPts: 5,
        ClosePts: 8,
        CloseHoldConfirmMs: 400);

    private static readonly GapSignalConfirmationConfig ConfigWithTimeGuards = new(
        ConfirmGapPts: 5,
        OpenPts: 8,
        HoldConfirmMs: 500,
        CloseConfirmGapPts: 5,
        ClosePts: 8,
        CloseHoldConfirmMs: 400,
        StartTimeHold: 2,
        EndTimeHold: 2,
        StartWaitTime: 3,
        EndWaitTime: 3);

    private static ITradingFlowEngine CreateSut()
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory());
        return new PortfolioCoordinatorAdapter(coordinator);
    }

    [Fact]
    public void ProcessSnapshot_RunsSequentialFlow_OpenBuyThenCloseBuy()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 15, 20, 0, DateTimeKind.Utc);

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
        Assert.Equal(TradingPositionSide.None, sut.CurrentPositionSide);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null));
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: 6, gapSell: null));
        Assert.Null(Process(sut, start.AddMilliseconds(300), gapBuy: 7, gapSell: null));

        var open = Process(sut, start.AddMilliseconds(520), gapBuy: 8, gapSell: null);
        Assert.NotNull(open);
        Assert.Equal(GapSignalAction.Open, open!.Action);
        Assert.Equal(GapSignalTriggerType.OpenByGapBuy, open.TriggerType);
        Assert.Equal(GapSignalSide.Buy, open.PrimarySide);
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapBuy, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Buy, sut.CurrentPositionSide);

        Assert.Null(Process(sut, start.AddMilliseconds(620), gapBuy: 20, gapSell: null));
        Assert.Null(Process(sut, start.AddMilliseconds(700), gapBuy: 20, gapSell: -5));
        Assert.Null(Process(sut, start.AddMilliseconds(860), gapBuy: 20, gapSell: -6));

        var close = Process(sut, start.AddMilliseconds(1110), gapBuy: 20, gapSell: -8);
        Assert.NotNull(close);
        Assert.Equal(GapSignalAction.Close, close!.Action);
        Assert.Equal(GapSignalTriggerType.CloseByGapSell, close.TriggerType);
        Assert.Equal(GapSignalSide.Buy, close.PrimarySide);

        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        sut.BeginWaitAfterClose(close.TriggeredAtUtc, startWaitSeconds: 0, endWaitSeconds: 0);

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.None, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.None, sut.CurrentPositionSide);
    }

    [Fact]
    public void ProcessSnapshot_RunsSequentialFlow_OpenSellThenCloseSell()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 15, 25, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: null, gapSell: -5));
        Assert.Null(Process(sut, start.AddMilliseconds(200), gapBuy: null, gapSell: -6));
        var open = Process(sut, start.AddMilliseconds(540), gapBuy: null, gapSell: -8);

        Assert.NotNull(open);
        Assert.Equal(GapSignalTriggerType.OpenByGapSell, open!.TriggerType);
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapSell, sut.CurrentPhase);
        Assert.Equal(TradingPositionSide.Sell, sut.CurrentPositionSide);

        Assert.Null(Process(sut, start.AddMilliseconds(640), gapBuy: 5, gapSell: -20));
        Assert.Null(Process(sut, start.AddMilliseconds(820), gapBuy: 6, gapSell: -20));
        var close = Process(sut, start.AddMilliseconds(1060), gapBuy: 8, gapSell: -20);

        Assert.NotNull(close);
        Assert.Equal(GapSignalAction.Close, close!.Action);
        Assert.Equal(GapSignalTriggerType.CloseByGapBuy, close.TriggerType);

        sut.BeginWaitAfterClose(close.TriggeredAtUtc, startWaitSeconds: 0, endWaitSeconds: 0);
        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
    }

    [Fact]
    public void Reset_ClearsPhaseAndPosition()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 15, 30, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null);
        _ = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null);

        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        sut.Reset();

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.None, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.None, sut.CurrentPositionSide);
    }

    [Fact]
    public void ProcessSnapshot_WhenHoldingTimeNotReached_DoesNotCheckCloseUntilElapsed()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 16, 0, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null, ConfigWithTimeGuards);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(open);
        Assert.Equal(2, sut.CurrentHoldingSeconds);

        // Before 2s elapsed, close blocked.
        Assert.Null(Process(sut, start.AddMilliseconds(1200), gapBuy: null, gapSell: -20, ConfigWithTimeGuards));
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        // After 2s elapsed, close trigger.
        Assert.Null(Process(sut, start.AddMilliseconds(2600), gapBuy: null, gapSell: -5, ConfigWithTimeGuards));
        Assert.Null(Process(sut, start.AddMilliseconds(2800), gapBuy: null, gapSell: -6, ConfigWithTimeGuards));
        var close = Process(sut, start.AddMilliseconds(3200), gapBuy: null, gapSell: -8, ConfigWithTimeGuards);
        Assert.NotNull(close);

        sut.BeginWaitAfterClose(close!.TriggeredAtUtc, 0, 0);
        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
    }

    [Fact]
    public void ProcessSnapshot_WhenWaitingTimeNotReached_DoesNotCheckOpenUntilElapsed()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 16, 10, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null, ConfigWithTimeGuards);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(open);

        _ = Process(sut, start.AddMilliseconds(2600), gapBuy: null, gapSell: -5, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(2800), gapBuy: null, gapSell: -6, ConfigWithTimeGuards);
        var close = Process(sut, start.AddMilliseconds(3200), gapBuy: null, gapSell: -8, ConfigWithTimeGuards);
        Assert.NotNull(close);

        Assert.Equal(0, sut.CurrentWaitSeconds);
        Assert.Null(sut.ClosedAtUtc);

        var closeCompletedAt = start.AddMilliseconds(3300);
        sut.BeginWaitAfterClose(closeCompletedAt, startWaitSeconds: 3, endWaitSeconds: 3);

        Assert.Equal(3, sut.CurrentWaitSeconds);
        Assert.Equal(closeCompletedAt, sut.ClosedAtUtc);
        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);

        // Before 3s elapsed, open blocked.
        Assert.Null(Process(sut, start.AddMilliseconds(4200), gapBuy: 10, gapSell: null, ConfigWithTimeGuards));
        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);

        // After 3s elapsed, open allowed.
        Assert.Null(Process(sut, start.AddMilliseconds(6400), gapBuy: 5, gapSell: null, ConfigWithTimeGuards));
        Assert.Null(Process(sut, start.AddMilliseconds(6600), gapBuy: 6, gapSell: null, ConfigWithTimeGuards));
        var nextOpen = Process(sut, start.AddMilliseconds(7100), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(nextOpen);
        Assert.Equal(GapSignalAction.Open, nextOpen!.Action);
    }

    [Fact]
    public void ProcessSnapshot_WhenRangeMinGreaterThanMax_SwapsWithoutCrash()
    {
        var sut = CreateSut();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5, OpenPts: 8, HoldConfirmMs: 500,
            CloseConfirmGapPts: 5, ClosePts: 8, CloseHoldConfirmMs: 400,
            StartTimeHold: 9, EndTimeHold: 4,
            StartWaitTime: 7, EndWaitTime: 2);

        var start = new DateTime(2026, 3, 18, 16, 20, 0, DateTimeKind.Utc);
        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, config);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null, config);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null, config);

        Assert.NotNull(open);
        Assert.InRange(sut.CurrentHoldingSeconds, 4, 9);
    }

    [Fact]
    public void BeginWaitAfterClose_WhenPendingWasClearedButStillWaitingClose_CanStillTransitionToWaitingOpen()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 16, 25, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null);
        Assert.NotNull(open);

        _ = Process(sut, start.AddMilliseconds(700), gapBuy: 20, gapSell: -5);
        _ = Process(sut, start.AddMilliseconds(860), gapBuy: 20, gapSell: -6);
        var close = Process(sut, start.AddMilliseconds(1110), gapBuy: 20, gapSell: -8);
        Assert.NotNull(close);

        sut.AbortPendingCloseExecution();
        // After abort, slot might be cleared. BeginWaitAfterClose should still work resiliently.
        sut.BeginWaitAfterClose(close!.TriggeredAtUtc, 0, 0);

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
    }

    [Fact]
    public void AbortPendingOpenExecution_WhenLiveSlotExists_DoesNotRemoveLiveSlot()
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory());
        var sut = new PortfolioCoordinatorAdapter(coordinator);

        coordinator.RegisterSyncedSlot(
            pairId: "AUTO-0000-1",
            side: TradingPositionSide.Buy,
            openMode: TradingOpenMode.GapBuy,
            ticketA: 100,
            ticketB: 200,
            openConfirmedAtUtc: DateTime.UtcNow,
            holdingSeconds: 10);

        sut.AbortPendingOpenExecution();

        Assert.Equal(1, coordinator.LiveCount);
        Assert.NotNull(coordinator.GetSlotByPairId("AUTO-0000-1"));
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);
    }

    [Fact]
    public void ForceWaitingClose_WhenBuy_SetsWaitingCloseFromGapBuy()
    {
        var sut = CreateSut();

        sut.ForceWaitingClose(TradingPositionSide.Buy);

        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapBuy, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Buy, sut.CurrentPositionSide);
        Assert.NotNull(sut.OpenedAtUtc);
    }

    [Fact]
    public void ForceWaitingClose_WhenSell_SetsWaitingCloseFromGapSell()
    {
        var sut = CreateSut();

        sut.ForceWaitingClose(TradingPositionSide.Sell);

        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapSell, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapSell, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Sell, sut.CurrentPositionSide);
        Assert.NotNull(sut.OpenedAtUtc);
    }

    [Fact]
    public void ForceWaitingOpen_ClearsPositionAndReturnsToWaitingOpen()
    {
        var sut = CreateSut();
        sut.ForceWaitingClose(TradingPositionSide.Buy);

        sut.ForceWaitingOpen();

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.None, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.None, sut.CurrentPositionSide);
        Assert.Null(sut.OpenedAtUtc);
        Assert.Null(sut.ClosedAtUtc);
        Assert.Equal(0, sut.CurrentHoldingSeconds);
        Assert.Equal(0, sut.CurrentWaitSeconds);
    }

    [Fact]
    public void ForceWaitingClose_WhenHoldingSecondsAlreadySet_Preserves()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 17, 0, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null, ConfigWithTimeGuards);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(open);
        Assert.Equal(2, sut.CurrentHoldingSeconds);

        sut.ForceWaitingClose(TradingPositionSide.Buy);

        Assert.Equal(2, sut.CurrentHoldingSeconds);
    }

    [Fact]
    public void ForceWaitingClose_WhenHoldingSecondsZero_UsesConfigFloorFromLastSeen()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 17, 5, 0, DateTimeKind.Utc);

        _ = Process(sut, start, gapBuy: null, gapSell: null, ConfigWithTimeGuards);
        Assert.Equal(0, sut.CurrentHoldingSeconds);

        sut.ForceWaitingClose(TradingPositionSide.Sell);

        Assert.Equal(2, sut.CurrentHoldingSeconds);
    }

    [Fact]
    public void ProcessSnapshot_AfterForceWaitingClose_CloseStillGatedByHolding()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 17, 10, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null, ConfigWithTimeGuards);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(open);
        Assert.Equal(2, sut.CurrentHoldingSeconds);

        sut.ForceWaitingClose(TradingPositionSide.Buy);
        Assert.Equal(2, sut.CurrentHoldingSeconds);

        Assert.Null(Process(sut, start.AddMilliseconds(1500), gapBuy: null, gapSell: -8, ConfigWithTimeGuards));
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        Assert.Null(Process(sut, start.AddMilliseconds(2600), gapBuy: null, gapSell: -5, ConfigWithTimeGuards));
        Assert.Null(Process(sut, start.AddMilliseconds(2800), gapBuy: null, gapSell: -6, ConfigWithTimeGuards));
        var close = Process(sut, start.AddMilliseconds(3200), gapBuy: null, gapSell: -8, ConfigWithTimeGuards);
        Assert.NotNull(close);
        Assert.Equal(GapSignalAction.Close, close!.Action);
    }

    [Fact]
    public void CanCheckClose_FallbackFloor_EvenWhenHoldingSecondsExternallyZero()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 3, 18, 17, 20, 0, DateTimeKind.Utc);

        _ = Process(sut, start, gapBuy: null, gapSell: null, ConfigWithTimeGuards);
        sut.ForceWaitingClose(TradingPositionSide.Sell);
        Assert.Equal(2, sut.CurrentHoldingSeconds);

        Assert.Null(Process(sut, start.AddMilliseconds(500), gapBuy: 8, gapSell: null, ConfigWithTimeGuards));
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapSell, sut.CurrentPhase);
    }

    private static GapSignalTriggerResult? Process(
        ITradingFlowEngine sut,
        DateTime timestampUtc,
        int? gapBuy,
        int? gapSell,
        GapSignalConfirmationConfig? config = null)
        => sut.ProcessSnapshot(
            new GapSignalSnapshot(timestampUtc, 2945.12m, 2945.34m, 2945.56m, 2945.78m, gapBuy, gapSell, 1),
            config ?? Config);
}
