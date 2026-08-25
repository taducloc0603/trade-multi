using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class GapDiagnosticsBehaviorBaselineTests
{
    private static readonly DateTime Start =
        new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);

    private static readonly GapStabilityConfig Stability =
        new(10, 0.50, 4.0, 3, 0.45, 0.60);

    [Fact]
    public void Open_WithAndWithoutLogger_ProducesSameTrigger()
    {
        var withoutLogger = new GapSignalConfirmationEngine();
        var withLogger = new GapSignalConfirmationEngine(new CaptureLogger());
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 50,
            OpenPts: 100,
            HoldConfirmMs: 2000,
            OpenGapStability: Stability);

        IReadOnlyList<GapSignalTriggerResult> expected = [];
        IReadOnlyList<GapSignalTriggerResult> actual = [];
        foreach (var (second, buy, sell) in new[]
                 {
                     (0, 100, -100),
                     (1, 110, -110),
                     (2, 120, -120)
                 })
        {
            var snapshot = Snapshot(second, buy, sell);
            expected = withoutLogger.ProcessSnapshot(snapshot, config);
            actual = withLogger.ProcessSnapshot(snapshot, config);
        }

        AssertTriggerListsEqual(expected, actual);
        Assert.Equal(2, actual.Count);
    }

    [Fact]
    public void NormalClose_WithAndWithoutLogger_ProducesSameTrigger()
    {
        var withoutLogger = new CloseSignalEngine();
        var withLogger = new CloseSignalEngine(new CaptureLogger());
        var config = CloseConfig(CloseGapMode.Normal);

        GapSignalTriggerResult? expected = null;
        GapSignalTriggerResult? actual = null;
        foreach (var (second, gap) in new[] { (0, -100), (1, -110), (2, -120) })
        {
            var snapshot = Snapshot(second, gapSell: gap);
            expected = withoutLogger.ProcessSnapshot(snapshot, config, TradingOpenMode.GapBuy);
            actual = withLogger.ProcessSnapshot(snapshot, config, TradingOpenMode.GapBuy);
        }

        AssertTriggerEqual(expected, actual);
        Assert.NotNull(actual);
    }

    [Fact]
    public void Tp_WithAndWithoutLogger_ProducesSameTrigger()
    {
        var withoutLogger = new CloseSignalEngine();
        var withLogger = new CloseSignalEngine(new CaptureLogger());
        var config = CloseConfig(CloseGapMode.Normal) with
        {
            CloseConfirmTpProfit = 5,
            CloseTpProfit = 10,
            CloseHoldConfirmMs = 1000
        };

        GapSignalTriggerResult? expected = null;
        GapSignalTriggerResult? actual = null;
        foreach (var (second, profit) in new[] { (0, 6d), (1, 11d) })
        {
            var snapshot = Snapshot(second);
            expected = withoutLogger.ProcessSnapshot(snapshot, config, TradingOpenMode.GapBuy, profit);
            actual = withLogger.ProcessSnapshot(snapshot, config, TradingOpenMode.GapBuy, profit);
        }

        AssertTriggerEqual(expected, actual);
        Assert.NotNull(actual);
        Assert.Equal(CloseSignalReason.Tp, actual!.CloseReason);
    }

    [Fact]
    public void Sos_WithAndWithoutLogger_ProducesSameTrigger()
    {
        var withoutLogger = new CloseSignalEngine();
        var withLogger = new CloseSignalEngine(new CaptureLogger());
        var config = CloseConfig(CloseGapMode.Sos) with
        {
            CloseConfirmGapPts = 50,
            ClosePts = 100,
            CloseHoldConfirmMs = 2000
        };

        GapSignalTriggerResult? expected = null;
        GapSignalTriggerResult? actual = null;
        foreach (var (second, gap) in new[] { (0, 40), (1, 30), (2, 20) })
        {
            var snapshot = Snapshot(second, gapSell: gap);
            expected = withoutLogger.ProcessSnapshot(snapshot, config, TradingOpenMode.GapBuy);
            actual = withLogger.ProcessSnapshot(snapshot, config, TradingOpenMode.GapBuy);
        }

        AssertTriggerEqual(expected, actual);
    }

    private static GapSignalConfirmationConfig CloseConfig(CloseGapMode mode) =>
        new(
            ConfirmGapPts: 0,
            OpenPts: 0,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 50,
            ClosePts: 100,
            CloseHoldConfirmMs: 2000,
            CloseGapMode: mode,
            CloseGapStability: Stability);

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

    private static void AssertTriggerListsEqual(
        IReadOnlyList<GapSignalTriggerResult> expected,
        IReadOnlyList<GapSignalTriggerResult> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            AssertTriggerEqual(expected[index], actual[index]);
        }
    }

    private static void AssertTriggerEqual(
        GapSignalTriggerResult? expected,
        GapSignalTriggerResult? actual)
    {
        if (expected is null || actual is null)
        {
            Assert.Equal(expected is null, actual is null);
            return;
        }

        Assert.Equal(expected.Triggered, actual.Triggered);
        Assert.Equal(expected.Action, actual.Action);
        Assert.Equal(expected.TriggerType, actual.TriggerType);
        Assert.Equal(expected.PrimarySide, actual.PrimarySide);
        Assert.Equal(expected.BuyGaps, actual.BuyGaps);
        Assert.Equal(expected.SellGaps, actual.SellGaps);
        Assert.Equal(expected.LastBuyGap, actual.LastBuyGap);
        Assert.Equal(expected.LastSellGap, actual.LastSellGap);
        Assert.Equal(expected.CloseReason, actual.CloseReason);
        Assert.Equal(expected.CloseGapMode, actual.CloseGapMode);
    }

    private sealed class CaptureLogger : ISlotLogger
    {
        public void Log(string message) { }
    }
}
