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

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: null, gapSell: -5, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: null, gapSell: -6, config, TradingOpenMode.GapBuy));

        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: null, gapSell: -8, config, TradingOpenMode.GapBuy);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalAction.Close, trigger!.Action);
        Assert.Equal(GapSignalTriggerType.CloseByGapSell, trigger.TriggerType);
        Assert.Equal(GapSignalSide.Buy, trigger.PrimarySide);
        Assert.Equal(new[] { 0, 0, 0 }, trigger.BuyGaps);
        Assert.Equal(new[] { -5, -6, -8 }, trigger.SellGaps);
        Assert.Equal(0, trigger.LastBuyGap);
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

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 5, gapSell: null, config, TradingOpenMode.GapSell));
        Assert.Null(Process(sut, start.AddMilliseconds(220), gapBuy: 6, gapSell: null, config, TradingOpenMode.GapSell));

        var trigger = Process(sut, start.AddMilliseconds(450), gapBuy: 8, gapSell: null, config, TradingOpenMode.GapSell);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalAction.Close, trigger!.Action);
        Assert.Equal(GapSignalTriggerType.CloseByGapBuy, trigger.TriggerType);
        Assert.Equal(GapSignalSide.Sell, trigger.PrimarySide);
        Assert.Equal(new[] { 5, 6, 8 }, trigger.BuyGaps);
        Assert.Equal(new[] { 0, 0, 0 }, trigger.SellGaps);
        Assert.Equal(8, trigger.LastBuyGap);
        Assert.Equal(0, trigger.LastSellGap);
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

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: null, gapSell: -5, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: null, gapSell: -6, config, TradingOpenMode.GapBuy));

        // Fails confirm -> reset close hold window.
        Assert.Null(Process(sut, start.AddMilliseconds(200), gapBuy: null, gapSell: -4, config, TradingOpenMode.GapBuy));

        Assert.Null(Process(sut, start.AddMilliseconds(250), gapBuy: null, gapSell: -5, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(400), gapBuy: null, gapSell: -8, config, TradingOpenMode.GapBuy));

        var trigger = Process(sut, start.AddMilliseconds(600), gapBuy: null, gapSell: -8, config, TradingOpenMode.GapBuy);

        Assert.NotNull(trigger);
        Assert.Equal(new[] { -5, -8, -8 }, trigger!.SellGaps);
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

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: null, gapSell: -5, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(120), gapBuy: null, gapSell: -6, config, TradingOpenMode.GapBuy));
        Assert.Null(Process(sut, start.AddMilliseconds(250), gapBuy: null, gapSell: -7, config, TradingOpenMode.GapBuy));

        var trigger = Process(sut, start.AddMilliseconds(520), gapBuy: null, gapSell: -8, config, TradingOpenMode.GapBuy);

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
            CloseHoldConfirmMs: 400);
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
    public void ProcessSnapshot_ResetsTpWindow_WhenProfitDropsBelowConfirm()
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
            CloseHoldConfirmMs: 300);
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
            CloseHoldConfirmMs: 300);
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
            LimitMaxTp: 0);
        var start = new DateTime(2026, 3, 18, 16, 0, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 100));
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 200));

        var trigger = Process(sut, start.AddMilliseconds(310), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 150);

        // LimitMaxTp=0 means disabled — profit lớn vẫn trigger bình thường
        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger!.CloseReason);
    }

    [Fact]
    public void ProcessSnapshot_LimitMaxTp_ResetsWindow_WhenProfitExceedsLimit()
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
            LimitMaxTp: 50);
        var start = new DateTime(2026, 3, 18, 16, 1, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 10));
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 20));
        // Spike vượt limitMaxTp=50 → reset window
        Assert.Null(Process(sut, start.AddMilliseconds(200), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 80));
        // Mở window mới sau reset
        Assert.Null(Process(sut, start.AddMilliseconds(250), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 15));

        // 250ms từ window mới (t=250), holdMs=300 → chưa đủ
        var trigger = Process(sut, start.AddMilliseconds(500), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 20);
        Assert.Null(trigger);
    }

    [Fact]
    public void ProcessSnapshot_LimitMaxTp_PreventsWindowOpen_WhenProfitExceedsLimitFromStart()
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
            LimitMaxTp: 50);
        var start = new DateTime(2026, 3, 18, 16, 2, 0, DateTimeKind.Utc);

        // Profit > limitMaxTp từ đầu → window không mở
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
            LimitMaxTp: 50);
        var start = new DateTime(2026, 3, 18, 16, 3, 0, DateTimeKind.Utc);

        // Cycle 1: spike → reset
        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 12));
        Assert.Null(Process(sut, start.AddMilliseconds(50), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 80)); // spike reset

        // Cycle 2: profit về bình thường → window mở lại
        Assert.Null(Process(sut, start.AddMilliseconds(100), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 15));
        Assert.Null(Process(sut, start.AddMilliseconds(200), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 20));

        var trigger = Process(sut, start.AddMilliseconds(310), gapBuy: 0, gapSell: 0, config, TradingOpenMode.GapBuy, slotProfit: 18);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger!.CloseReason);
    }

    // ===== Close-gap "target profit" mode (CloseGapTargetProfit + WithTarget thresholds) =====
    // Lưu ý: đặt gapBuy == gapSell để tránh quirk BuyGaps/SellGaps của ProcessSide, tập trung test
    // chuyển ngưỡng theo profit + xử lý giá trị WithTarget ÂM.

    [Fact]
    public void ProcessSnapshot_TargetMode_ClosesBuy_WithNegativeWithTargetThresholds()
    {
        var sut = new CloseSignalEngine();
        // Ngưỡng chuẩn 50/80 (chặt, không fire), WithTarget âm -20/-30 (lỏng) khi profit >= 15.
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseHoldConfirmMs: 400,
            CloseGapTargetProfit: 15,
            CloseConfirmGapPtsWithTarget: -20,
            ClosePtsWithTarget: -30);
        var start = new DateTime(2026, 3, 18, 17, 0, 0, DateTimeKind.Utc);

        // confirm ≤ +20 (=-(-20)), last ≤ +30. GapSell còn DƯƠNG vẫn đóng vì đã đạt profit target.
        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 15, gapSell: 15, config, TradingOpenMode.GapBuy, slotProfit: 20));
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: 10, gapSell: 10, config, TradingOpenMode.GapBuy, slotProfit: 20));

        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: 18, gapSell: 18, config, TradingOpenMode.GapBuy, slotProfit: 20);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalAction.Close, trigger!.Action);
        Assert.Equal(CloseSignalReason.Gap, trigger.CloseReason);
        Assert.Equal(CloseGapMode.Target, trigger.GapMode);
    }

    [Fact]
    public void ProcessSnapshot_TargetMode_DoesNotClose_WhenTickBreaksLooseConfirm()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseHoldConfirmMs: 400,
            CloseGapTargetProfit: 15,
            CloseConfirmGapPtsWithTarget: -20,
            ClosePtsWithTarget: -30);
        var start = new DateTime(2026, 3, 18, 17, 5, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 15, gapSell: 15, config, TradingOpenMode.GapBuy, slotProfit: 20));
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: 10, gapSell: 10, config, TradingOpenMode.GapBuy, slotProfit: 20));
        // Tick cuối GapSell=25 > +20 → fail confirm (lỏng) → reset, không đóng.
        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: 25, gapSell: 25, config, TradingOpenMode.GapBuy, slotProfit: 20);

        Assert.Null(trigger);
    }

    [Fact]
    public void ProcessSnapshot_TargetMode_ClosesSell_WithNegativeWithTargetThresholds()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseHoldConfirmMs: 400,
            CloseGapTargetProfit: 15,
            CloseConfirmGapPtsWithTarget: -20,
            ClosePtsWithTarget: -30);
        var start = new DateTime(2026, 3, 18, 17, 10, 0, DateTimeKind.Utc);

        // Sell close: confirm ≥ -20, last ≥ -30. GapBuy còn ÂM vẫn đóng.
        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: -15, gapSell: -15, config, TradingOpenMode.GapSell, slotProfit: 20));
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: -10, gapSell: -10, config, TradingOpenMode.GapSell, slotProfit: 20));

        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: -18, gapSell: -18, config, TradingOpenMode.GapSell, slotProfit: 20);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalTriggerType.CloseByGapBuy, trigger!.TriggerType);
        Assert.Equal(CloseSignalReason.Gap, trigger.CloseReason);
        Assert.Equal(CloseGapMode.Target, trigger.GapMode);
    }

    [Fact]
    public void ProcessSnapshot_TargetMode_KeepsTarget_WhenProfitDropsBelowTarget_Latched()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseHoldConfirmMs: 400,
            CloseGapTargetProfit: 15,
            CloseConfirmGapPtsWithTarget: -20,
            ClosePtsWithTarget: -30);
        var start = new DateTime(2026, 3, 18, 17, 15, 0, DateTimeKind.Utc);

        // Tick 1 profit=20 (>=15) → LATCH target. gapBuy=15 <= +20 → confirm bắt đầu.
        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 15, gapSell: 15, config, TradingOpenMode.GapBuy, slotProfit: 20));
        // Tick 2 profit=10 (<15) nhưng ĐÃ latch → vẫn dùng ngưỡng target lỏng, confirm không reset.
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: 10, gapSell: 10, config, TradingOpenMode.GapBuy, slotProfit: 10));
        // Tick 3 profit=10 (<15), latch giữ target → gapBuy=18 <= +20 và <= +30, hold đủ 400ms → ĐÓNG.
        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: 18, gapSell: 18, config, TradingOpenMode.GapBuy, slotProfit: 10);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalAction.Close, trigger!.Action);
        Assert.Equal(CloseSignalReason.Gap, trigger.CloseReason);
        Assert.Equal(CloseGapMode.Target, trigger.GapMode);
        Assert.Equal(CloseGapMode.Target, sut.LastResolvedGapMode);
    }

    [Fact]
    public void ProcessSnapshot_TargetMode_Reset_ClearsLatch_BackToNormalThresholds()
    {
        var sut = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseHoldConfirmMs: 400,
            CloseGapTargetProfit: 15,
            CloseConfirmGapPtsWithTarget: -20,
            ClosePtsWithTarget: -30);
        var start = new DateTime(2026, 3, 18, 17, 30, 0, DateTimeKind.Utc);

        // Latch target bằng 1 tick profit>=15.
        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 15, gapSell: 15, config, TradingOpenMode.GapBuy, slotProfit: 20));
        Assert.Equal(CloseGapMode.Target, sut.LastResolvedGapMode);

        // Reset (mô phỏng slot đóng → engine mới) xoá latch.
        sut.Reset();
        Assert.Equal(CloseGapMode.Normal, sut.LastResolvedGapMode);

        // Sau reset, profit<15 suốt → KHÔNG re-latch → ngưỡng chuẩn 50/80. gapBuy=18 <= -50? false → không đóng.
        Assert.Null(Process(sut, start.AddMilliseconds(600), gapBuy: 18, gapSell: 18, config, TradingOpenMode.GapBuy, slotProfit: 10));
        Assert.Null(Process(sut, start.AddMilliseconds(780), gapBuy: 18, gapSell: 18, config, TradingOpenMode.GapBuy, slotProfit: 10));
        var trigger = Process(sut, start.AddMilliseconds(1020), gapBuy: 18, gapSell: 18, config, TradingOpenMode.GapBuy, slotProfit: 10);

        Assert.Null(trigger);
        Assert.Equal(CloseGapMode.Normal, sut.LastResolvedGapMode);
    }

    [Fact]
    public void ProcessSnapshot_TargetMode_FreshEngine_DoesNotLatch_WhenProfitNeverReachesTarget()
    {
        var sut = new CloseSignalEngine();
        // profit=10 luôn < X=15 → không bao giờ latch → ngưỡng chuẩn 50/80, gapBuy=18 không đủ đóng.
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 50,
            ClosePts: 80,
            CloseHoldConfirmMs: 400,
            CloseGapTargetProfit: 15,
            CloseConfirmGapPtsWithTarget: -20,
            ClosePtsWithTarget: -30);
        var start = new DateTime(2026, 3, 18, 17, 35, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: 18, gapSell: 18, config, TradingOpenMode.GapBuy, slotProfit: 10));
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: 18, gapSell: 18, config, TradingOpenMode.GapBuy, slotProfit: 10));
        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: 18, gapSell: 18, config, TradingOpenMode.GapBuy, slotProfit: 10);

        Assert.Null(trigger);
        Assert.Equal(CloseGapMode.Normal, sut.LastResolvedGapMode);
    }

    [Fact]
    public void ProcessSnapshot_TargetMode_Disabled_WhenTargetProfitZero_UsesNormalThresholds()
    {
        var sut = new CloseSignalEngine();
        // X=0 → WithTarget bị bỏ qua hoàn toàn; dùng ngưỡng chuẩn 5/8. GapMode=Normal.
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 400,
            CloseGapTargetProfit: 0,
            CloseConfirmGapPtsWithTarget: -20,
            ClosePtsWithTarget: -30);
        var start = new DateTime(2026, 3, 18, 17, 20, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: -5, gapSell: -5, config, TradingOpenMode.GapBuy, slotProfit: 999));
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: -6, gapSell: -6, config, TradingOpenMode.GapBuy, slotProfit: 999));

        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: -8, gapSell: -8, config, TradingOpenMode.GapBuy, slotProfit: 999);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Gap, trigger!.CloseReason);
        Assert.Equal(CloseGapMode.Normal, trigger.GapMode);
    }

    [Fact]
    public void ProcessSnapshot_NormalMode_GapModeIsNormal_WhenProfitBelowTarget()
    {
        var sut = new CloseSignalEngine();
        // X=100, profit=15 (<X) → không kích target; đóng bằng ngưỡng chuẩn 5/8. GapMode=Normal.
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5,
            OpenPts: 8,
            HoldConfirmMs: 500,
            CloseConfirmGapPts: 5,
            ClosePts: 8,
            CloseHoldConfirmMs: 400,
            CloseGapTargetProfit: 100,
            CloseConfirmGapPtsWithTarget: -20,
            ClosePtsWithTarget: -30);
        var start = new DateTime(2026, 3, 18, 17, 25, 0, DateTimeKind.Utc);

        Assert.Null(Process(sut, start.AddMilliseconds(0), gapBuy: -5, gapSell: -5, config, TradingOpenMode.GapBuy, slotProfit: 15));
        Assert.Null(Process(sut, start.AddMilliseconds(180), gapBuy: -6, gapSell: -6, config, TradingOpenMode.GapBuy, slotProfit: 15));

        var trigger = Process(sut, start.AddMilliseconds(420), gapBuy: -8, gapSell: -8, config, TradingOpenMode.GapBuy, slotProfit: 15);

        Assert.NotNull(trigger);
        Assert.Equal(CloseGapMode.Normal, trigger!.GapMode);
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
