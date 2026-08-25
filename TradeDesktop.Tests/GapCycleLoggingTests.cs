using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests;

public sealed class GapCycleLoggingTests
{
    private static readonly DateTime Start =
        new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);

    private static readonly GapStabilityConfig Stability =
        new(10, 0.50, 4.0, 3, 0.45, 0.60);

    [Fact]
    public void Open_LogsStateChangesAndTrigger_ButDoesNotLogJoinedTicks()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);
        var config = OpenConfig();

        engine.ProcessSnapshot(Snapshot(0, gapBuy: 100), config);
        engine.ProcessSnapshot(Snapshot(1, gapBuy: 110), config);
        engine.ProcessSnapshot(Snapshot(2, gapBuy: 120), config);

        Assert.Equal(4, logger.Messages.Count);
        Assert.Contains("[CYCLE_STARTED]", logger.Messages[0]);
        Assert.Contains("[CYCLE_STABLE]", logger.Messages[1]);
        Assert.Contains("[TRIGGER_EMITTED]", logger.Messages[2]);
        Assert.Contains("[CYCLE_RESET]", logger.Messages[3]);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("JOINED", StringComparison.Ordinal));

        var stable = logger.Messages[1];
        Assert.Contains("action=OPEN side=BUY slot_id=-", stable);
        Assert.Contains("sample_count=3", stable);
        Assert.Contains("duration_ms=2000", stable);
        Assert.Contains("center=110", stable);
        Assert.Contains("mad=10", stable);
        Assert.Contains("tolerance=55", stable);
        Assert.Contains("new_gap=120", stable);
        Assert.Contains("delta=10", stable);
        Assert.Contains("dispersion=", stable);
        Assert.Contains("drift=", stable);
        Assert.Contains("status=Stable", stable);
        Assert.Contains("reason=\"", stable);
    }

    [Fact]
    public void Open_LogsNewCycleAndRejectedWithDistinctReasons()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);
        var config = OpenConfig(open: 10_000, limitMaxGap: 500);

        engine.ProcessSnapshot(Snapshot(0, gapBuy: 100), config);
        engine.ProcessSnapshot(Snapshot(1, gapBuy: 300), config);
        engine.ProcessSnapshot(Snapshot(2, gapBuy: 550), config);

        var split = Assert.Single(logger.Messages.Where(message =>
            message.Contains("[NEW_CYCLE_CREATED]", StringComparison.Ordinal)));
        Assert.Contains("new_gap=300", split);
        Assert.Contains("Delta", split);

        var rejected = Assert.Single(logger.Messages.Where(message =>
            message.Contains("[CYCLE_REJECTED]", StringComparison.Ordinal)));
        Assert.Contains("new_gap=550", rejected);
        Assert.Contains("limit_max_gap 500", rejected);
        Assert.Contains("status=Rejected", rejected);
    }

    [Fact]
    public void CloseEngineCreatedForPositionSlot_LogsSlotIdAndCloseSide()
    {
        var logger = new CaptureLogger();
        var engine = new CloseSignalEngine(logger);
        _ = new PositionSlot(7, "PAIR-7", engine);
        var config = CloseConfig();

        engine.ProcessSnapshot(Snapshot(0, gapSell: -100), config, TradingOpenMode.GapBuy);
        engine.ProcessSnapshot(Snapshot(1, gapSell: -110), config, TradingOpenMode.GapBuy);
        engine.ProcessSnapshot(Snapshot(2, gapSell: -120), config, TradingOpenMode.GapBuy);

        Assert.Contains(logger.Messages, message =>
            message.Contains("[CYCLE_STABLE] action=CLOSE side=BUY slot_id=7", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[TRIGGER_EMITTED] action=CLOSE side=BUY slot_id=7", StringComparison.Ordinal));
    }

    private static GapSignalConfirmationConfig OpenConfig(
        int open = 100,
        int limitMaxGap = 0) =>
        new(
            ConfirmGapPts: 50,
            OpenPts: open,
            HoldConfirmMs: 2000,
            LimitMaxGap: limitMaxGap,
            OpenGapStability: Stability);

    private static GapSignalConfirmationConfig CloseConfig() =>
        new(
            ConfirmGapPts: 0,
            OpenPts: 0,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 50,
            ClosePts: 100,
            CloseHoldConfirmMs: 2000,
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

    private sealed class CaptureLogger : ISlotLogger
    {
        public List<string> Messages { get; } = [];

        public void Log(string message) => Messages.Add(message);
    }
}
