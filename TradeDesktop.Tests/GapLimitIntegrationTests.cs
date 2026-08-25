using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class GapLimitIntegrationTests
{
    private static readonly GapStabilityConfig Stability =
        new(10, 0.50, 4.0, 3, 0.45, 0.60);

    private static readonly DateTime Start =
        new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Open_BothLimitsDisabled_AllowsVeryLargeStableGap()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(limitMaxGap: 0);

        Assert.Empty(ProcessOpen(engine, config, 0, 1_000_000));
        Assert.Empty(ProcessOpen(engine, config, 1, 1_100_000));
        var trigger = Assert.Single(ProcessOpen(engine, config, 2, 1_050_000));

        var guard = CheckGuard(trigger, maxGap: 0);
        Assert.True(guard.CanTrade);
    }

    [Fact]
    public void Close_BothLimitsDisabled_AllowsVeryLargeStableGap()
    {
        var engine = new CloseSignalEngine();
        var config = CloseConfig(limitMaxGap: 0);

        Assert.Null(ProcessClose(engine, config, 0, -1_000_000));
        Assert.Null(ProcessClose(engine, config, 1, -1_100_000));
        var trigger = ProcessClose(engine, config, 2, -1_050_000);

        Assert.NotNull(trigger);
        var guard = CheckGuard(trigger, maxGap: 0);
        Assert.True(guard.CanTrade);
    }

    [Fact]
    public void LimitMaxGap_RejectsTickAndDoesNotUseItAsNextCycleCenter()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(
            limitMaxGap: 500,
            confirm: 50,
            open: 100);

        Assert.Empty(ProcessOpen(engine, config, 0, 100));
        Assert.Empty(ProcessOpen(engine, config, 1, 120));
        Assert.Empty(ProcessOpen(engine, config, 2, 550));
        Assert.Empty(ProcessOpen(engine, config, 3, 125));
        Assert.Empty(ProcessOpen(engine, config, 4, 130));
        var trigger = Assert.Single(ProcessOpen(engine, config, 5, 140));

        Assert.Equal([125, 130, 140], trigger.BuyGaps);
        Assert.DoesNotContain(550, trigger.BuyGaps);
    }

    [Fact]
    public void MaxGap_BlocksStableOpenTriggerAtFinalGuard()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(limitMaxGap: 0, confirm: 100, open: 500);

        ProcessOpen(engine, config, 0, 520);
        ProcessOpen(engine, config, 1, 540);
        var trigger = Assert.Single(ProcessOpen(engine, config, 2, 550));

        var guard = CheckGuard(trigger, maxGap: 500);
        Assert.False(guard.CanTrade);
        Assert.Contains("max_gap=500", guard.SkipReason);
    }

    [Fact]
    public void MaxGap_BlocksStableNegativeCloseTriggerAtFinalGuard()
    {
        var engine = new CloseSignalEngine();
        var config = CloseConfig(limitMaxGap: 0, confirm: 100, close: 500);

        ProcessClose(engine, config, 0, -520);
        ProcessClose(engine, config, 1, -540);
        var trigger = ProcessClose(engine, config, 2, -550);

        Assert.NotNull(trigger);
        var guard = CheckGuard(trigger, maxGap: 500);
        Assert.False(guard.CanTrade);
        Assert.Contains("-max_gap=-500", guard.SkipReason);
    }

    [Fact]
    public void Limit500_AllowsCycleButMax450BlocksFinalTick480()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(limitMaxGap: 500, confirm: 100, open: 450);

        Assert.Empty(ProcessOpen(engine, config, 0, 460));
        Assert.Empty(ProcessOpen(engine, config, 1, 470));
        var trigger = Assert.Single(ProcessOpen(engine, config, 2, 480));

        Assert.Equal([460, 470, 480], trigger.BuyGaps);
        var guard = CheckGuard(trigger, maxGap: 450);
        Assert.False(guard.CanTrade);
        Assert.Contains("Gap=480", guard.SkipReason);
    }

    [Fact]
    public void MaxGap_AllowsFinalTickAtExactBoundary()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(limitMaxGap: 500, confirm: 100, open: 450);

        ProcessOpen(engine, config, 0, 460);
        ProcessOpen(engine, config, 1, 470);
        var trigger = Assert.Single(ProcessOpen(engine, config, 2, 450));

        Assert.True(CheckGuard(trigger, maxGap: 450).CanTrade);
    }

    private static GapSignalConfirmationConfig OpenConfig(
        int limitMaxGap,
        int confirm = 1,
        int open = 1_000_000) =>
        new(
            ConfirmGapPts: confirm,
            OpenPts: open,
            HoldConfirmMs: 2000,
            LimitMaxGap: limitMaxGap,
            OpenGapStability: Stability);

    private static GapSignalConfirmationConfig CloseConfig(
        int limitMaxGap,
        int confirm = 1,
        int close = 1_000_000) =>
        new(
            ConfirmGapPts: 0,
            OpenPts: 0,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: confirm,
            ClosePts: close,
            CloseHoldConfirmMs: 2000,
            LimitMaxGap: limitMaxGap,
            CloseGapStability: Stability);

    private static IReadOnlyList<GapSignalTriggerResult> ProcessOpen(
        GapSignalConfirmationEngine engine,
        GapSignalConfirmationConfig config,
        int second,
        int gapBuy) =>
        engine.ProcessSnapshot(
            Snapshot(second, gapBuy: gapBuy),
            config);

    private static GapSignalTriggerResult? ProcessClose(
        CloseSignalEngine engine,
        GapSignalConfirmationConfig config,
        int second,
        int gapSell) =>
        engine.ProcessSnapshot(
            Snapshot(second, gapSell: gapSell),
            config,
            TradingOpenMode.GapBuy);

    private static GapSignalSnapshot Snapshot(
        int second,
        int? gapBuy = null,
        int? gapSell = null) =>
        new(
            Start.AddSeconds(second),
            ExchangeABid: 1.1000m,
            ExchangeAAsk: 1.1001m,
            ExchangeBBid: 1.1101m,
            ExchangeBAsk: 1.1102m,
            gapBuy,
            gapSell,
            PointMultiplier: 100);

    private static SignalEntryGuard.GuardResult CheckGuard(
        GapSignalTriggerResult trigger,
        int maxGap) =>
        SignalEntryGuard.Check(
            trigger,
            metrics: null,
            new SignalEntryGuard.GuardConfig(
                ConfirmLatencyMs: 0,
                MaxGap: maxGap,
                MaxSpread: 0,
                PointMultiplier: 100),
            new Queue<SignalEntryGuard.PriceHistoryEntry>(),
            holdConfirmMs: 0);
}
