using System.Globalization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Tests.FastLeg;

// Phase 3: verdict sàn C được CHỞ theo (snapshot → trigger → log). Ở chế độ observe (BlocksOpen/BlocksClose = false)
// engine không chặn gì — kể cả khi verdict là TooFast. Gate của chế độ filter test ở FastLegEngineGateTests.
public sealed class FastLegVerdictPropagationTests
{
    private static readonly DateTime Start = new(2026, 11, 3, 2, 0, 0, DateTimeKind.Utc);
    private static readonly GapStabilityConfig Stability = new(10, 0.50, 4.0, 3, 0.35, 0.40);

    private static FastLegVerdict TooFast() => new(
        FastLegVerdictKind.TooFast,
        MovePts: 6.5m,
        VelocityPtsPerSec: 32.5m,
        SpanMs: 200,
        AgeMs: 40,
        CBid: 3946.1m,
        CAsk: 3946.27m,
        FastLegMode.Observe,
        BlocksOpen: false,
        BlocksClose: false,
        DownReason: null,
        FastLegHeartbeatState.Alive);

    // ---------- Engine: trigger mang đúng verdict của tick phát ----------

    [Fact]
    public void OpenTrigger_CarriesVerdictOfTriggerTick_AndDoesNotBlock()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 50, OpenPts: 100, HoldConfirmMs: 0, OpenGapStability: Stability, SignalCycleSize: 3);
        var verdict = TooFast();

        Assert.Empty(engine.ProcessSnapshot(Snapshot(0, gapBuy: 100, fastLeg: verdict), config));
        Assert.Empty(engine.ProcessSnapshot(Snapshot(1, gapBuy: 110, fastLeg: verdict), config));
        var trigger = Assert.Single(engine.ProcessSnapshot(Snapshot(2, gapBuy: 120, fastLeg: verdict), config));

        // TooFast ở chế độ observe vẫn phát: chỉ ghi dữ liệu, không chặn.
        Assert.Same(verdict, trigger.FastLeg);
        Assert.Equal(0, trigger.FastLegDeferredTicks);
        Assert.Equal(0, trigger.FastLegDeferredMs);
    }

    [Fact]
    public void OpenTrigger_WithoutVerdict_HasNullFastLeg()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 50, OpenPts: 100, HoldConfirmMs: 0, OpenGapStability: Stability, SignalCycleSize: 3);

        engine.ProcessSnapshot(Snapshot(0, gapBuy: 100), config);
        engine.ProcessSnapshot(Snapshot(1, gapBuy: 110), config);
        var trigger = Assert.Single(engine.ProcessSnapshot(Snapshot(2, gapBuy: 120), config));

        Assert.Null(trigger.FastLeg);
    }

    [Fact]
    public void OpenTrigger_UsesVerdictOfReleaseTickNotEarlierTicks()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 50, OpenPts: 100, HoldConfirmMs: 0, OpenGapStability: Stability, SignalCycleSize: 3);
        var early = TooFast();
        var last = TooFast() with { Kind = FastLegVerdictKind.Confirmed, MovePts = 1m, BlocksOpen = false, BlocksClose = false };

        engine.ProcessSnapshot(Snapshot(0, gapBuy: 100, fastLeg: early), config);
        engine.ProcessSnapshot(Snapshot(1, gapBuy: 110, fastLeg: early), config);
        var trigger = Assert.Single(engine.ProcessSnapshot(Snapshot(2, gapBuy: 120, fastLeg: last), config));

        Assert.Same(last, trigger.FastLeg);
    }

    [Fact]
    public void NormalCloseTrigger_CarriesVerdict()
    {
        var engine = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 0, OpenPts: 0, HoldConfirmMs: 0,
            CloseConfirmGapPts: 50, ClosePts: 100, CloseHoldConfirmMs: 0,
            CloseGapStability: Stability, SignalCycleSize: 3);
        var verdict = TooFast();

        Assert.Null(engine.ProcessSnapshot(Snapshot(0, gapSell: -100, fastLeg: verdict), config, TradingOpenMode.GapBuy, null));
        Assert.Null(engine.ProcessSnapshot(Snapshot(1, gapSell: -120, fastLeg: verdict), config, TradingOpenMode.GapBuy, null));
        var trigger = engine.ProcessSnapshot(Snapshot(2, gapSell: -110, fastLeg: verdict), config, TradingOpenMode.GapBuy, null);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Gap, trigger!.CloseReason);
        Assert.Same(verdict, trigger.FastLeg);
    }

    [Fact]
    public void TpTrigger_CarriesVerdict()
    {
        var engine = new CloseSignalEngine();
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 5, OpenPts: 8, HoldConfirmMs: 500,
            CloseConfirmGapPts: 50, ClosePts: 80,
            CloseConfirmTpProfit: 10, CloseTpProfit: 15, CloseHoldConfirmMs: 400,
            SignalCycleSize: 3);
        var verdict = TooFast();

        Assert.Null(engine.ProcessSnapshot(SnapshotMs(0, verdict), config, TradingOpenMode.GapBuy, 10));
        Assert.Null(engine.ProcessSnapshot(SnapshotMs(180, verdict), config, TradingOpenMode.GapBuy, 12));
        var trigger = engine.ProcessSnapshot(SnapshotMs(420, verdict), config, TradingOpenMode.GapBuy, 15);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger!.CloseReason);
        Assert.Same(verdict, trigger.FastLeg);
    }

    // ---------- Gap-tick: F tắt = y hệt từng byte; F bật = c_* trước point= ----------

    [Fact]
    public void GapTick_WithoutVerdict_IsByteIdenticalToLegacyFormat()
    {
        var metrics = Metrics();
        var time = metrics.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        var line = GapTickLineFormatter.Format(metrics, point: 100);

        Assert.Equal(
            $"[{time}] [GAP_TICK] gap_buy=12 gap_sell=-3 a_sym=XAUUSD a_bid=2412.35 a_ask=2412.55 a_spread=20 a_lat=8 " +
            "b_sym=XAUUSD.s b_bid=2412.67 b_ask=2412.88 b_spread=21 b_lat=11 point=100",
            line);
        Assert.Equal(line, GapTickLineFormatter.Format(metrics, point: 100, fastLeg: null));
    }

    [Fact]
    public void GapTick_WithVerdict_InsertsCFieldsBeforePoint()
    {
        var line = GapTickLineFormatter.Format(Metrics(), point: 100, fastLeg: TooFast());

        Assert.EndsWith(
            " b_lat=11 c_bid=3946.1 c_ask=3946.27 c_age=40 c_move=6.5 c_verdict=TooFast point=100",
            line,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GapTick_DownVerdictWithoutPrices_WritesDashes()
    {
        var down = TooFast() with
        {
            Kind = FastLegVerdictKind.Down,
            MovePts = 0m,
            AgeMs = null,
            CBid = null,
            CAsk = null,
            DownReason = "map_not_found"
        };

        var line = GapTickLineFormatter.Format(Metrics(), point: 100, fastLeg: down);

        Assert.Contains(" c_bid=- c_ask=- c_age=- c_move=0 c_verdict=Down point=100", line, StringComparison.Ordinal);
    }

    // ---------- Signal-outcome: fast_* chỉ xuất hiện khi có verdict ----------

    [Fact]
    public void OutcomeSignalLine_WithVerdict_AppendsFastFields()
    {
        var logger = new CaptureLogger();
        var tracker = new SignalGapOutcomeTracker(logger, futureTickCount: 3);

        tracker.OnSignalPending(OutcomeSignal() with { FastLeg = TooFast(), FastLegDeferredMs = 150 });
        tracker.AttachStt("sig-1", 7, "AUTO-0003-1");

        var line = Assert.Single(logger.Lines);
        Assert.EndsWith(
            "future_ticks_planned=3 fast_verdict=TooFast fast_move_pts=6.5 fast_age_ms=40 fast_deferred_ms=150",
            line,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OutcomeSignalLine_WithoutVerdict_HasNoFastFields()
    {
        var logger = new CaptureLogger();
        var tracker = new SignalGapOutcomeTracker(logger, futureTickCount: 3);

        tracker.OnSignalPending(OutcomeSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-1");

        var line = Assert.Single(logger.Lines);
        Assert.EndsWith("future_ticks_planned=3", line, StringComparison.Ordinal);
        Assert.DoesNotContain("fast_", line, StringComparison.Ordinal);
    }

    private static GapSignalSnapshot Snapshot(
        int second,
        int? gapBuy = null,
        int? gapSell = null,
        FastLegVerdict? fastLeg = null) =>
        new(
            Start.AddSeconds(second),
            ExchangeABid: 1.1000m,
            ExchangeAAsk: 1.1001m,
            ExchangeBBid: 1.1101m,
            ExchangeBAsk: 1.1102m,
            gapBuy,
            gapSell,
            PointMultiplier: 100,
            FastLeg: fastLeg);

    private static GapSignalSnapshot SnapshotMs(int milliseconds, FastLegVerdict fastLeg) =>
        new(
            Start.AddMilliseconds(milliseconds),
            ExchangeABid: 1.1000m,
            ExchangeAAsk: 1.1001m,
            ExchangeBBid: 1.1101m,
            ExchangeBAsk: 1.1102m,
            GapBuy: 0,
            GapSell: 0,
            PointMultiplier: 100,
            FastLeg: fastLeg);

    private static DashboardMetrics Metrics()
    {
        var a = new ExchangeDashboardMetrics(
            Symbol: "XAUUSD", Bid: 2412.35m, Ask: 2412.55m, Spread: 20m, LatencyMs: 8m, Tps: 5f,
            Time: "14:32:07.412", MaxLatMs: 30m, AvgLatMs: 10m, IsConnected: true, Error: null);
        var b = new ExchangeDashboardMetrics(
            Symbol: "XAUUSD.s", Bid: 2412.67m, Ask: 2412.88m, Spread: 21m, LatencyMs: 11m, Tps: 6f,
            Time: "14:32:07.418", MaxLatMs: 33m, AvgLatMs: 12m, IsConnected: true, Error: null);
        return new DashboardMetrics(a, b, 12, -3, IsConnectedA: true, IsConnectedB: true,
            TimestampUtc: new DateTime(2026, 9, 2, 7, 32, 7, 412, DateTimeKind.Utc));
    }

    private static SignalOutcomeSignal OutcomeSignal() => new(
        SignalId: "sig-1",
        CycleId: "OPEN-BUY-1",
        SlotId: null,
        Action: GapSignalAction.Open,
        Side: GapSignalSide.Buy,
        TriggerType: GapSignalTriggerType.OpenByGapBuy,
        TriggeredAtUtc: Start,
        Symbol: "XAUUSD.s|XAUUSD.s",
        PointMultiplier: 100,
        ABid: 2411.35m,
        AAsk: 2411.55m,
        BBid: 2411.95m,
        BAsk: 2412.15m,
        GapBuy: 40,
        GapSell: -20,
        SignalGaps: new[] { 38, 39, 40 },
        ConfirmGapPts: 25,
        OpenPts: 30,
        CloseConfirmGapPts: 20,
        ClosePts: 15,
        LimitMaxGap: 120,
        MaxGap: 80,
        SignalCycleSize: 3);

    private sealed class CaptureLogger : ISignalOutcomeRawLogger
    {
        public List<string> Lines { get; } = new();

        public void LogSignalOutcomeRaw(string message) => Lines.Add(message);
    }
}
