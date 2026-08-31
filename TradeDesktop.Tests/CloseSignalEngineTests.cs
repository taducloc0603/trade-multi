using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class CloseSignalEngineTests
{
    [Fact]
    public void ProcessSnapshot_TriggersCloseBuy_WhenGapSellMatchesReverseThresholds()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 400);
        var start = new DateTime(2026, 3, 18, 15, 0, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: -5, gapSell: -5, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: -6, gapSell: -6, config, TradingOpenMode.GapBuy));

        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: -8, gapSell: -8, config, TradingOpenMode.GapBuy);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalAction.Close, trigger!.Action);
        Assert.Equal(GapSignalTriggerType.CloseByGapSell, trigger.TriggerType);
        Assert.Equal(GapSignalSide.Buy, trigger.PrimarySide);
        Assert.Equal(new[] { -5, -6, -8 }, trigger.BuyGaps);
        Assert.Equal(new[] { -5, -6, -8 }, trigger.SellGaps);
        Assert.Equal(-8, trigger.LastBuyGap);
        Assert.Equal(-8, trigger.LastSellGap);
        Assert.Equal(2945.12m, trigger.LastABid);
        Assert.Equal(2945.34m, trigger.LastAAsk);
        Assert.Equal(2945.56m, trigger.LastBBid);
        Assert.Equal(2945.78m, trigger.LastBAsk);
    }

    [Fact]
    public void ProcessSnapshot_TriggersCloseSell_WhenGapBuyMatchesReverseThresholds()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 400);
        var start = new DateTime(2026, 3, 18, 15, 5, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: 5, config, TradingOpenMode.GapSell));
        Assert.Null(Process(sut, start.AddMilliseconds(220), gapBuy: 6, gapSell: 6, config, TradingOpenMode.GapSell));

        var trigger = Process(sut, start.AddMilliseconds(450), gapBuy: 8, gapSell: 8, config, TradingOpenMode.GapSell);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalAction.Close, trigger!.Action);
        Assert.Equal(GapSignalTriggerType.CloseByGapBuy, trigger.TriggerType);
        Assert.Equal(GapSignalSide.Sell, trigger.PrimarySide);
        Assert.Equal(new[] { 5, 6, 8 }, trigger.BuyGaps);
        Assert.Equal(new[] { 5, 6, 8 }, trigger.SellGaps);
        Assert.Equal(8, trigger.LastBuyGap);
        Assert.Equal(8, trigger.LastSellGap);
        Assert.Equal(2945.12m, trigger.LastABid);
        Assert.Equal(2945.34m, trigger.LastAAsk);
        Assert.Equal(2945.56m, trigger.LastBBid);
        Assert.Equal(2945.78m, trigger.LastBAsk);
    }

    [Fact]
    public void ProcessSnapshot_ResetsWindow_WhenAnyTickFailsCloseConfirm()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 300);
        var start = new DateTime(2026, 3, 18, 15, 10, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: -5, gapSell: -5, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: -6, gapSell: -6, config, TradingOpenMode.GapBuy));

        // Fails confirm -> reset close hold window.
        Assert.Null(Process(sut, start.AddMilliseconds(200), gapBuy: -4, gapSell: -4, config, TradingOpenMode.GapBuy));

        Assert.Null(Process(sut, start.AddMilliseconds(250), gapBuy: -5, gapSell: -5, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(400), gapBuy: -8, gapSell: -8, config, TradingOpenMode.GapBuy));

        var trigger = Process(sut, start.AddMilliseconds(600), gapBuy: -8, gapSell: -8, config, TradingOpenMode.GapBuy);

        Assert.NotNull(trigger);
        Assert.Equal(new[] { -5, -8, -8 }, trigger!.SellGaps);
        Assert.Equal(new[] { -5, -8, -8 }, trigger.BuyGaps);
    }

    [Fact]
    public void ProcessSnapshot_SosThresholds_CloseGapBuyAfterInwardHold()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 0,
            OpenPts: 1,
            HoldConfirmMs: 900,
            CloseConfirmGapPts: -15,
            ClosePts: -8,
            CloseHoldConfirmMs: 900,
            CloseGapMode: CloseGapMode.Sos);
        var start = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, gapBuy: 40, gapSell: 15, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(450), gapBuy: 50, gapSell: 13, config, TradingOpenMode.GapBuy));

        var trigger = Process(
            sut, start.AddMilliseconds(900), gapBuy: 60, gapSell: 8, config, TradingOpenMode.GapBuy);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalTriggerType.CloseByGapSell, trigger!.TriggerType);
        Assert.Equal(8, trigger.LastSellGap);
        Assert.Equal(CloseGapMode.Sos, trigger.CloseGapMode);
    }

    [Fact]
    public void ProcessSnapshot_SosThresholds_CloseGapSellAfterInwardHold()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 0,
            OpenPts: 1,
            HoldConfirmMs: 900,
            CloseConfirmGapPts: -15,
            ClosePts: -8,
            CloseHoldConfirmMs: 900,
            CloseGapMode: CloseGapMode.Sos);
        var start = new DateTime(2026, 8, 10, 10, 5, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, gapBuy: -15, gapSell: -40, config, TradingOpenMode.GapSell));
        Assert.Null(Process(sut, start.AddMilliseconds(450), gapBuy: -11, gapSell: -50, config, TradingOpenMode.GapSell));

        var trigger = Process(
            sut, start.AddMilliseconds(900), gapBuy: -8, gapSell: -60, config, TradingOpenMode.GapSell);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalTriggerType.CloseByGapBuy, trigger!.TriggerType);
        Assert.Equal(-8, trigger.LastBuyGap);
        Assert.Equal(CloseGapMode.Sos, trigger.CloseGapMode);
    }

    [Fact]
    public void ProcessSnapshot_SosThresholds_ResetWhenConfirmBreaksBeforeHold()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 0,
            OpenPts: 1,
            HoldConfirmMs: 900,
            CloseConfirmGapPts: -15,
            ClosePts: -8,
            CloseHoldConfirmMs: 900,
            CloseGapMode: CloseGapMode.Sos);
        var start = new DateTime(2026, 8, 10, 10, 10, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, gapBuy: 0, gapSell: 15, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(300), gapBuy: 0, gapSell: 17, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(400), gapBuy: 0, gapSell: 14, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(900), gapBuy: 0, gapSell: 10, config, TradingOpenMode.GapBuy));

        var trigger = Process(
            sut, start.AddMilliseconds(1_300), gapBuy: 0, gapSell: 8, config, TradingOpenMode.GapBuy);

        Assert.NotNull(trigger);
    }

    [Fact]
    public void ProcessSnapshot_SosGapBuy_ResetWhenConfirmBreaksBeforeHold()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 0,
            OpenPts: 1,
            HoldConfirmMs: 900,
            CloseConfirmGapPts: -15,
            ClosePts: -8,
            CloseHoldConfirmMs: 900,
            CloseGapMode: CloseGapMode.Sos);
        var start = new DateTime(2026, 8, 10, 10, 15, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, gapBuy: -15, gapSell: 0, config, TradingOpenMode.GapSell));
        Assert.Null(Process(sut, start.AddMilliseconds(300), gapBuy: -17, gapSell: 0, config, TradingOpenMode.GapSell));
        Assert.Null(Process(sut, start.AddMilliseconds(400), gapBuy: -14, gapSell: 0, config, TradingOpenMode.GapSell));
        Assert.Null(Process(sut, start.AddMilliseconds(900), gapBuy: -10, gapSell: 0, config, TradingOpenMode.GapSell));

        var trigger = Process(
            sut, start.AddMilliseconds(1_300), gapBuy: -8, gapSell: 0, config, TradingOpenMode.GapSell);

        Assert.NotNull(trigger);
    }

    [Fact]
    public void ProcessSnapshot_SosResetsAtHold_WhenFinalCloseThresholdIsNotMet()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 0,
            OpenPts: 1,
            HoldConfirmMs: 900,
            CloseConfirmGapPts: -15,
            ClosePts: -8,
            CloseHoldConfirmMs: 900,
            CloseGapMode: CloseGapMode.Sos);
        var start = new DateTime(2026, 8, 10, 10, 20, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, gapBuy: 0, gapSell: 15, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(900), gapBuy: 0, gapSell: 10, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(1_000), gapBuy: 0, gapSell: 8, config, TradingOpenMode.GapBuy));

        var trigger = Process(
            sut, start.AddMilliseconds(1_900), gapBuy: 0, gapSell: 8, config, TradingOpenMode.GapBuy);

        Assert.NotNull(trigger);
    }

    [Fact]
    public void ProcessSnapshot_DoesNotTrigger_WhenCloseMaxTimesTickExceeded()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 400,
            CloseMaxTimesTick: 3);
        var start = new DateTime(2026, 3, 18, 15, 20, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: null, gapSell: -5, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(120), gapBuy: null, gapSell: -6, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(250), gapBuy: null, gapSell: -7, config, TradingOpenMode.GapBuy));

        var trigger = Process(sut, start.AddMilliseconds(520), gapBuy: null, gapSell: -8, config, TradingOpenMode.GapBuy);

        Assert.Null(trigger);
    }

    [Fact]
    public void ProcessSnapshot_StillTriggers_WhenCloseMaxTimesTickIsZero()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 400,
            CloseMaxTimesTick: 0);
        var start = new DateTime(2026, 3, 18, 15, 25, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: -5, gapSell: -5, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(120), gapBuy: -6, gapSell: -6, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(250), gapBuy: -7, gapSell: -7, config, TradingOpenMode.GapBuy));

        var trigger = Process(sut, start.AddMilliseconds(520), gapBuy: -8, gapSell: -8, config, TradingOpenMode.GapBuy);

        Assert.NotNull(trigger);
    }

    [Fact]
    public void ProcessSnapshot_TriggersCloseByTp_WhenSlotProfitHoldsAndFinalTargetMatches()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseHoldConfirmMs: 400,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 15, 30, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 10));
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 12));

        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 15);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalAction.Close, trigger!.Action);
        Assert.Equal(CloseSignalReason.Tp, trigger.CloseReason);
        Assert.Equal(15, trigger.CloseTpProfit);
        Assert.Equal(15, trigger.CloseTpTarget);
        Assert.Equal(new[] { 10d, 12d, 15d }, trigger.CloseTpProfits);
    }

    [Fact]
    public void ProcessSnapshot_ResetsTpCycle_WhenProfitDropsBelowConfirm()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseHoldConfirmMs: 300,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 15, 35, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 10));
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 12));
        Assert.Null(Process(sut, start.AddMilliseconds(200), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 9));
        Assert.Null(Process(sut, start.AddMilliseconds(250), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 10));
        Assert.Null(Process(sut, start.AddMilliseconds(400), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 15));

        var trigger = Process(sut, start.AddMilliseconds(600), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 15);

        Assert.NotNull(trigger);
        Assert.Equal(new[] { 10d, 15d, 15d }, trigger!.CloseTpProfits);
    }

    [Fact]
    public void ProcessSnapshot_PrefersTpReason_WhenGapAndTpBothTrigger()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseHoldConfirmMs: 300,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 15, 40, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: null, gapSell: -5, config, TradingOpenMode.GapBuy, slotProfit: 10));
        Assert.Null(Process(sut, start.AddMilliseconds(150), gapBuy: null, gapSell: -6, config, TradingOpenMode.GapBuy, slotProfit: 12));

        var trigger = Process(sut, start.AddMilliseconds(350), gapBuy: null, gapSell: -8, config, TradingOpenMode.GapBuy, slotProfit: 15);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger!.CloseReason);
        Assert.Equal(GapSignalTriggerType.CloseByGapSell, trigger.TriggerType);
    }

    [Fact]
    public void ProcessSnapshot_LimitMaxTp_Disabled_WhenZero()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseHoldConfirmMs: 300,
            LimitMaxTp: 0,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 16, 0, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 100));
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 200));

        var trigger = Process(sut, start.AddMilliseconds(310), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 150);

        // LimitMaxTp=0 means disabled — profit lớn vẫn trigger bình thường
        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger!.CloseReason);
    }

    [Fact]
    public void ProcessSnapshot_LimitMaxTp_ResetsCycle_WhenProfitExceedsLimit()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseHoldConfirmMs: 300,
            LimitMaxTp: 50,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 16, 1, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 10));
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 20));
        // Spike vượt limitMaxTp=50 → reset cycle
        Assert.Null(Process(sut, start.AddMilliseconds(200), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 80));
        // Mở cycle mới sau reset
        Assert.Null(Process(sut, start.AddMilliseconds(250), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 15));

        // Cycle mới chỉ có 2/3 profit nên chưa đủ.
        var trigger = Process(sut, start.AddMilliseconds(500), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 20);
        Assert.Null(trigger);
    }

    [Fact]
    public void ProcessSnapshot_LimitMaxTp_PreventsCycleStart_WhenProfitExceedsLimitFromStart()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseHoldConfirmMs: 300,
            LimitMaxTp: 50,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 16, 2, 0, DateTimeKind.Utc);

        // Profit > limitMaxTp từ đầu → cycle không mở
        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 80));
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 60));

        var trigger = Process(sut, start.AddMilliseconds(400), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 55);
        Assert.Null(trigger);
    }

    [Fact]
    public void ProcessSnapshot_LimitMaxTp_NewCycle_AfterSpikeReset()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseHoldConfirmMs: 200,
            LimitMaxTp: 50,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 16, 3, 0, DateTimeKind.Utc);

        // Cycle 1: spike → reset
        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 12));
        Assert.Null(Process(sut, start.AddMilliseconds(50), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 80)); // spike reset

        // Cycle 2: profit về bình thường → cycle mở lại
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 15));
        Assert.Null(Process(sut, start.AddMilliseconds(200), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 20));

        var trigger = Process(sut, start.AddMilliseconds(310), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 18);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger!.CloseReason);
    }

    [Fact]
    public void ProcessSnapshot_CloseMaxTpProfit_RejectsCompletedCycleAndStartsFreshCycle()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseMaxTpProfit: 20,
            CloseHoldConfirmMs: 100,
            CloseMaxTimesTick: 0,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 16, 4, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, 0, 0, config, TradingOpenMode.GapBuy, 25));
        Assert.Null(Process(sut, start.AddMilliseconds(1), 0, 0, config, TradingOpenMode.GapBuy, 25));
        Assert.Null(Process(sut, start.AddMilliseconds(2), 0, 0, config, TradingOpenMode.GapBuy, 25));

        Assert.Null(Process(sut, start.AddMilliseconds(3), 0, 0, config, TradingOpenMode.GapBuy, 18));
        Assert.Null(Process(sut, start.AddMilliseconds(4), 0, 0, config, TradingOpenMode.GapBuy, 18));
        var trigger = Process(sut, start.AddMilliseconds(5), 0, 0, config, TradingOpenMode.GapBuy, 18);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger!.CloseReason);
        Assert.Equal(18, trigger.CloseTpProfit);
        Assert.Equal(new[] { 18d, 18d, 18d }, trigger.CloseTpProfits);
    }

    [Fact]
    public void ProcessSnapshot_CloseMaxTimesTick_IsIgnoredByFixedSizeTpCycle()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseMaxTpProfit: 20,
            CloseHoldConfirmMs: 100,
            CloseMaxTimesTick: 1,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 16, 5, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, 0, 0, config, TradingOpenMode.GapBuy, 18));
        Assert.Null(Process(sut, start.AddMilliseconds(1), 0, 0, config, TradingOpenMode.GapBuy, 18));
        var trigger = Process(sut, start.AddMilliseconds(2), 0, 0, config, TradingOpenMode.GapBuy, 18);
        Assert.NotNull(trigger);
    }

    [Fact]
    public void ProcessSnapshot_FixedSizeTen_TriggersTpOnlyOnTenthProfitAndIgnoresHoldTime()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseHoldConfirmMs: 999_999,
            SignalCycleSize: 10);
        var start = new DateTime(2026, 3, 18, 16, 6, 0, DateTimeKind.Utc);

        for (var index = 0; index < 9; index++)
        {
            Assert.Null(Process(
                sut,
                start.AddMilliseconds(index),
                0,
                0,
                config,
                TradingOpenMode.GapBuy,
                15 + index));
        }

        var trigger = Process(
            sut,
            start.AddMilliseconds(9),
            0,
            0,
            config,
            TradingOpenMode.GapBuy,
            24);
        Assert.NotNull(trigger);
        Assert.Equal(10, trigger!.CloseTpProfits!.Count);
    }

    [Fact]
    public void ProcessSnapshot_FixedSizeOne_TriggersTpFromFirstProfitAtTarget()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 0,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            CloseHoldConfirmMs: 999_999,
            SignalCycleSize: 1);
        var start = new DateTime(2026, 3, 18, 16, 7, 0, DateTimeKind.Utc);

        var trigger = Process(sut, start, 0, 0, config, TradingOpenMode.GapBuy, 15);

        Assert.NotNull(trigger);
        Assert.Equal([15d], trigger!.CloseTpProfits);
    }

    [Fact]
    public void ProcessSnapshot_CompletedTpCycleBelowTarget_StartsFreshCycle()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 16, 7, 30, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, 0, 0, config, TradingOpenMode.GapBuy, 10));
        Assert.Null(Process(sut, start.AddMilliseconds(1), 0, 0, config, TradingOpenMode.GapBuy, 12));
        Assert.Null(Process(sut, start.AddMilliseconds(2), 0, 0, config, TradingOpenMode.GapBuy, 14));

        Assert.Null(Process(sut, start.AddMilliseconds(3), 0, 0, config, TradingOpenMode.GapBuy, 15));
        Assert.Null(Process(sut, start.AddMilliseconds(4), 0, 0, config, TradingOpenMode.GapBuy, 16));
        var trigger = Process(sut, start.AddMilliseconds(5), 0, 0, config, TradingOpenMode.GapBuy, 17);

        Assert.NotNull(trigger);
        Assert.Equal(new[] { 15d, 16d, 17d }, trigger!.CloseTpProfits);
    }

    [Fact]
    public void ProcessSnapshot_DuplicateSnapshot_DoesNotIncreaseTpCycleCount()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 16, 8, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, 0, 0, config, TradingOpenMode.GapBuy, 10));
        Assert.Null(Process(sut, start, 0, 0, config, TradingOpenMode.GapBuy, 10));
        Assert.Null(Process(sut, start.AddMilliseconds(1), 0, 0, config, TradingOpenMode.GapBuy, 12));
        var trigger = Process(sut, start.AddMilliseconds(2), 0, 0, config, TradingOpenMode.GapBuy, 15);

        Assert.NotNull(trigger);
        Assert.Equal(new[] { 10d, 12d, 15d }, trigger!.CloseTpProfits);
    }

    [Fact]
    public void ProcessSnapshot_CycleSizeChange_ResetsTpCycle()
    {
        var sut = new CloseSignalEngine();
        var sizeThree = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            SignalCycleSize: 3);
        var sizeTwo = sizeThree with { SignalCycleSize = 2 };
        var start = new DateTime(2026, 3, 18, 16, 9, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, 0, 0, sizeThree, TradingOpenMode.GapBuy, 10));
        Assert.Null(Process(sut, start.AddMilliseconds(1), 0, 0, sizeThree, TradingOpenMode.GapBuy, 12));
        Assert.Null(Process(sut, start.AddMilliseconds(2), 0, 0, sizeTwo, TradingOpenMode.GapBuy, 15));
        var trigger = Process(sut, start.AddMilliseconds(3), 0, 0, sizeTwo, TradingOpenMode.GapBuy, 16);

        Assert.NotNull(trigger);
        Assert.Equal(new[] { 15d, 16d }, trigger!.CloseTpProfits);
    }

    [Fact]
    public void ResetGapState_PreservesTpCycle_ButResetClearsIt()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseConfirmTpProfit: 10,
            CloseTpProfit: 15,
            SignalCycleSize: 3);
        var start = new DateTime(2026, 3, 18, 16, 10, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start, 0, 0, config, TradingOpenMode.GapBuy, 10));
        sut.ResetGapState();
        Assert.Null(Process(sut, start.AddMilliseconds(1), 0, 0, config, TradingOpenMode.GapBuy, 12));
        sut.Reset();
        Assert.Null(Process(sut, start.AddMilliseconds(2), 0, 0, config, TradingOpenMode.GapBuy, 15));
        Assert.Null(Process(sut, start.AddMilliseconds(3), 0, 0, config, TradingOpenMode.GapBuy, 16));
        var trigger = Process(sut, start.AddMilliseconds(4), 0, 0, config, TradingOpenMode.GapBuy, 17);

        Assert.NotNull(trigger);
        Assert.Equal(new[] { 15d, 16d, 17d }, trigger!.CloseTpProfits);
    }

    private static GapSignalTriggerResult? Process(
        CloseSignalEngine sut,
        DateTime timestampUtc,
        int? gapBuy,
        int? gapSell,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        double? slotProfit = null)
        => sut.ProcessSnapshot(
            new GapSignalSnapshot(timestampUtc, 2945.12m, 2945.34m, 2945.56m, 2945.78m, gapBuy ?? 0, gapSell ?? 0, 1),
            config,
            openMode,
            slotProfit);
}
