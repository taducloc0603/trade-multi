using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class GapStableOpenIntegrationTests
{
    private static readonly GapStabilityConfig Stability =
        new(10, 0.50, 4.0, 3, 0.35, 0.40);

    private static readonly DateTime Start =
        new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LargeStableBuyCycle_TriggersAfterSamplesAndHold()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 2000);

        Assert.Empty(Process(engine, config, 0, gapBuy: 100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 120));
        var results = Process(engine, config, 2, gapBuy: 110);

        var trigger = Assert.Single(results);
        Assert.Equal(GapSignalTriggerType.OpenByGapBuy, trigger.TriggerType);
        Assert.Equal([100, 120, 110], trigger.BuyGaps);
        Assert.Empty(trigger.SellGaps);
        Assert.Equal(110, trigger.LastBuyGap);
        Assert.Null(trigger.LastSellGap);
    }

    [Fact]
    public void LargeStableSellCycle_PreservesNegativeDirection()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 2000);

        Assert.Empty(Process(engine, config, 0, gapSell: -100));
        Assert.Empty(Process(engine, config, 1, gapSell: -120));
        var results = Process(engine, config, 2, gapSell: -110);

        var trigger = Assert.Single(results);
        Assert.Equal(GapSignalTriggerType.OpenByGapSell, trigger.TriggerType);
        Assert.Equal([-100, -120, -110], trigger.SellGaps);
        Assert.Empty(trigger.BuyGaps);
        Assert.Equal(-110, trigger.LastSellGap);
        Assert.Null(trigger.LastBuyGap);
    }

    [Fact]
    public void SpikeStartsNewCycle_AndDoesNotTriggerImmediately()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 1, open: 20, holdMs: 3000);

        Assert.Empty(Process(engine, config, 0, gapBuy: 2));
        Assert.Empty(Process(engine, config, 1, gapBuy: 4));
        Assert.Empty(Process(engine, config, 2, gapBuy: 3));
        Assert.Empty(Process(engine, config, 3, gapBuy: 5));

        var spike = Process(engine, config, 4, gapBuy: 20);

        Assert.Empty(spike);
        Assert.Empty(Process(engine, config, 5, gapBuy: 2));
    }

    [Fact]
    public void RealTransitionToLargeRegion_TriggersOnlyAfterNewCycleConfirms()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 1, open: 100, holdMs: 2000);

        Assert.Empty(Process(engine, config, 0, gapBuy: 2));
        Assert.Empty(Process(engine, config, 1, gapBuy: 4));
        Assert.Empty(Process(engine, config, 2, gapBuy: 3));
        Assert.Empty(Process(engine, config, 3, gapBuy: 5));
        Assert.Empty(Process(engine, config, 4, gapBuy: 100));
        Assert.Empty(Process(engine, config, 5, gapBuy: 110));

        var results = Process(engine, config, 6, gapBuy: 120);

        var trigger = Assert.Single(results);
        Assert.Equal([100, 110, 120], trigger.BuyGaps);
    }

    [Fact]
    public void MissingRequiredPrice_ResetsCycleAndNeverAddsFakeZero()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 2000);

        Assert.Empty(Process(engine, config, 0, gapBuy: 100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 110));
        Assert.Empty(Process(
            engine,
            config,
            2,
            gapBuy: 120,
            exchangeAAsk: null));
        Assert.Empty(Process(engine, config, 3, gapBuy: 100));
        Assert.Empty(Process(engine, config, 4, gapBuy: 110));

        var results = Process(engine, config, 5, gapBuy: 120);

        var trigger = Assert.Single(results);
        Assert.Equal([100, 110, 120], trigger.BuyGaps);
        Assert.DoesNotContain(0, trigger.BuyGaps);
    }

    [Fact]
    public void BuyAndSellCycles_AreIndependent()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 2000);

        Assert.Empty(Process(engine, config, 0, gapBuy: 100, gapSell: -100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 110, gapSell: -110));
        var results = Process(engine, config, 2, gapBuy: 120, gapSell: -120);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.TriggerType == GapSignalTriggerType.OpenByGapBuy);
        Assert.Contains(results, result => result.TriggerType == GapSignalTriggerType.OpenByGapSell);
    }

    [Fact]
    public void TriggerResetsCycle_AndOldSamplesAreNotReused()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 2000);

        Process(engine, config, 0, gapBuy: 100);
        Process(engine, config, 1, gapBuy: 110);
        Assert.Single(Process(engine, config, 2, gapBuy: 120));

        Assert.Empty(Process(engine, config, 3, gapBuy: 130));
        Assert.Empty(Process(engine, config, 4, gapBuy: 140));
        var second = Process(engine, config, 5, gapBuy: 150);

        var trigger = Assert.Single(second);
        Assert.Equal([130, 140, 150], trigger.BuyGaps);
    }

    [Fact]
    public void HoldConfirmNotReached_DoesNotTriggerEvenWithEnoughSamples()
    {
        // Nhánh TIME: đủ MinStableSamples nhưng chưa đủ open_hold_confirm_ms -> không trigger.
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 999_999);

        for (var index = 0; index < 10; index++)
        {
            Assert.Empty(Process(engine, config, index, gapBuy: 100 + index));
        }
    }

    [Fact]
    public void SignalCycleSize_DoesNotAffectTimeBasedCycle()
    {
        // signal_cycle_size vẫn được mang theo để ghi log, nhưng không còn quyết định gì.
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 2000) with
        {
            SignalCycleSize = 10
        };

        Assert.Empty(Process(engine, config, 0, gapBuy: 100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 120));
        var trigger = Assert.Single(Process(engine, config, 2, gapBuy: 110));

        Assert.Equal([100, 120, 110], trigger.BuyGaps);
    }

    [Fact]
    public void ConfirmFailure_DiscardsCycleAndDoesNotCountFailingGap()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 0);

        Assert.Empty(Process(engine, config, 0, gapBuy: 100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 110));
        Assert.Empty(Process(engine, config, 2, gapBuy: 49));
        Assert.Empty(Process(engine, config, 3, gapBuy: 120));
        Assert.Empty(Process(engine, config, 4, gapBuy: 130));
        var trigger = Assert.Single(Process(engine, config, 5, gapBuy: 140));

        Assert.Equal([120, 130, 140], trigger.BuyGaps);
    }

    [Fact]
    public void StabilityMinSamples_ControlsTimeBasedCycle()
    {
        // Nhánh TIME: MinStableSamples LÀ điều kiện số mẫu, hold-time là điều kiện thời gian.
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 0) with
        {
            OpenGapStability = Stability with { MinStableSamples = 5 }
        };

        Assert.Empty(Process(engine, config, 0, gapBuy: 100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 110));
        Assert.Empty(Process(engine, config, 2, gapBuy: 120));
        Assert.Empty(Process(engine, config, 3, gapBuy: 110));
        Assert.Single(Process(engine, config, 4, gapBuy: 120));
    }

    [Fact]
    public void FinalGapBelowOpenTarget_KeepsCollectingInsteadOfResetting()
    {
        // Khác biệt cốt lõi so với FIXED_SIZE: Cycle ổn định nhưng mẫu cuối chưa đạt open_pts
        // thì KHÔNG reset — mẫu kế tiếp vẫn nhập vào cùng Cycle và có thể trigger ngay.
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 0);

        Assert.Empty(Process(engine, config, 0, gapBuy: 60));
        Assert.Empty(Process(engine, config, 1, gapBuy: 70));
        Assert.Empty(Process(engine, config, 2, gapBuy: 80));
        var trigger = Assert.Single(Process(engine, config, 3, gapBuy: 100));

        Assert.Equal([60, 70, 80, 100], trigger.BuyGaps);
        Assert.Equal(100, trigger.LastBuyGap);
    }

    [Fact]
    public void LimitMaxGapDisabled_AllowsVeryLargeStableCycle()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 1, open: 1_000_000, holdMs: 2000, limitMaxGap: 0);

        Assert.Empty(Process(engine, config, 0, gapBuy: 1_000_000));
        Assert.Empty(Process(engine, config, 1, gapBuy: 1_100_000));
        var results = Process(engine, config, 2, gapBuy: 1_050_000);

        Assert.Single(results);
    }

    [Fact]
    public void OpenMaxTimesTick_BlocksCycleLongerThanLimit()
    {
        // Cycle đã ổn định và mẫu cuối đạt open_pts, nhưng đã thu 3 mẫu > giới hạn 2 -> reset, không trigger.
        var engine = new GapSignalConfirmationEngine();
        var config = Config(
            confirm: 50,
            open: 100,
            holdMs: 2000,
            openMaxTimesTick: 2);

        Assert.Empty(Process(engine, config, 0, gapBuy: 100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 110));
        Assert.Empty(Process(engine, config, 2, gapBuy: 120));

        var afterReset = Assert.Single(engine.GetCycleStatuses(), status =>
            status.Kind == SignalCycleKind.OpenBuy);
        Assert.Equal(0, afterReset.CurrentCount);
    }

    [Fact]
    public void OpenMaxTimesTickZero_DoesNotLimitCycleLength()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(
            confirm: 50,
            open: 100,
            holdMs: 2000,
            openMaxTimesTick: 0);

        Assert.Empty(Process(engine, config, 0, gapBuy: 100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 110));
        Assert.Single(Process(engine, config, 2, gapBuy: 120));
    }

    [Fact]
    public void CycleStatusSnapshot_IsReadOnlyAndReflectsProgressAndReset()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 0);

        Assert.Empty(Process(engine, config, 0, gapBuy: 100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 110));

        var collecting = Assert.Single(engine.GetCycleStatuses(), status =>
            status.Kind == SignalCycleKind.OpenBuy);
        Assert.Equal(2, collecting.CurrentCount);
        // Nhánh TIME: mẫu số của panel là MinStableSamples, không phải signal_cycle_size.
        Assert.Equal(Stability.MinStableSamples, collecting.RequiredCount);
        Assert.Equal("Collecting", collecting.Status);
        Assert.Equal(110d, collecting.LastValue);

        var secondRead = Assert.Single(engine.GetCycleStatuses(), status =>
            status.Kind == SignalCycleKind.OpenBuy);
        Assert.Equal(collecting, secondRead);

        Assert.Empty(Process(engine, config, 2, gapBuy: 49));
        var reset = Assert.Single(engine.GetCycleStatuses(), status =>
            status.Kind == SignalCycleKind.OpenBuy);
        Assert.Equal(0, reset.CurrentCount);
        Assert.Equal(Stability.MinStableSamples, reset.RequiredCount);
        Assert.Equal("Empty", reset.Status);
        Assert.Contains("Confirm", reset.LastReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CycleLifecycle_PreservesTriggeredEvent()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = Config(confirm: 50, open: 100, holdMs: 0);

        Assert.Empty(Process(engine, config, 0, gapBuy: 100));
        Assert.Empty(Process(engine, config, 1, gapBuy: 110));
        var trigger = Assert.Single(Process(engine, config, 2, gapBuy: 120));
        var completed = Assert.Single(engine.GetCycleStatuses(), status =>
            status.Kind == SignalCycleKind.OpenBuy);

        Assert.Equal("Triggered", completed.LastEvent);
        Assert.Equal(trigger.DiagnosticCycleId, completed.LastEventCycleId);
        Assert.Equal(3, completed.LastEventCount);
        Assert.Equal(Start.AddSeconds(2), completed.LastEventAtUtc);
    }

    private static GapSignalConfirmationConfig Config(
        int confirm,
        int open,
        int holdMs,
        int limitMaxGap = 0,
        int openMaxTimesTick = 0) =>
        new(
            ConfirmGapPts: confirm,
            OpenPts: open,
            HoldConfirmMs: holdMs,
            LimitMaxGap: limitMaxGap,
            OpenMaxTimesTick: openMaxTimesTick,
            OpenGapStability: Stability,
            SignalCycleSize: 3);

    private static IReadOnlyList<GapSignalTriggerResult> Process(
        GapSignalConfirmationEngine engine,
        GapSignalConfirmationConfig config,
        int second,
        int? gapBuy = null,
        int? gapSell = null,
        decimal? exchangeAAsk = 1.1001m) =>
        engine.ProcessSnapshot(
            new GapSignalSnapshot(
                Start.AddSeconds(second),
                ExchangeABid: 1.1000m,
                ExchangeAAsk: exchangeAAsk,
                ExchangeBBid: 1.1101m,
                ExchangeBAsk: 1.1102m,
                gapBuy,
                gapSell,
                PointMultiplier: 100),
            config);
}
