using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

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
        Assert.Equal(2000, trigger.EffectiveCloseHoldMs);
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
    public void SosClose_UsesHoldTimeAndMinStableSamples()
    {
        // Nhánh TIME: SOS Close dùng CHUNG close_hold_confirm_ms với Normal Close.
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 5, close: 3, holdMs: 2000) with
        {
            CloseGapMode = CloseGapMode.Sos
        };

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 5));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 4));
        var trigger = Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: 3);

        Assert.NotNull(trigger);
        Assert.Equal(CloseGapMode.Sos, trigger!.CloseGapMode);
        Assert.Equal(CloseSignalReason.Gap, trigger.CloseReason);
        Assert.Equal([5, 4, 3], trigger.SellGaps);
        Assert.Equal(2000, trigger.EffectiveCloseHoldMs);
    }

    [Fact]
    public void SosClose_DoesNotTriggerBeforeHoldConfirm()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 5, close: 3, holdMs: 999_999) with
        {
            CloseGapMode = CloseGapMode.Sos
        };

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 5));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 4));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: 3));
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
        Assert.Null(Process(engine, sos, TradingOpenMode.GapBuy, 3, gapSell: 3));
        var trigger = Process(engine, sos, TradingOpenMode.GapBuy, 4, gapSell: 3);

        Assert.NotNull(trigger);
        Assert.Equal(CloseGapMode.Sos, trigger!.CloseGapMode);
        Assert.Equal([3, 3, 3], trigger.SellGaps);
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
    public void ModeTransitionGapReset_DoesNotResetTpCycle()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 1000) with
        {
            CloseConfirmTpProfit = 5,
            CloseTpProfit = 10,
            SignalCycleSize = 2
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
            SignalCycleSize = 1,
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
    public void TpCycles_AreIndependentAcrossCloseEngines()
    {
        var first = new CloseSignalEngine();
        var second = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 1000) with
        {
            CloseConfirmTpProfit = 5,
            CloseTpProfit = 10
        };

        Assert.Null(Process(first, config, TradingOpenMode.GapBuy, 0, slotProfit: 5));
        Assert.Null(Process(second, config, TradingOpenMode.GapBuy, 0, slotProfit: 5));
        var firstTrigger = Process(first, config, TradingOpenMode.GapBuy, 1, slotProfit: 10);
        var secondTrigger = Process(second, config, TradingOpenMode.GapBuy, 1, slotProfit: 10);

        Assert.NotNull(firstTrigger);
        Assert.NotNull(secondTrigger);
        Assert.NotEqual(firstTrigger!.DiagnosticCycleId, secondTrigger!.DiagnosticCycleId);
        Assert.NotEqual(firstTrigger.DiagnosticSignalId, secondTrigger.DiagnosticSignalId);
    }

    [Fact]
    public void HoldConfirmNotReached_DoesNotCloseEvenWithEnoughSamples()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 999_999);

        for (var index = 0; index < 10; index++)
        {
            Assert.Null(Process(
                engine,
                config,
                TradingOpenMode.GapBuy,
                index,
                gapSell: -100 - index));
        }
    }

    [Fact]
    public void SignalCycleSize_DoesNotAffectTimeBasedCloseCycle()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 2000) with
        {
            SignalCycleSize = 10
        };

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -100));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -120));
        var trigger = Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -110);

        Assert.NotNull(trigger);
        Assert.Equal([-100, -120, -110], trigger!.SellGaps);
    }

    [Fact]
    public void ConfirmFailure_DiscardsNormalCloseCycleAndFailingGap()
    {
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 0);

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -100));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -110));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -49));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 3, gapSell: -120));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 4, gapSell: -130));
        var trigger = Process(engine, config, TradingOpenMode.GapBuy, 5, gapSell: -140);

        Assert.NotNull(trigger);
        Assert.Equal([-120, -130, -140], trigger!.SellGaps);
    }

    [Fact]
    public void StabilityMinSamples_ControlsNormalCloseCycle()
    {
        // Nhánh TIME: MinStableSamples LÀ điều kiện số mẫu của Normal Close.
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 0) with
        {
            CloseGapStability = Stability with { MinStableSamples = 5 }
        };

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -100));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -110));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -120));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 3, gapSell: -110));
        Assert.NotNull(Process(engine, config, TradingOpenMode.GapBuy, 4, gapSell: -120));
    }

    [Fact]
    public void FinalGapBelowCloseTarget_KeepsCollectingInsteadOfResetting()
    {
        // Cycle ổn định nhưng mẫu cuối chưa đạt close_pts thì KHÔNG reset;
        // mẫu kế tiếp vẫn nhập vào cùng Cycle và có thể trigger ngay.
        var engine = new CloseSignalEngine();
        var config = Config(confirm: 50, close: 100, holdMs: 0);

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -60));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -70));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -80));
        var trigger = Process(engine, config, TradingOpenMode.GapBuy, 3, gapSell: -100);

        Assert.NotNull(trigger);
        Assert.Equal([-60, -70, -80, -100], trigger!.SellGaps);
    }

    [Fact]
    public void CloseMaxTimesTick_BlocksNormalCloseCycleLongerThanLimit()
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
    }

    [Fact]
    public void CloseMaxTimesTickZero_DoesNotLimitNormalCloseCycle()
    {
        var engine = new CloseSignalEngine();
        var config = Config(
            confirm: 50,
            close: 100,
            holdMs: 2000,
            closeMaxTimesTick: 0);

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -100));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -110));
        Assert.NotNull(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -120));
    }

    // ---- SOS Close: state riêng, nhưng dùng CHUNG close_hold_confirm_ms và close_max_times_tick ----

    [Fact]
    public void SosHoldConfirmNotReached_NeverTriggers()
    {
        var engine = new CloseSignalEngine();
        var config = SosConfig(confirm: 5, close: 3, cycleSize: 10, holdMs: 999_999);

        for (var index = 0; index < 9; index++)
        {
            // Mọi mẫu đều đạt sos_close_confirm_gap_pts (<= 5) nhưng chưa đủ hold-time.
            Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, index, gapSell: 5));
        }

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 9, gapSell: 3));
    }

    [Fact]
    public void SosConfirmFailure_ResetsSosCycleAndDiscardsFailingGap()
    {
        var engine = new CloseSignalEngine();
        var config = SosConfig(confirm: 5, close: 3, cycleSize: 3, holdMs: 0);

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 5));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 4));
        // Gap 6 > sos_close_confirm_gap_pts=5 → reset chu kỳ, bản thân mẫu này bị loại.
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: 6));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 3, gapSell: 5));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 4, gapSell: 4));
        var trigger = Process(engine, config, TradingOpenMode.GapBuy, 5, gapSell: 3);

        Assert.NotNull(trigger);
        Assert.Equal([5, 4, 3], trigger!.SellGaps);
    }

    [Fact]
    public void SosFinalGapAboveCloseTarget_KeepsCollectingInsteadOfResetting()
    {
        var engine = new CloseSignalEngine();
        var config = SosConfig(confirm: 5, close: 3, cycleSize: 3, holdMs: 0);

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 5));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 5));
        // Đủ mẫu + đủ hold nhưng gap cuối = 4 > sos_close_gap_pts=3 → chưa trigger, KHÔNG reset.
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: 4));

        var trigger = Process(engine, config, TradingOpenMode.GapBuy, 3, gapSell: 3);

        Assert.NotNull(trigger);
        Assert.Equal([5, 5, 4, 3], trigger!.SellGaps);
    }

    [Fact]
    public void CloseHoldConfirmMs_AppliesToSosCycle()
    {
        // Nhánh TIME: SOS dùng chung close_hold_confirm_ms với Normal Close.
        var engine = new CloseSignalEngine();
        var hugeHold = SosConfig(confirm: 5, close: 3, cycleSize: 3, holdMs: 999_999);
        var noHold = SosConfig(confirm: 5, close: 3, cycleSize: 3, holdMs: 0);

        Assert.Null(Process(engine, hugeHold, TradingOpenMode.GapBuy, 0, gapSell: 5));
        Assert.Null(Process(engine, hugeHold, TradingOpenMode.GapBuy, 1, gapSell: 4));
        Assert.Null(Process(engine, hugeHold, TradingOpenMode.GapBuy, 2, gapSell: 3));

        var other = new CloseSignalEngine();
        Assert.Null(Process(other, noHold, TradingOpenMode.GapBuy, 0, gapSell: 5));
        Assert.Null(Process(other, noHold, TradingOpenMode.GapBuy, 1, gapSell: 4));
        var withoutHold = Process(other, noHold, TradingOpenMode.GapBuy, 2, gapSell: 3);

        Assert.NotNull(withoutHold);
        Assert.Equal([5, 4, 3], withoutHold!.SellGaps);
        Assert.Equal(0, withoutHold.EffectiveCloseHoldMs);
    }

    [Fact]
    public void CloseMaxTimesTick_BlocksSosCycleLongerThanLimit()
    {
        var engine = new CloseSignalEngine();
        var config = SosConfig(
            confirm: 5,
            close: 3,
            cycleSize: 3,
            holdMs: 2000,
            closeMaxTimesTick: 2);

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 5));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 4));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 2, gapSell: 3));
    }

    [Fact]
    public void SosNormalCloseAndTp_DoNotShareCycleState()
    {
        var engine = new CloseSignalEngine();
        _ = new PositionSlot(11, "PAIR-11", engine);
        // holdMs > 0 để cả Normal Close lẫn TP còn đang thu mẫu tại thời điểm chụp status.
        var normal = Config(confirm: 50, close: 100, holdMs: 5000) with
        {
            CloseConfirmTpProfit = 5,
            CloseTpProfit = 500
        };

        // Chạy Normal Close + TP để hai chu kỳ này có tiến độ.
        Assert.Null(Process(engine, normal, TradingOpenMode.GapBuy, 0, gapSell: -100, slotProfit: 5));
        Assert.Null(Process(engine, normal, TradingOpenMode.GapBuy, 1, gapSell: -110, slotProfit: 6));

        var statuses = engine.GetCycleStatuses();
        var normalClose = Assert.Single(statuses, s => s.Kind == SignalCycleKind.NormalCloseBuy);
        var sosClose = Assert.Single(statuses, s => s.Kind == SignalCycleKind.SosCloseBuy);
        var tp = Assert.Single(statuses, s => s.Kind == SignalCycleKind.Tp);

        Assert.Equal(2, normalClose.CurrentCount);
        Assert.Equal(2, tp.CurrentCount);
        // SOS có state riêng nên không bị Normal Close/TP làm bẩn.
        Assert.Equal(0, sosClose.CurrentCount);
        Assert.Equal(11, sosClose.SlotId);
    }

    [Fact]
    public void SosProgress_DoesNotLeakIntoNormalCloseCycle()
    {
        var engine = new CloseSignalEngine();
        _ = new PositionSlot(12, "PAIR-12", engine);
        var sos = SosConfig(confirm: 5, close: 3, cycleSize: 10, holdMs: 0);

        Assert.Null(Process(engine, sos, TradingOpenMode.GapBuy, 0, gapSell: 5));
        Assert.Null(Process(engine, sos, TradingOpenMode.GapBuy, 1, gapSell: 4));

        var statuses = engine.GetCycleStatuses();
        var sosClose = Assert.Single(statuses, s => s.Kind == SignalCycleKind.SosCloseBuy);
        var normalClose = Assert.Single(statuses, s => s.Kind == SignalCycleKind.NormalCloseBuy);

        Assert.Equal(2, sosClose.CurrentCount);
        // Nhánh TIME: mẫu số là MinStableSamples, không phải signal_cycle_size.
        Assert.Equal(Stability.MinStableSamples, sosClose.RequiredCount);
        Assert.Equal(0, normalClose.CurrentCount);
    }

    [Fact]
    public void CycleStatusSnapshot_ExposesIndependentNormalCloseAndTpProgress()
    {
        var engine = new CloseSignalEngine();
        _ = new PositionSlot(7, "PAIR-7", engine);
        var config = Config(confirm: 50, close: 100, holdMs: 5000) with
        {
            CloseConfirmTpProfit = 5,
            CloseTpProfit = 50
        };

        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -60, slotProfit: 5));
        Assert.Null(Process(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -70, slotProfit: 6));

        var statuses = engine.GetCycleStatuses();
        var close = Assert.Single(statuses, status => status.Kind == SignalCycleKind.NormalCloseBuy);
        var tp = Assert.Single(statuses, status => status.Kind == SignalCycleKind.Tp);
        Assert.Equal(7, close.SlotId);
        Assert.Equal(2, close.CurrentCount);
        Assert.Equal(Stability.MinStableSamples, close.RequiredCount);
        Assert.Equal(-70d, close.LastValue);
        Assert.Equal(2, tp.CurrentCount);
        // TP chốt theo thời gian nên không có số mẫu đích.
        Assert.Equal(0, tp.RequiredCount);
        Assert.Equal(6d, tp.LastValue);
    }

    [Fact]
    public void TpCycleStatus_PreservesTriggeredEventAfterTradingStateReset()
    {
        var engine = new CloseSignalEngine();
        _ = new PositionSlot(9, "PAIR-9", engine);
        var config = Config(confirm: 50, close: 100, holdMs: 0) with
        {
            CloseConfirmTpProfit = 5,
            CloseTpProfit = 10
        };

        var trigger = Process(
            engine,
            config,
            TradingOpenMode.GapBuy,
            0,
            gapSell: null,
            slotProfit: 10);
        Assert.NotNull(trigger);

        var status = Assert.Single(engine.GetCycleStatuses(), item =>
            item.Kind == SignalCycleKind.Tp);
        Assert.Equal(0, status.CurrentCount);
        Assert.Equal("Triggered", status.LastEvent);
        Assert.Equal(trigger!.DiagnosticCycleId, status.LastEventCycleId);
        Assert.Equal(1, status.LastEventCount);
        Assert.Equal(10d, status.LastEventValue);
        Assert.Equal(9, status.SlotId);
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
            CloseGapStability: Stability,
            SignalCycleSize: 3);

    // SOS đảo chiều ngưỡng: với slot GapBuy, mọi gapSell phải <= sos_close_confirm_gap_pts
    // và gap cuối phải <= sos_close_gap_pts.
    private static GapSignalConfirmationConfig SosConfig(
        int confirm,
        int close,
        int cycleSize,
        int holdMs,
        int closeMaxTimesTick = 0) =>
        Config(confirm, close, holdMs, closeMaxTimesTick) with
        {
            CloseGapMode = CloseGapMode.Sos,
            SignalCycleSize = cycleSize
        };

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
