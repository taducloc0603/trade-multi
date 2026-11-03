using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.FastLeg;

// Phase 4: gate sàn C TRONG engine (D4: hoãn, không reset cycle), trần MaxDeferMs (D8), SOS bỏ qua (D5),
// Close không chờ C hỏng (D6). Verdict dựng tay: BlocksOpen/BlocksClose đúng như evaluator sinh ra ở chế độ filter.
public sealed class FastLegEngineGateTests
{
    private static readonly DateTime Start = new(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc);
    private static readonly GapStabilityConfig Stability = new(10, 0.50, 4.0, 3, 0.35, 0.40);
    private static readonly GapStabilityConfig CloseStability = new(10, 0.60, 4.0, 3, 0.45, 0.60);

    private static FastLegVerdict Verdict(
        FastLegVerdictKind kind,
        bool blocksOpen,
        bool blocksClose,
        int maxDeferMs = 3000) => new(
        kind,
        MovePts: kind == FastLegVerdictKind.TooFast ? 8m : 1m,
        VelocityPtsPerSec: 0m,
        SpanMs: 200,
        AgeMs: 50,
        CBid: 3946.1m,
        CAsk: 3946.27m,
        FastLegMode.Filter,
        blocksOpen,
        blocksClose,
        DownReason: kind == FastLegVerdictKind.Down ? "heartbeat_stale" : null,
        FastLegHeartbeatState.Alive,
        maxDeferMs);

    private static FastLegVerdict TooFast(int maxDeferMs = 3000) => Verdict(FastLegVerdictKind.TooFast, true, true, maxDeferMs);
    private static FastLegVerdict Confirmed() => Verdict(FastLegVerdictKind.Confirmed, false, false);

    // ================= OPEN =================

    private static GapSignalConfirmationConfig OpenConfig(int openMaxTimesTick = 0) => new(
        ConfirmGapPts: 50, OpenPts: 100, HoldConfirmMs: 0, OpenMaxTimesTick: openMaxTimesTick,
        OpenGapStability: Stability, SignalCycleSize: 3);

    private static IReadOnlyList<GapSignalTriggerResult> Open(
        GapSignalConfirmationEngine engine, int second, int gapBuy, FastLegVerdict? c, int? config = null) =>
        engine.ProcessSnapshot(Snap(Start.AddSeconds(second), gapBuy: gapBuy, c: c), OpenConfig(config ?? 0));

