using System.Globalization;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.HoldConfirm;

/// <summary>
/// Phase 0 của hold_confirm_mode: khoá hành vi mode ms (TIME_AND_MIN_SAMPLES) TRƯỚC khi sửa.
/// Golden sinh từ code chưa có hold_confirm_mode; mọi phase sau phải pass KHÔNG sửa file golden.
/// Mỗi test dùng DiagnosticConfigId riêng vì dòng [GAP_STABILITY_RAW][CONFIG] khử trùng lặp bằng
/// state tĩnh theo config_id.
/// </summary>
public sealed class HoldConfirmMsCharacterizationTests
{
    private static readonly DateTime Start = new(2026, 11, 15, 1, 0, 0, DateTimeKind.Utc);

    private static readonly GapStabilityConfig Stability = new(10, 0.50, 4.0, 3, 0.45, 0.60);

    [Fact]
    public void GapCycleState_MsTransitions_MatchGolden()
    {
        var state = new GapCycleState();
        var transcript = new StringBuilder();
        var step = 0;

        void Run(int ms, int? gap, bool hasData = true, bool confirm = true, int hold = 1000, int limit = 0)
        {
            var update = state.Process(Start.AddMilliseconds(ms), gap, hasData, confirm, Stability, hold, limit);
            transcript.Append(CultureInfo.InvariantCulture, $"{++step:00} t={ms} gap={Fmt(gap)} -> ")
                .Append(Describe(update)).Append('\n');
        }

        Run(0, 100);
        Run(500, 105);
        Run(1000, 110);              // đủ 3 mẫu + 1000ms -> Stable
        Run(1500, 120);
        Run(2000, 300);              // delta vượt tolerance -> NewCycle
        Run(1800, 310);              // timestamp lùi -> ResetTimestamp
        Run(2500, -50);              // đổi dấu -> NewCycle
        Run(3000, -55, confirm: false);
        Run(3500, 100, hasData: false);
        Run(4000, 100);
        Run(4500, 150);
        Run(5000, 60);
        Run(5500, 140);
        Run(6000, 55);               // dải rộng -> có thể Unstable
        Run(6500, 600, limit: 500);  // vượt limit_max_gap -> Rejected
        Run(7000, 200, hold: 0);     // hold 0 -> chỉ còn MinStableSamples
        Run(7050, 205, hold: 0);
        Run(7100, 210, hold: 0);
        Run(7150, 212, hold: 0);
        Run(8000, 100, hold: 0);     // delta -> NewCycle từ 100
        Run(8050, 150, hold: 0);
        Run(8100, 50, hold: 0);      // MAD 50 / center 100 = 0.5 > 0.45 -> BecameUnstable
        Run(8150, 55, hold: 0);
        transcript.Append("RESET -> ").Append(Describe(state.Reset("characterization"))).Append('\n');

        GoldenTranscript.AssertMatches("ms-gap-cycle-state", transcript.ToString());
    }

    [Fact]
    public void OpenEngine_MsLogsAndTriggers_MatchGolden()
    {
        var logger = new TranscriptLogger();
        var engine = new GapSignalConfirmationEngine(logger);
        var config = new GapSignalConfirmationConfig(
            ConfirmGapPts: 50,
            OpenPts: 100,
            HoldConfirmMs: 1000,
            OpenGapStability: Stability,
            DiagnosticConfigId: "HC-GOLDEN-OPEN",
            DiagnosticSymbol: "XAUUSD|XAUUSD",
            DiagnosticMaxGap: 700,
            SignalCycleSize: 3);

        var series = new (int Ms, int? Buy, int? Sell)[]
        {
            (0, 60, -200), (500, 70, -210), (1000, 80, -220), (1500, 90, -230), (2000, 110, -240),
            (2500, 115, -250), (3000, 20, -120), (3500, 40, -110), (4000, 60, -105), (4500, 65, -108),
            (5000, 300, -30), (5500, 310, -20), (6000, 320, -10), (6500, 330, -5), (7000, 340, 0),
        };

        foreach (var (ms, buy, sell) in series)
        {
            logger.Note($"tick t={ms} gapBuy={Fmt(buy)} gapSell={Fmt(sell)}");
            foreach (var trigger in engine.ProcessSnapshot(Snapshot(ms, buy, sell), config))
            {
                logger.Note("TRIGGER " + DescribeTrigger(trigger));
            }
        }

        engine.Reset();
        logger.Note("after reset");
        GoldenTranscript.AssertMatches("ms-open-engine", logger.Build());
    }

