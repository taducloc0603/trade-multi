using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class TradingFlowEngineTests
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

    [Fact]
    public void ProcessSnapshot_RunsSequentialFlow_OpenBuyThenCloseBuy()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
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
        Assert.Equal(2945.12m, open.LastABid);
        Assert.Equal(2945.34m, open.LastAAsk);
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapBuy, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Buy, sut.CurrentPositionSide);

        // WaitingClose: should ignore open checks even when GAP_BUY is strong.
        Assert.Null(Process(sut, start.AddMilliseconds(620), gapBuy: 20, gapSell: null));

        Assert.Null(Process(sut, start.AddMilliseconds(700), gapBuy: 20, gapSell: -5));
        Assert.Null(Process(sut, start.AddMilliseconds(860), gapBuy: 20, gapSell: -6));

        var close = Process(sut, start.AddMilliseconds(1110), gapBuy: 20, gapSell: -8);
        Assert.NotNull(close);
        Assert.Equal(GapSignalAction.Close, close!.Action);
        Assert.Equal(GapSignalTriggerType.CloseByGapSell, close.TriggerType);
        Assert.Equal(GapSignalSide.Buy, close.PrimarySide);
        Assert.Equal(2945.12m, close.LastABid);
        Assert.Equal(2945.34m, close.LastAAsk);

        // After close signal, flow waits for external close execution confirmation.
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapBuy, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Buy, sut.CurrentPositionSide);

        sut.BeginWaitAfterClose(close.TriggeredAtUtc, startWaitSeconds: 0, endWaitSeconds: 0);

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.None, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.None, sut.CurrentPositionSide);
    }

    [Fact]
    public void ProcessSnapshot_RunsSequentialFlow_OpenSellThenCloseSell()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var start = new DateTime(2026, 3, 18, 15, 25, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: null, gapSell: -5));
        Assert.Null(Process(sut, start.AddMilliseconds(200), gapBuy: null, gapSell: -6));
        var open = Process(sut, start.AddMilliseconds(540), gapBuy: null, gapSell: -8);

        Assert.NotNull(open);
        Assert.Equal(GapSignalAction.Open, open!.Action);
        Assert.Equal(GapSignalTriggerType.OpenByGapSell, open.TriggerType);
        Assert.Equal(GapSignalSide.Sell, open.PrimarySide);
        Assert.Equal(2945.12m, open.LastABid);
        Assert.Equal(2945.34m, open.LastAAsk);
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapSell, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapSell, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Sell, sut.CurrentPositionSide);

        Assert.Null(Process(sut, start.AddMilliseconds(640), gapBuy: 5, gapSell: -20));
        Assert.Null(Process(sut, start.AddMilliseconds(820), gapBuy: 6, gapSell: -20));
        var close = Process(sut, start.AddMilliseconds(1060), gapBuy: 8, gapSell: -20);

        Assert.NotNull(close);
        Assert.Equal(GapSignalAction.Close, close!.Action);
        Assert.Equal(GapSignalTriggerType.CloseByGapBuy, close.TriggerType);
        Assert.Equal(GapSignalSide.Sell, close.PrimarySide);
        Assert.Equal(2945.12m, close.LastABid);
        Assert.Equal(2945.34m, close.LastAAsk);

        // After close signal, flow waits for external close execution confirmation.
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapSell, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapSell, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Sell, sut.CurrentPositionSide);

        sut.BeginWaitAfterClose(close.TriggeredAtUtc, startWaitSeconds: 0, endWaitSeconds: 0);

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.None, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.None, sut.CurrentPositionSide);
    }

    [Fact]
    public void Reset_ClearsPhaseAndPosition()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var start = new DateTime(2026, 3, 18, 15, 30, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null);
        _ = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null);

        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapBuy, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Buy, sut.CurrentPositionSide);

        sut.Reset();

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.None, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.None, sut.CurrentPositionSide);
    }

    [Fact]
    public void ProcessSnapshot_WhenHoldingTimeNotReached_DoesNotCheckCloseUntilElapsed()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var start = new DateTime(2026, 3, 18, 16, 0, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null, ConfigWithTimeGuards);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);

        Assert.NotNull(open);
        Assert.Equal(2, sut.CurrentHoldingSeconds);
        Assert.Equal(open!.TriggeredAtUtc, sut.OpenedAtUtc);

        // Before 2s elapsed, close is blocked.
        Assert.Null(Process(sut, start.AddMilliseconds(1200), gapBuy: null, gapSell: -20, ConfigWithTimeGuards));
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        // After 2s elapsed, close checks can run and trigger with valid close window.
        Assert.Null(Process(sut, start.AddMilliseconds(2600), gapBuy: null, gapSell: -5, ConfigWithTimeGuards));
        Assert.Null(Process(sut, start.AddMilliseconds(2800), gapBuy: null, gapSell: -6, ConfigWithTimeGuards));
        var close = Process(sut, start.AddMilliseconds(3200), gapBuy: null, gapSell: -8, ConfigWithTimeGuards);

        Assert.NotNull(close);

        // Not waiting-open until external close execution confirmation.
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        sut.BeginWaitAfterClose(close!.TriggeredAtUtc, startWaitSeconds: 0, endWaitSeconds: 0);

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
    }

    [Fact]
    public void ProcessSnapshot_WhenWaitingTimeNotReached_DoesNotCheckOpenUntilElapsed()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var start = new DateTime(2026, 3, 18, 16, 10, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null, ConfigWithTimeGuards);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(open);

        // reach close
        _ = Process(sut, start.AddMilliseconds(2600), gapBuy: null, gapSell: -5, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(2800), gapBuy: null, gapSell: -6, ConfigWithTimeGuards);
        var close = Process(sut, start.AddMilliseconds(3200), gapBuy: null, gapSell: -8, ConfigWithTimeGuards);
        Assert.NotNull(close);

        // Wait timer starts only after external close execution confirmation.
        Assert.Equal(0, sut.CurrentWaitSeconds);
        Assert.Null(sut.ClosedAtUtc);
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        var closeCompletedAt = start.AddMilliseconds(3300);
        sut.BeginWaitAfterClose(closeCompletedAt, startWaitSeconds: 3, endWaitSeconds: 3);

        Assert.Equal(3, sut.CurrentWaitSeconds);
        Assert.Equal(closeCompletedAt, sut.ClosedAtUtc);
        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);

        // Before 3s elapsed, open is blocked.
        Assert.Null(Process(sut, start.AddMilliseconds(4200), gapBuy: 10, gapSell: null, ConfigWithTimeGuards));
        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.None, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.None, sut.CurrentPositionSide);

        // After waiting time elapsed, open can run.
        Assert.Null(Process(sut, start.AddMilliseconds(6400), gapBuy: 5, gapSell: null, ConfigWithTimeGuards));
        Assert.Null(Process(sut, start.AddMilliseconds(6600), gapBuy: 6, gapSell: null, ConfigWithTimeGuards));
        var nextOpen = Process(sut, start.AddMilliseconds(7100), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(nextOpen);
        Assert.Equal(GapSignalAction.Open, nextOpen!.Action);
    }

    [Fact]
    public void ProcessSnapshot_WhenRangeMinGreaterThanMax_SwapsWithoutCrash()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 400,
            StartTimeHold: 9,
            EndTimeHold: 4,
            StartWaitTime: 7,
            EndWaitTime: 2);

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
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var start = new DateTime(2026, 3, 18, 16, 25, 0, DateTimeKind.Utc);

        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null);
        Assert.NotNull(open);
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        _ = Process(sut, start.AddMilliseconds(700), gapBuy: 20, gapSell: -5);
        _ = Process(sut, start.AddMilliseconds(860), gapBuy: 20, gapSell: -6);
        var close = Process(sut, start.AddMilliseconds(1110), gapBuy: 20, gapSell: -8);
        Assert.NotNull(close);

        // Simulate a premature abort path that clears pending flag,
        // while engine is still in waiting-close phase.
        sut.AbortPendingCloseExecution();
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        // External confirmation still needs to complete the cycle.
        sut.BeginWaitAfterClose(close!.TriggeredAtUtc, startWaitSeconds: 0, endWaitSeconds: 0);

        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.None, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.None, sut.CurrentPositionSide);
    }

    [Fact]
    public void ProcessSnapshot_WhenSnapshotTimestampStaleNearNow_WaitGateCanProgressByWallClock()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var nowUtc = DateTime.UtcNow;

        // Move to waiting-open with wait gate = 1 second.
        _ = Process(sut, nowUtc.AddMilliseconds(-600), gapBuy: 5, gapSell: null, ConfigWithTimeGuards);
        _ = Process(sut, nowUtc.AddMilliseconds(-400), gapBuy: 6, gapSell: null, ConfigWithTimeGuards);
        var open = Process(sut, nowUtc.AddMilliseconds(-100), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(open);

        _ = Process(sut, nowUtc.AddMilliseconds(2200), gapBuy: null, gapSell: -5, ConfigWithTimeGuards);
        _ = Process(sut, nowUtc.AddMilliseconds(2400), gapBuy: null, gapSell: -6, ConfigWithTimeGuards);
        var close = Process(sut, nowUtc.AddMilliseconds(2800), gapBuy: null, gapSell: -8, ConfigWithTimeGuards);
        Assert.NotNull(close);
        sut.BeginWaitAfterClose(DateTime.UtcNow, startWaitSeconds: 1, endWaitSeconds: 1);

        // Feed a stale snapshot timestamp (frozen around "now").
        var staleSnapshotTs = nowUtc;
        Assert.Null(Process(sut, staleSnapshotTs, gapBuy: 10, gapSell: null, ConfigWithTimeGuards));

        Thread.Sleep(1200);

        // With wall-clock fallback, open checks should proceed even when snapshot timestamp is stale.
        Assert.Null(Process(sut, staleSnapshotTs, gapBuy: 5, gapSell: null, ConfigWithTimeGuards));
        Assert.Null(Process(sut, staleSnapshotTs, gapBuy: 6, gapSell: null, ConfigWithTimeGuards));
        var nextOpen = Process(sut, staleSnapshotTs, gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(nextOpen);
        Assert.Equal(GapSignalAction.Open, nextOpen!.Action);
    }

    [Fact]
    public void ForceWaitingClose_WhenBuy_SetsWaitingCloseFromGapBuy()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());

        sut.ForceWaitingClose(TradingPositionSide.Buy);

        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapBuy, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Buy, sut.CurrentPositionSide);
        Assert.NotNull(sut.OpenedAtUtc);
    }

    [Fact]
    public void ForceWaitingClose_WhenSell_SetsWaitingCloseFromGapSell()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());

        sut.ForceWaitingClose(TradingPositionSide.Sell);

        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapSell, sut.CurrentPhase);
        Assert.Equal(TradingOpenMode.GapSell, sut.CurrentOpenMode);
        Assert.Equal(TradingPositionSide.Sell, sut.CurrentPositionSide);
        Assert.NotNull(sut.OpenedAtUtc);
    }

    [Fact]
    public void ForceWaitingOpen_ClearsPositionAndReturnsToWaitingOpen()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
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
        // Bug cũ: ForceWaitingClose set CurrentHoldingSeconds = 0,
        // làm CanCheckClose return true ngay → close signal chạy tự do.
        // Fix: giữ nguyên giá trị đã random lúc open trigger.

        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var start = new DateTime(2026, 3, 18, 17, 0, 0, DateTimeKind.Utc);

        // Happy-path open → CurrentHoldingSeconds = 2 (vì Start=End=2 trong ConfigWithTimeGuards)
        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null, ConfigWithTimeGuards);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(open);
        Assert.Equal(2, sut.CurrentHoldingSeconds);

        // Giả lập sync state từ live pair (ví dụ app restart giữa chu kỳ) —
        // caller có thể gọi ForceWaitingClose để restore phase.
        sut.ForceWaitingClose(TradingPositionSide.Buy);

        // Với fix: giữ nguyên holding đã có.
        Assert.Equal(2, sut.CurrentHoldingSeconds);
    }

    [Fact]
    public void ForceWaitingClose_WhenHoldingSecondsZero_UsesConfigFloorFromLastSeen()
    {
        // Kịch bản: app vừa start, chưa từng trigger open trong engine;
        // caller gọi ForceWaitingClose để sync state vì MMF có lệnh tool từ session trước.
        // Fix: phải random lại hold từ config đã thấy gần nhất, không để = 0.

        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var start = new DateTime(2026, 3, 18, 17, 5, 0, DateTimeKind.Utc);

        // Chỉ feed 1 snapshot để engine cache _lastSeenStartTimeHold/_lastSeenEndTimeHold = 2
        _ = Process(sut, start, gapBuy: null, gapSell: null, ConfigWithTimeGuards);
        Assert.Equal(0, sut.CurrentHoldingSeconds);

        sut.ForceWaitingClose(TradingPositionSide.Sell);

        // Không còn = 0 nữa — fallback về [StartTimeHold..EndTimeHold] = [2..2]
        Assert.Equal(2, sut.CurrentHoldingSeconds);
    }

    [Fact]
    public void ProcessSnapshot_AfterForceWaitingClose_CloseStillGatedByHolding()
    {
        // End-to-end: sau khi ForceWaitingClose, close-gate vẫn phải chặn
        // signal close nếu chưa đủ holding seconds.

        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var start = new DateTime(2026, 3, 18, 17, 10, 0, DateTimeKind.Utc);

        // Open bình thường
        _ = Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, ConfigWithTimeGuards);
        _ = Process(sut, start.AddMilliseconds(200), gapBuy: 6, gapSell: null, ConfigWithTimeGuards);
        var open = Process(sut, start.AddMilliseconds(550), gapBuy: 8, gapSell: null, ConfigWithTimeGuards);
        Assert.NotNull(open);
        Assert.Equal(2, sut.CurrentHoldingSeconds);

        // Ngay lập tức ForceWaitingClose (simulate sync path)
        sut.ForceWaitingClose(TradingPositionSide.Buy);
        Assert.Equal(2, sut.CurrentHoldingSeconds); // không reset về 0

        // 1 giây sau open — close signal xuất hiện nhưng chưa đủ 2s hold → phải bị chặn
        Assert.Null(Process(sut, start.AddMilliseconds(1500), gapBuy: null, gapSell: -8, ConfigWithTimeGuards));
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        // 2.6s sau open — close-gate mở, engine bắt đầu cho close-signal engine chạy.
        // Cần bơm đủ tick (CloseHoldConfirmMs=400, ClosePts=8 trong ConfigWithTimeGuards)
        Assert.Null(Process(sut, start.AddMilliseconds(2600), gapBuy: null, gapSell: -5, ConfigWithTimeGuards));
        Assert.Null(Process(sut, start.AddMilliseconds(2800), gapBuy: null, gapSell: -6, ConfigWithTimeGuards));
        var close = Process(sut, start.AddMilliseconds(3200), gapBuy: null, gapSell: -8, ConfigWithTimeGuards);
        Assert.NotNull(close);
        Assert.Equal(GapSignalAction.Close, close!.Action);
    }

    [Fact]
    public void CanCheckClose_FallbackFloor_EvenWhenHoldingSecondsExternallyZero()
    {
        // Defense-in-depth: nếu vì bất kỳ lý do nào CurrentHoldingSeconds = 0
        // trong khi phase là WaitingClose và OpenedAtUtc có giá trị,
        // close-gate phải dùng _lastSeenStartTimeHold làm floor.

        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var start = new DateTime(2026, 3, 18, 17, 20, 0, DateTimeKind.Utc);

        // Feed 1 tick để engine cache config hold range = [2..2]
        _ = Process(sut, start, gapBuy: null, gapSell: null, ConfigWithTimeGuards);

        // ForceWaitingClose sẽ random hold từ [2..2] = 2 do CurrentHoldingSeconds=0
        sut.ForceWaitingClose(TradingPositionSide.Sell);
        Assert.Equal(2, sut.CurrentHoldingSeconds);

        // Tại T=500ms sau force — close signal KHÔNG được fire (chưa đủ 2s)
        Assert.Null(Process(sut, start.AddMilliseconds(500), gapBuy: 8, gapSell: null, ConfigWithTimeGuards));
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapSell, sut.CurrentPhase);
    }

    [Fact]
    public void ProcessSnapshot_WhenOpenSpikeDetected_StartsCooldownAndBlocksOpenUntilElapsed()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 0,
            OpenGapTick: 2,
            CloseGapTick: 2,
            CoolDownGapTick: 2);
        var start = new DateTime(2026, 3, 18, 18, 0, 0, DateTimeKind.Utc);

        // Establish previous ask values.
        Assert.Null(ProcessWithPrices(sut, start, 2945.12m, 2945.34m, 2945.56m, 2945.78m, gapBuy: null, gapSell: null, config, pointMultiplier: 100));

        // Ask spike on A: abs(2945.40 - 2945.34) * 100 = 6 > open_gap_tick(2) -> cooldown starts.
        Assert.Null(ProcessWithPrices(sut, start.AddMilliseconds(100), 2945.12m, 2945.40m, 2945.56m, 2945.78m, gapBuy: 8, gapSell: null, config, pointMultiplier: 100));
        Assert.NotNull(sut.LastSkipDiagnostic);
        Assert.Equal("GAP_COOLDOWN_ACTIVE", sut.LastSkipDiagnostic!.Reason);
        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.LastSkipDiagnostic.Phase);
        Assert.True(sut.LastSkipDiagnostic.CooldownLeftMs > 0);
        Assert.Equal(2, sut.LastSkipDiagnostic.OpenGapTick);
        Assert.Equal(2, sut.LastSkipDiagnostic.CloseGapTick);

        // During cooldown, open signal must still be blocked.
        Assert.Null(ProcessWithPrices(sut, start.AddMilliseconds(1500), 2945.13m, 2945.41m, 2945.56m, 2945.78m, gapBuy: 8, gapSell: null, config, pointMultiplier: 100));
        Assert.Equal(TradingFlowPhase.WaitingOpen, sut.CurrentPhase);

        // After cooldown elapsed, open can trigger again.
        var open = ProcessWithPrices(sut, start.AddMilliseconds(2200), 2945.14m, 2945.42m, 2945.57m, 2945.79m, gapBuy: 8, gapSell: null, config, pointMultiplier: 100);
        Assert.NotNull(open);
        Assert.Equal(GapSignalAction.Open, open!.Action);
        Assert.Null(sut.LastSkipDiagnostic);
    }

    [Fact]
    public void ProcessSnapshot_WhenCloseSpikeDetected_StartsCooldownAndBlocksCloseUntilElapsed()
    {
        var sut = new TradingFlowEngine(new GapSignalConfirmationEngine(), new CloseSignalEngine());
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 0,
            OpenGapTick: 2,
            CloseGapTick: 2,
            CoolDownGapTick: 2);
        var start = new DateTime(2026, 3, 18, 18, 10, 0, DateTimeKind.Utc);

        // Open first to enter waiting-close phase.
        Assert.Null(ProcessWithPrices(sut, start, 2945.12m, 2945.34m, 2945.56m, 2945.78m, gapBuy: 5, gapSell: null, config, pointMultiplier: 100));
        var open = ProcessWithPrices(sut, start.AddMilliseconds(100), 2945.12m, 2945.34m, 2945.56m, 2945.78m, gapBuy: 8, gapSell: null, config, pointMultiplier: 100);
        Assert.NotNull(open);
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        // Ask spike while waiting-close: abs(2945.42 - 2945.34) * 100 = 8 > close_gap_tick(2).
        Assert.Null(ProcessWithPrices(sut, start.AddMilliseconds(200), 2945.12m, 2945.42m, 2945.56m, 2945.78m, gapBuy: 20, gapSell: -8, config, pointMultiplier: 100));
        Assert.NotNull(sut.LastSkipDiagnostic);
        Assert.Equal("GAP_COOLDOWN_ACTIVE", sut.LastSkipDiagnostic!.Reason);
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.LastSkipDiagnostic.Phase);
        Assert.True(sut.LastSkipDiagnostic.CooldownLeftMs > 0);
        Assert.Equal(2, sut.LastSkipDiagnostic.OpenGapTick);
        Assert.Equal(2, sut.LastSkipDiagnostic.CloseGapTick);

        // Close remains blocked within cooldown window.
        Assert.Null(ProcessWithPrices(sut, start.AddMilliseconds(1500), 2945.12m, 2945.43m, 2945.56m, 2945.78m, gapBuy: 20, gapSell: -8, config, pointMultiplier: 100));
        Assert.Equal(TradingFlowPhase.WaitingCloseFromGapBuy, sut.CurrentPhase);

        // After cooldown elapsed, close can trigger.
        var close = ProcessWithPrices(sut, start.AddMilliseconds(2300), 2945.12m, 2945.44m, 2945.56m, 2945.78m, gapBuy: 20, gapSell: -8, config, pointMultiplier: 100);
        Assert.NotNull(close);
        Assert.Equal(GapSignalAction.Close, close!.Action);
    }

    private static GapSignalTriggerResult? Process(
        TradingFlowEngine sut,
        DateTime timestampUtc,
        int? gapBuy,
        int? gapSell,
        GapSignalConfirmationConfig? config = null)
        => sut.ProcessSnapshot(
            new GapSignalSnapshot(timestampUtc, 2945.12m, 2945.34m, 2945.56m, 2945.78m, gapBuy, gapSell, 1),
            config ?? Config);

    private static GapSignalTriggerResult? ProcessWithPrices(
        TradingFlowEngine sut,
        DateTime timestampUtc,
        decimal? aBid,
        decimal? aAsk,
        decimal? bBid,
        decimal? bAsk,
        int? gapBuy,
        int? gapSell,
        GapSignalConfirmationConfig config,
        int pointMultiplier)
        => sut.ProcessSnapshot(
            new GapSignalSnapshot(timestampUtc, aBid, aAsk, bBid, bAsk, gapBuy, gapSell, pointMultiplier),
            config);
}