    [Fact]
    public void Open_TooFast_DefersWithoutResettingCycle_ThenReleasesOnConfirmed()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);

        Assert.Empty(Open(engine, 0, 100, Confirmed()));
        Assert.Empty(Open(engine, 1, 110, Confirmed()));
        Assert.Empty(Open(engine, 2, 120, TooFast()));            // đủ điều kiện nhưng giá C đang chạy ⇒ hoãn
        var trigger = Assert.Single(Open(engine, 3, 125, Confirmed()));

        // Cycle KHÔNG bị reset khi hoãn: trigger mang cả mẫu trước và sau tick hoãn, không phải chờ lại hold/MinSamples.
        Assert.Equal([100, 110, 120, 125], trigger.BuyGaps);
        Assert.Equal(1, trigger.FastLegDeferredTicks);
        Assert.Equal(1000, trigger.FastLegDeferredMs);
        Assert.Equal(Start.AddSeconds(3), trigger.TriggeredAtUtc);   // thời điểm tick thả ⇒ không bị SIGNAL_EXPIRED
        Assert.Equal(FastLegVerdictKind.Confirmed, trigger.FastLeg!.Kind);
        Assert.Single(logger.Lines, l => l.StartsWith("[FAST_LEG][DEFER] action=OPEN side=BUY", StringComparison.Ordinal));
        Assert.Single(logger.Lines, l => l.Contains("[FAST_LEG][RELEASE] action=OPEN side=BUY", StringComparison.Ordinal)
            && l.Contains("deferred_ticks=1", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_ManyDeferredTicks_LogOneDeferAndOneRelease()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);

        Open(engine, 0, 100, Confirmed());
        Open(engine, 1, 110, Confirmed());
        // Hoãn nhiều tick liên tiếp, mỗi tick cách 200 ms (trần mặc định 3000 ms).
        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(engine.ProcessSnapshot(Snap(Start.AddSeconds(2).AddMilliseconds(200 * i), gapBuy: 120, c: TooFast()), OpenConfig()));
        }

        var trigger = Assert.Single(engine.ProcessSnapshot(Snap(Start.AddSeconds(3), gapBuy: 120, c: Confirmed()), OpenConfig()));

        Assert.Equal(5, trigger.FastLegDeferredTicks);
        Assert.Equal(1, logger.Lines.Count(l => l.StartsWith("[FAST_LEG][DEFER]", StringComparison.Ordinal)));
        Assert.Equal(1, logger.Lines.Count(l => l.StartsWith("[FAST_LEG][RELEASE]", StringComparison.Ordinal)));
    }

    [Fact]
    public void Open_DeferBeyondMaxDeferMs_AbandonsCycle_NextSignalNeedsNewCycle()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);

        Open(engine, 0, 100, Confirmed());
        Open(engine, 1, 110, Confirmed());
        Assert.Empty(Open(engine, 2, 120, TooFast(maxDeferMs: 500)));
        Assert.Empty(Open(engine, 3, 120, TooFast(maxDeferMs: 500)));   // hoãn 1000 ms ≥ 500 ⇒ bỏ cycle
        // C đã yên nhưng cycle cũ đã bị bỏ: phải gom lại đủ mẫu, KHÔNG phát ngay.
        Assert.Empty(Open(engine, 4, 120, Confirmed()));

        Assert.Contains(logger.Lines, l => l.StartsWith("[FAST_LEG][ABANDON] action=OPEN side=BUY", StringComparison.Ordinal)
            && l.Contains("reason=defer_timeout", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l => l.StartsWith("[FAST_LEG][RELEASE]", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_DeferBeyondOpenMaxTimesTick_ResetsAsBefore()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);

        Open(engine, 0, 100, Confirmed(), config: 4);
        Open(engine, 1, 110, Confirmed(), config: 4);
        Assert.Empty(Open(engine, 2, 120, TooFast(), config: 4));      // 3 mẫu, hoãn
        Assert.Empty(Open(engine, 3, 120, TooFast(), config: 4));      // 4 mẫu, vẫn hoãn
        Assert.Empty(Open(engine, 4, 120, Confirmed(), config: 4));    // 5 mẫu > 4 ⇒ reset như cũ, không phát

        Assert.Contains(logger.Lines, l => l.Contains("[FAST_LEG][ABANDON]", StringComparison.Ordinal)
            && l.Contains("reason=max_times_tick", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_CycleBreaksWhileDeferred_AbandonsDefer()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);

        Open(engine, 0, 100, Confirmed());
        Open(engine, 1, 110, Confirmed());
        Assert.Empty(Open(engine, 2, 120, TooFast()));
        Assert.Empty(Open(engine, 3, 10, Confirmed()));   // gap rơi dưới confirm ⇒ cycle vỡ

        Assert.Contains(logger.Lines, l => l.Contains("[FAST_LEG][ABANDON] action=OPEN side=BUY", StringComparison.Ordinal)
            && l.Contains("reason=cycle_reset", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_ExternalResetWhileDeferred_AbandonsAndNeedsNewCycle()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);

        Open(engine, 0, 100, Confirmed());
        Open(engine, 1, 110, Confirmed());
        Assert.Empty(Open(engine, 2, 120, TooFast()));
        engine.Reset();   // vd close thắng ở tick thả ⇒ coordinator reset open engine
        Assert.Empty(Open(engine, 3, 125, Confirmed()));

        Assert.Contains(logger.Lines, l => l.Contains("reason=engine_reset", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FastLegVerdictKind.Stale, true, false)]   // fail_open tắt ⇒ evaluator đặt BlocksOpen ⇒ hoãn
    [InlineData(FastLegVerdictKind.Down, true, false)]
    [InlineData(FastLegVerdictKind.Stale, false, true)]   // fail_open bật ⇒ không chặn ⇒ phát
    [InlineData(FastLegVerdictKind.Down, false, true)]
    public void Open_StaleOrDown_FollowsBlocksOpen(FastLegVerdictKind kind, bool blocksOpen, bool expectTrigger)
    {
        var engine = new GapSignalConfirmationEngine();
        var c = Verdict(kind, blocksOpen, blocksClose: false);

        Open(engine, 0, 100, c);
        Open(engine, 1, 110, c);
        var results = Open(engine, 2, 120, c);

        Assert.Equal(expectTrigger, results.Count == 1);
    }

    [Fact]
    public void Open_ObserveTooFast_TriggersImmediately()
    {
        var engine = new GapSignalConfirmationEngine();
        var observe = Verdict(FastLegVerdictKind.TooFast, blocksOpen: false, blocksClose: false) with { Mode = FastLegMode.Observe };

        Open(engine, 0, 100, observe);
        Open(engine, 1, 110, observe);
        var trigger = Assert.Single(Open(engine, 2, 120, observe));

        Assert.Equal(FastLegVerdictKind.TooFast, trigger.FastLeg!.Kind);
        Assert.Equal(0, trigger.FastLegDeferredTicks);
    }

    [Fact]
    public void Open_SellSideDefersIndependently()
    {
        var engine = new GapSignalConfirmationEngine();

        engine.ProcessSnapshot(Snap(Start, gapSell: -100, c: Confirmed()), OpenConfig());
        engine.ProcessSnapshot(Snap(Start.AddSeconds(1), gapSell: -110, c: Confirmed()), OpenConfig());
        Assert.Empty(engine.ProcessSnapshot(Snap(Start.AddSeconds(2), gapSell: -120, c: TooFast()), OpenConfig()));
        var trigger = Assert.Single(engine.ProcessSnapshot(Snap(Start.AddSeconds(3), gapSell: -125, c: Confirmed()), OpenConfig()));

        Assert.Equal(GapSignalTriggerType.OpenByGapSell, trigger.TriggerType);
        Assert.Equal(1, trigger.FastLegDeferredTicks);
    }

    // ================= NORMAL / SOS CLOSE =================

    private static GapSignalConfirmationConfig CloseConfig() => new(
        ConfirmGapPts: 0, OpenPts: 0, HoldConfirmMs: 0,
        CloseConfirmGapPts: 50, ClosePts: 100, CloseHoldConfirmMs: 0,
        CloseGapStability: CloseStability, SignalCycleSize: 3);

    private static GapSignalConfirmationConfig SosConfig() => new(
        ConfirmGapPts: 0, OpenPts: 0, HoldConfirmMs: 0,
        CloseConfirmGapPts: 5, ClosePts: 3, CloseHoldConfirmMs: 0,
        CloseGapStability: CloseStability, SignalCycleSize: 3, CloseGapMode: CloseGapMode.Sos);

    private static GapSignalTriggerResult? Close(
        CloseSignalEngine engine, GapSignalConfirmationConfig config, int second, int gapSell, FastLegVerdict? c) =>
        engine.ProcessSnapshot(Snap(Start.AddSeconds(second), gapSell: gapSell, c: c), config, TradingOpenMode.GapBuy, null);

    [Fact]
    public void NormalClose_TooFast_DefersThenReleases()
    {
        var logger = new CaptureLogger();
        var engine = new CloseSignalEngine(logger);

        Assert.Null(Close(engine, CloseConfig(), 0, -100, Confirmed()));
        Assert.Null(Close(engine, CloseConfig(), 1, -120, Confirmed()));
        Assert.Null(Close(engine, CloseConfig(), 2, -110, TooFast()));
        var trigger = Close(engine, CloseConfig(), 3, -115, Confirmed());

        Assert.NotNull(trigger);
        Assert.Equal([-100, -120, -110, -115], trigger!.SellGaps);
        Assert.Equal(1, trigger.FastLegDeferredTicks);
        Assert.Contains(logger.Lines, l => l.StartsWith("[FAST_LEG][DEFER] action=CLOSE side=BUY", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalClose_ApplyToExitOff_TooFastDoesNotDefer()
    {
        // apply_to_exit tắt ⇒ evaluator đặt BlocksClose = false.
        var engine = new CloseSignalEngine();
        var c = Verdict(FastLegVerdictKind.TooFast, blocksOpen: true, blocksClose: false);

        Close(engine, CloseConfig(), 0, -100, c);
        Close(engine, CloseConfig(), 1, -120, c);

        Assert.NotNull(Close(engine, CloseConfig(), 2, -110, c));
    }

    [Theory]
    [InlineData(FastLegVerdictKind.Stale)]
    [InlineData(FastLegVerdictKind.Down)]
    public void NormalClose_NeverWaitsForBrokenC(FastLegVerdictKind kind)
    {
        // D6: Down/Stale chặn Open (fail_open tắt) nhưng KHÔNG bao giờ chặn Close.
        var engine = new CloseSignalEngine();
        var c = Verdict(kind, blocksOpen: true, blocksClose: false);

        Close(engine, CloseConfig(), 0, -100, c);
        Close(engine, CloseConfig(), 1, -120, c);

        Assert.NotNull(Close(engine, CloseConfig(), 2, -110, c));
    }

    [Fact]
    public void SosClose_TooFast_IsNeverDeferred()
    {
        var engine = new CloseSignalEngine();

        Close(engine, SosConfig(), 0, 5, TooFast());
        Close(engine, SosConfig(), 1, 4, TooFast());
        var trigger = Close(engine, SosConfig(), 2, 3, TooFast());

        Assert.NotNull(trigger);
        Assert.Equal(CloseGapMode.Sos, trigger!.CloseGapMode);
        Assert.Equal(0, trigger.FastLegDeferredTicks);
    }

    [Fact]
    public void NormalClose_DeferBeyondMaxDeferMs_Abandons()
    {
        var logger = new CaptureLogger();
        var engine = new CloseSignalEngine(logger);

        Close(engine, CloseConfig(), 0, -100, Confirmed());
        Close(engine, CloseConfig(), 1, -120, Confirmed());
        Assert.Null(Close(engine, CloseConfig(), 2, -110, TooFast(maxDeferMs: 500)));
        Assert.Null(Close(engine, CloseConfig(), 3, -110, TooFast(maxDeferMs: 500)));
        Assert.Null(Close(engine, CloseConfig(), 4, -110, Confirmed()));   // cycle mới, chưa đủ mẫu

        Assert.Contains(logger.Lines, l => l.Contains("[FAST_LEG][ABANDON] action=CLOSE", StringComparison.Ordinal)
            && l.Contains("reason=defer_timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalClose_SosFlipWhileDeferred_AbandonsDefer()
    {
        var logger = new CaptureLogger();
        var engine = new CloseSignalEngine(logger);

        Close(engine, CloseConfig(), 0, -100, Confirmed());
        Close(engine, CloseConfig(), 1, -120, Confirmed());
        Assert.Null(Close(engine, CloseConfig(), 2, -110, TooFast()));
        engine.ResetGapState();   // coordinator lật Normal ↔ SOS

        Assert.Contains(logger.Lines, l => l.Contains("[FAST_LEG][ABANDON] action=CLOSE side=BUY", StringComparison.Ordinal)
            && l.Contains("reason=engine_reset", StringComparison.Ordinal));
    }

    // ================= TP =================

    private static GapSignalConfirmationConfig TpConfig() => new(
        ConfirmGapPts: 5, OpenPts: 8, HoldConfirmMs: 500,
        CloseConfirmGapPts: 50, ClosePts: 80,
        CloseConfirmTpProfit: 10, CloseTpProfit: 15, CloseHoldConfirmMs: 400,
        SignalCycleSize: 3);

    private static GapSignalTriggerResult? Tp(CloseSignalEngine engine, int ms, double profit, FastLegVerdict c) =>
        engine.ProcessSnapshot(
            Snap(Start.AddMilliseconds(ms), gapBuy: 0, gapSell: 0, c: c), TpConfig(), TradingOpenMode.GapBuy, profit);

    [Fact]
    public void Tp_TooFast_DefersKeepingWindow_ThenReleases()
    {
        var engine = new CloseSignalEngine();

        Assert.Null(Tp(engine, 0, 10, Confirmed()));
        Assert.Null(Tp(engine, 180, 12, Confirmed()));
        Assert.Null(Tp(engine, 420, 15, TooFast()));        // đủ hold + đạt target nhưng C đang chạy ⇒ hoãn
        var trigger = Tp(engine, 500, 15, Confirmed());      // cửa sổ còn nguyên ⇒ phát ngay

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger!.CloseReason);
        Assert.Equal(1, trigger.FastLegDeferredTicks);
        Assert.Equal(80, trigger.FastLegDeferredMs);
    }

    [Fact]
    public void Tp_ProfitFallsBelowTargetWhileDeferred_Abandons()
    {
        var logger = new CaptureLogger();
        var engine = new CloseSignalEngine(logger);

        Tp(engine, 0, 10, Confirmed());
        Tp(engine, 180, 12, Confirmed());
        Assert.Null(Tp(engine, 420, 15, TooFast()));
        Assert.Null(Tp(engine, 500, 12, Confirmed()));       // rớt dưới target ⇒ TARGET_NOT_REACHED, bỏ hoãn

        Assert.Contains(logger.Lines, l => l.Contains("[FAST_LEG][ABANDON] action=TP", StringComparison.Ordinal)
            && l.Contains("reason=target_not_reached", StringComparison.Ordinal));
    }

    [Fact]
    public void NullFastLeg_EnginesBehaveAsBefore()
    {
        var open = new GapSignalConfirmationEngine();
        Open(open, 0, 100, null);
        Open(open, 1, 110, null);
        Assert.Single(Open(open, 2, 120, null));

        var close = new CloseSignalEngine();
        Close(close, CloseConfig(), 0, -100, null);
        Close(close, CloseConfig(), 1, -120, null);
        Assert.NotNull(Close(close, CloseConfig(), 2, -110, null));
    }

    private static GapSignalSnapshot Snap(DateTime at, int? gapBuy = null, int? gapSell = null, FastLegVerdict? c = null) =>
        new(at,
            ExchangeABid: 1.1000m,
            ExchangeAAsk: 1.1001m,
            ExchangeBBid: 1.1101m,
            ExchangeBAsk: 1.1102m,
            gapBuy,
            gapSell,
            PointMultiplier: 100,
            FastLeg: c);

    private sealed class CaptureLogger : ISlotLogger
    {
        public List<string> Lines { get; } = [];

        public void Log(string message) => Lines.Add(message);
    }
}