    [Fact]
    public void NormalCloseEngine_MsLogsAndTriggers_MatchGolden()
    {
        var logger = new TranscriptLogger();
        var engine = new CloseSignalEngine(logger);
        var config = CloseConfig("HC-GOLDEN-NORMAL", CloseGapMode.Normal);

        RunClose(logger, engine, config, TradingOpenMode.GapBuy, new (int, int?, int?)[]
        {
            (0, 100, -60), (500, 100, -70), (1000, 100, -80), (1500, 100, -90), (2000, 100, -105),
            (2500, 100, -40), (3000, 100, -120), (3500, 100, -125), (4000, 100, -130), (4500, 100, -128),
        });

        GoldenTranscript.AssertMatches("ms-normal-close-engine", logger.Build());
    }

    [Fact]
    public void SosCloseEngine_MsLogsAndTriggers_MatchGolden()
    {
        var logger = new TranscriptLogger();
        var engine = new CloseSignalEngine(logger);
        var config = CloseConfig("HC-GOLDEN-SOS", CloseGapMode.Sos);

        // Vị thế Sell -> đóng theo GapBuy; SOS quy ngưỡng về hướng hồi vào trong (gap >= -|x|).
        RunClose(logger, engine, config, TradingOpenMode.GapSell, new (int, int?, int?)[]
        {
            (0, -40, -200), (500, -35, -200), (1000, -30, -200), (1500, -20, -200), (2000, 10, -200),
            (2500, -200, -200), (3000, -45, -200), (3500, -42, -200), (4000, -41, -200), (4500, -40, -200),
        });

        GoldenTranscript.AssertMatches("ms-sos-close-engine", logger.Build());
    }

    [Fact]
    public void TpCloseEngine_MsLogsAndTriggers_MatchGolden()
    {
        var logger = new TranscriptLogger();
        var engine = new CloseSignalEngine(logger);
        var config = CloseConfig("HC-GOLDEN-TP", CloseGapMode.Normal) with
        {
            CloseConfirmTpProfit = 2,
            CloseTpProfit = 5,
        };

        var profits = new (int Ms, double? Profit)[]
        {
            (0, 1), (500, 2.5), (1000, 3), (1500, 6), (2000, 1), (2500, 3), (3000, 4), (3500, 5.5), (4000, 6), (4500, null),
        };

        foreach (var (ms, profit) in profits)
        {
            logger.Note($"tick t={ms} profit={(profit.HasValue ? profit.Value.ToString(CultureInfo.InvariantCulture) : "null")}");
            var trigger = engine.ProcessSnapshot(Snapshot(ms, 0, 0), config, TradingOpenMode.GapBuy, profit);
            if (trigger is not null)
            {
                logger.Note("TRIGGER " + DescribeTrigger(trigger));
            }
        }

        GoldenTranscript.AssertMatches("ms-tp-close-engine", logger.Build());
    }

    [Fact]
    public void SignalOutcomeLine_MsHoldFields_MatchGolden()
    {
        var logger = new OutcomeLogger();
        var tracker = new SignalGapOutcomeTracker(logger, 2, 16, 30_000, 600, 3_000, 16);
        tracker.OnSignalPending(new SignalOutcomeSignal(
            SignalId: "sig-1",
            CycleId: "cycle-1",
            SlotId: null,
            Action: GapSignalAction.Open,
            Side: GapSignalSide.Buy,
            TriggerType: GapSignalTriggerType.OpenByGapBuy,
            TriggeredAtUtc: Start,
            Symbol: "XAUUSD|XAUUSD.s",
            PointMultiplier: 100,
            ABid: 2411.35m,
            AAsk: 2411.55m,
            BBid: 2411.95m,
            BAsk: 2412.15m,
            GapBuy: 40,
            GapSell: -20,
            SignalGaps: [38, 39, 40],
            ConfirmGapPts: 25,
            OpenPts: 30,
            CloseConfirmGapPts: 20,
            ClosePts: 15,
            LimitMaxGap: 120,
            MaxGap: 700,
            SignalCycleSize: 3,
            HoldConfirmMs: 1500,
            CloseHoldConfirmMs: 2500));
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.OnTick(new SignalOutcomeTick(Start.AddMilliseconds(50), 41, -19));
        tracker.OnTick(new SignalOutcomeTick(Start.AddMilliseconds(100), 42, -18));

        GoldenTranscript.AssertMatches("ms-signal-outcome", string.Join('\n', logger.Lines) + "\n");
    }

