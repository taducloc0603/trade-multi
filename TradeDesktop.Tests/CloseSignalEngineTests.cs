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
