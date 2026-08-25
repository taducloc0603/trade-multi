using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class GapStableCloseIntegrationTests
{
    private static readonly GapStabilityConfig Stability =
        new(10, 0.60, 4.0, 3, 0.45, 0.60);

    private static readonly DateTime Start =
        new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GapBuyPosition_ClosesByStableNegativeGapSell()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 2000);

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -100));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -120));
        var trigger = Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -110);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalTriggerType.CloseByGapSell, trigger.TriggerType);
        Assert.Equal(GapSignalSide.Buy, trigger.PrimarySide);
        Assert.Equal([-100, -120, -110], trigger.SellGaps);
        Assert.Empty(trigger.BuyGaps);
        Assert.Equal(CloseGapMode.Normal, trigger.CloseGapMode);
        Assert.Equal(-50, trigger.EffectiveCloseConfirmGapPts);
        Assert.Equal(-100, trigger.EffectiveCloseGapPts);
    }

    [Fact]
    public void GapSellPosition_ClosesByStablePositiveGapBuy()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 2000);

        Assert.Null(Process(engine, config, TradingOpenMode.GapSell, 0, gapBuy: 100));
        Assert.Null(Process(engine, config, TradingOpenMode.GapSell, 1, gapBuy: 120));
        var trigger = Process(engine, config, TradingOpenMode.GapSell, 2, gapBuy: 110);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalTriggerType.CloseByGapBuy, trigger.TriggerType);
        Assert.Equal(GapSignalSide.Sell, trigger.PrimarySide);
        Assert.Equal([100, 120, 110], trigger.BuyGaps);
        Assert.Empty(trigger.SellGaps);
        Assert.Equal(50, trigger.EffectiveCloseConfirmGapPts);
        Assert.Equal(100, trigger.EffectiveCloseGapPts);
    }

    [Fact]
    public void SpikeDoesNotCloseImmediately_AndReturnRegionMustConfirmAgain()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 1, close: 20, holdMs: 3000);

        Assert.Null(Process(engine, config, TradingOpenMode.GapSell, 0, gapBuy: 2));
        Assert.Null(Process(engine, config, TradingOpenMode.GapSell, 1, gapBuy: 4));
        Assert.Null(Process(engine, config, TradingOpenMode.GapSell, 2, gapBuy: 3));
        Assert.Null(Process(engine, config, TradingOpenMode.GapSell, 3, gapBuy: 5));
        Assert.Null(Process(engine, config, TradingOpenMode.GapSell, 4, gapBuy: 20));
        Assert.Null(Process(engine, config, TradingOpenMode.GapSell, 5, gapBuy: 2));
    }

    [Fact]
    public void EachCloseEngineMaintainsIndependentSlotCycle()
    {
        var slotOne = new CloseSignalEngine();
        var slotTwo = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 2000);

        Process(slotOne, config, TradingOpenMode.GapBuy, 0, gapSell: -100);
        Process(slotOne, config, TradingOpenMode.GapBuy, 1, gapSell: -110);
        Process(slotTwo, config, TradingOpenMode.GapBuy, 1, gapSell: -100);

        var slotOneTrigger = Process(slotOne, config, TradingOpenMode.GapBuy, 2, gapSell: -120);
        var slotTwoTrigger = Process(slotTwo, config, TradingOpenMode.GapBuy, 2, gapSell: -110);

        Assert.NotNull(slotOneTrigger);
        Assert.Null(slotTwoTrigger);
    }

    [Fact]
    public void MissingRequiredPrice_ResetsNormalCloseCycle()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 2000);

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -100));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -110));
        Assert.Null(Process(
            engine,
            config,
            TradingOpenMode.GapBuy,
            2,
            gapSell: -120,
            exchangeABid: null));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 3, gapSell: -100));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 4, gapSell: -110));

        var trigger = Process(engine, config, TradingOpenMode.GapBuy, 5, gapSell: -120);

        Assert.NotNull(trigger);
        Assert.Equal([-100, -110, -120], trigger.SellGaps);
    }

    [Fact]
    public void ResetGapState_ClearsNormalCloseCycle()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 2000);
        Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -100);
        Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -110);

        engine.ResetGapState();

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -120));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 3, gapSell: -110));
        Assert.NotNull(Process(engine, config, TradingOpenMode.GapBuy, 4, gapSell: -100));
    }

    [Fact]
    public void SosClose_BypassesStablePolicyAndKeepsLegacyFastPath()
    {
        var engine = new CloseSignalEngine();
        var strictStability = Stability with { MinStableSamples = 10 };
        var config = Config(confirm: 5, close: 3, holdMs: 1000) with
        {
            CloseGapMode = CloseGapMode.Sos,
            CloseGapStability = strictStability
        };

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 5));
        var trigger = Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 3);

        Assert.NotNull(trigger);
        Assert.Equal(CloseGapMode.Sos, trigger.CloseGapMode);
        Assert.Equal(CloseSignalReason.Gap, trigger.CloseReason);
        Assert.Equal([5, 3], trigger.SellGaps);
    }

    [Fact]
    public void NormalToSos_ResetDoesNotReuseNormalStableSamples()
    {
        var engine = new CloseSignalEngine();
        var normal = Config(confirm: 50, close: 100, holdMs: 2000);
        var sos = Config(confirm: 5, close: 3, holdMs: 1000) with
        {
            CloseGapMode = CloseGapMode.Sos
        };

        Assert.Null(Process(engine, normal, TradingOpenMode.GapBuy, 0, gapSell: -100));
        Assert.Null(Process(engine, normal, TradingOpenMode.GapBuy, 1, gapSell: -110));

        // PortfolioCoordinator gọi đúng reset này khi UpdateSosMode đổi trạng thái.
        engine.ResetGapState();

        Assert.Null(Process(engine, sos, TradingOpenMode.GapBuy, 2, gapSell: 3));
        var trigger = Process(engine, sos, TradingOpenMode.GapBuy, 3, gapSell: 3);

        Assert.NotNull(trigger);
        Assert.Equal(CloseGapMode.Sos, trigger.CloseGapMode);
        Assert.Equal([3, 3], trigger.SellGaps);
        Assert.DoesNotContain(-100, trigger.SellGaps);
        Assert.DoesNotContain(-110, trigger.SellGaps);
    }

    [Fact]
    public void SosToNormal_ResetRequiresFreshNormalStableCycle()
    {
        var engine = new CloseSignalEngine();
        var sos = Config(confirm: 5, close: 3, holdMs: 1000) with
        {
            CloseGapMode = CloseGapMode.Sos
        };
        var normal = Config(confirm: 50, close: 100, holdMs: 2000);

        Assert.Null(Process(engine, sos, TradingOpenMode.GapBuy, 0, gapSell: 3));
        engine.ResetGapState();

        Assert.Null(Process(engine, normal, TradingOpenMode.GapBuy, 1, gapSell: -100));
        Assert.Null(Process(engine, normal, TradingOpenMode.GapBuy, 2, gapSell: -110));
        var trigger = Process(engine, normal, TradingOpenMode.GapBuy, 3, gapSell: -120);

        Assert.NotNull(trigger);
        Assert.Equal(CloseGapMode.Normal, trigger.CloseGapMode);
        Assert.Equal([-100, -110, -120], trigger.SellGaps);
        Assert.DoesNotContain(3, trigger.SellGaps);
    }

    [Fact]
    public void ModeTransitionGapReset_DoesNotResetTpWindow()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 1000) with
        {
            CloseConfirmTpProfit = 5,
            CloseTpProfit = 10
        };

        Assert.Null(Process(
            engine,
            config,
            TradingOpenMode.GapBuy,
            0,
            gapSell: null,
            slotProfit: 5));

        // Normal ↔ SOS chỉ reset Gap state; TP Hold vẫn tiếp tục.
        engine.ResetGapState();

        var trigger = Process(
            engine,
            config with { CloseGapMode = CloseGapMode.Sos },
            TradingOpenMode.GapBuy,
            1,
            gapSell: null,
            slotProfit: 10);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger.CloseReason);
        Assert.Equal(10d, trigger.CloseTpProfit);
    }

    [Fact]
    public void TpClose_IsUnaffectedByStableGapPolicy()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 1000) with
        {
            CloseConfirmTpProfit = 5,
            CloseTpProfit = 10,
            CloseGapStability = Stability with { MinStableSamples = 10 }
        };

        Assert.Null(Process(
            engine,
            config,
            TradingOpenMode.GapBuy,
            0,
            gapSell: null,
            slotProfit: 5));
        var trigger = Process(
            engine,
            config,
            TradingOpenMode.GapBuy,
            1,
            gapSell: null,
            slotProfit: 10);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger.CloseReason);
        Assert.Equal(10d, trigger.CloseTpProfit);
    }

    [Fact]
    public void CloseMaxTimesTick_StillRejectsAndResetsStableCycle()
    {
        var engine = new CloseSignalEngine();
        var config = Config(
            confirm: 50,
            close: 100,
            holdMs: 2000,
            closeMaxTimesTick: 2);

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -100));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -110));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -120));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 3, gapSell: -130));
    }

    private static GapSignalConfirmationConfig Config(
        int confirm,
        int close,
        int holdMs,
        int closeMaxTimesTick = 0) =>
        new(
            ConfirmGapPts: 0,
            OpenPts: 0,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: confirm,
            ClosePts: close,
            CloseHoldConfirmMs: holdMs,
            CloseMaxTimesTick: closeMaxTimesTick,
            CloseGapStability: Stability);

    private static GapSignalTriggerResult? Process(
        CloseSignalEngine engine,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        int second,
        int? gapBuy = null,
        int? gapSell = null,
        decimal? exchangeABid = 1.1000m,
        double? slotProfit = null) =>
        engine.ProcessSnapshot(
            new GapSignalSnapshot(
                Start.AddSeconds(second),
                ExchangeABid: exchangeABid,
                ExchangeAAsk: 1.1001m,
                ExchangeBBid: 1.1101m,
                ExchangeBAsk: 1.1102m,
                gapBuy,
                gapSell,
                PointMultiplier: 100),
            config,
            openMode,
            slotProfit);
}