    private static void RunClose(
        TranscriptLogger logger,
        CloseSignalEngine engine,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        IEnumerable<(int Ms, int? Buy, int? Sell)> series)
    {
        foreach (var (ms, buy, sell) in series)
        {
            logger.Note($"tick t={ms} gapBuy={Fmt(buy)} gapSell={Fmt(sell)}");
            var trigger = engine.ProcessSnapshot(Snapshot(ms, buy, sell), config, openMode, slotProfit: null);
            if (trigger is not null)
            {
                logger.Note("TRIGGER " + DescribeTrigger(trigger));
            }
        }

        engine.Reset();
        logger.Note("after reset");
    }

    private static GapSignalConfirmationConfig CloseConfig(string configId, CloseGapMode mode) =>
        new(
            ConfirmGapPts: 0,
            OpenPts: 0,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 50,
            ClosePts: 100,
            CloseHoldConfirmMs: 1000,
            CloseGapMode: mode,
            CloseGapStability: Stability,
            DiagnosticConfigId: configId,
            DiagnosticSymbol: "XAUUSD|XAUUSD",
            DiagnosticMaxGap: 700,
            SignalCycleSize: 3);

    private static GapSignalSnapshot Snapshot(int ms, int? gapBuy, int? gapSell) =>
        new(
            Start.AddMilliseconds(ms),
            ExchangeABid: 2411.00m,
            ExchangeAAsk: 2411.20m,
            ExchangeBBid: 2411.50m,
            ExchangeBAsk: 2411.70m,
            gapBuy,
            gapSell,
            PointMultiplier: 100);

    private static string Describe(GapCycleUpdateResult update) =>
        $"{update.Transition} | current {DescribeCycle(update.CurrentCycle)}" +
        (update.CompletedCycle is null ? string.Empty : $" | completed {DescribeCycle(update.CompletedCycle)}");

    private static string DescribeCycle(GapCycleSnapshot cycle) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"id={(cycle.CycleId.Length == 0 ? "-" : cycle.CycleId)} status={cycle.Status} samples={cycle.SampleCount} " +
            $"gaps=[{string.Join(',', cycle.Gaps)}] duration={cycle.DurationMs:0.####} " +
            $"center={Num(cycle.Center)} tol={Num(cycle.Tolerance)} disp={Num(cycle.Dispersion)} drift={Num(cycle.Drift)} " +
            $"reason=\"{cycle.Reason}\"");

    private static string DescribeTrigger(GapSignalTriggerResult trigger) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"type={trigger.TriggerType} action={trigger.Action} side={trigger.PrimarySide} reason={trigger.CloseReason} " +
            $"at={trigger.TriggeredAtUtc:O} buyGaps=[{string.Join(',', trigger.BuyGaps)}] sellGaps=[{string.Join(',', trigger.SellGaps)}] " +
            $"lastBuy={Fmt(trigger.LastBuyGap)} lastSell={Fmt(trigger.LastSellGap)} mode={trigger.CloseGapMode} " +
            $"effConfirm={Fmt(trigger.EffectiveCloseConfirmGapPts)} effClose={Fmt(trigger.EffectiveCloseGapPts)} " +
            $"effHoldMs={Fmt(trigger.EffectiveCloseHoldMs)} tp={Num(trigger.CloseTpProfit)} " +
            $"cycle={(trigger.DiagnosticCycleId.Length == 0 ? "-" : trigger.DiagnosticCycleId)} " +
            $"signal={(trigger.DiagnosticSignalId.Length == 0 ? "-" : trigger.DiagnosticSignalId)}");

    private static string Fmt(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string Num(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "null";

    private sealed class OutcomeLogger : ISignalOutcomeRawLogger
    {
        public List<string> Lines { get; } = [];

        public void LogSignalOutcomeRaw(string message) => Lines.Add(message);
    }
}
