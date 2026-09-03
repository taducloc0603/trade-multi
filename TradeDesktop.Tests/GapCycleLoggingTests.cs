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
    public void Open_LogsCycleStartProgressCompletionAndTrigger()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);
        var config = OpenConfig();

        engine.ProcessSnapshot(Snapshot(0, gapBuy: 100), config);
        engine.ProcessSnapshot(Snapshot(1, gapBuy: 110), config);
        var emitted = Assert.Single(engine.ProcessSnapshot(Snapshot(2, gapBuy: 120), config));

        Assert.Contains(logger.Messages, message =>
            message.Contains("[OPEN_CYCLE][STARTED]", StringComparison.Ordinal)
            && message.Contains("count=1/3", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[OPEN_CYCLE][PROGRESS]", StringComparison.Ordinal)
            && message.Contains("count=2/3", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[OPEN_CYCLE][COMPLETED]", StringComparison.Ordinal)
            && message.Contains("count=3/3", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[OPEN_CYCLE][TRIGGERED]", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("JOINED", StringComparison.Ordinal));

        var stable = Assert.Single(logger.Messages.Where(message =>
            message.Contains("[GAP_STABILITY][CYCLE_STABLE]", StringComparison.Ordinal)));
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
        Assert.Contains("next_status=Stable", stable);
        Assert.Contains("result=STABLE", stable);
        Assert.Contains("gaps=\"100|110|120\"", stable);
        Assert.Contains("gaps_truncated=false", stable);
        Assert.Contains("total_sample_count=3", stable);
        Assert.Contains("logged_sample_count=3", stable);
        Assert.Contains("started_at=2026-08-25T00:00:00.0000000Z", stable);
        Assert.Contains("ended_at=2026-08-25T00:00:02.0000000Z", stable);
        Assert.Contains("config_id=CONFIG-TEST", stable);
        Assert.Contains("symbol=XAUUSD|XAUUSD", stable);
        Assert.Contains("absolute_floor=10", stable);
        Assert.Contains("relative_tolerance=0.5", stable);
        Assert.Contains("mad_multiplier=4", stable);
        Assert.Contains("min_stable_samples=3", stable);
        Assert.Contains("max_dispersion=0.45", stable);
        Assert.Contains("max_drift=0.6", stable);
        Assert.Contains("hold_confirm_ms=2000", stable);
        Assert.Contains("confirmation_mode=TIME_AND_MIN_SAMPLES", stable);
        Assert.Contains("signal_cycle_size=3", stable);
        Assert.Contains("hold_confirm_ignored=false", stable);
        Assert.Contains("limit_max_gap=0", stable);
        Assert.Contains("max_gap=700", stable);
        Assert.Contains("reason=\"", stable);

        var cycleId = ReadValue(stable, "cycle_id");
        Assert.Equal(32, cycleId.Length);
        Assert.All(cycleId, character => Assert.True(Uri.IsHexDigit(character)));

        var trigger = Assert.Single(logger.Messages.Where(message =>
            message.Contains("[GAP_STABILITY][TRIGGER_EMITTED]", StringComparison.Ordinal)));
        var signalId = ReadValue(trigger, "signal_id");
        Assert.Equal(cycleId, ReadValue(trigger, "cycle_id"));
        Assert.Equal(cycleId, emitted.DiagnosticCycleId);
        Assert.Equal(signalId, emitted.DiagnosticSignalId);
        Assert.Equal(32, signalId.Length);
        Assert.All(signalId, character => Assert.True(Uri.IsHexDigit(character)));
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

        Assert.Contains(logger.Messages, message =>
            message.Contains("[OPEN_CYCLE][STARTED]", StringComparison.Ordinal));
        Assert.Equal(2, logger.Messages.Count(message =>
            message.Contains("[OPEN_CYCLE][RESET]", StringComparison.Ordinal)));

        var split = Assert.Single(logger.RawMessages.Where(message =>
            message.Contains("[CYCLE_COMPLETED]", StringComparison.Ordinal)
            && message.Contains("result=DELTA_SPLIT", StringComparison.Ordinal)));
        Assert.Contains("new_gap=300", split);
        Assert.Contains("Delta", split);
        Assert.Contains("action=OPEN side=BUY gap_type=GAP_BUY", split);
        Assert.Contains("gaps_order=oldest_to_newest gaps_unit=point", split);

        var rejected = Assert.Single(logger.RawMessages.Where(message =>
            message.Contains("[CYCLE_COMPLETED]", StringComparison.Ordinal)
            && message.Contains("result=REJECTED", StringComparison.Ordinal)));
        Assert.Contains("new_gap=550", rejected);
        Assert.Contains("limit_max_gap 500", rejected);
        Assert.Contains("status_before=Collecting", rejected);
        Assert.Contains("next_status=Rejected", rejected);
    }

    [Fact]
    public void Open_DoesNotLogShortReset_ButLogsSignificantCompletedCycle()
    {
        var shortLogger = new CaptureLogger();
        var shortEngine = new GapSignalConfirmationEngine(shortLogger);
        var config = OpenConfig(open: 10_000);

        shortEngine.ProcessSnapshot(Snapshot(0, gapBuy: 100), config);
        shortEngine.ProcessSnapshot(Snapshot(0, gapBuy: 0), config);

        Assert.Contains(shortLogger.Messages, message =>
            message.Contains("[OPEN_CYCLE][STARTED]", StringComparison.Ordinal));
        Assert.Contains(shortLogger.Messages, message =>
            message.Contains("[OPEN_CYCLE][RESET]", StringComparison.Ordinal)
            && message.Contains("count=1/3", StringComparison.Ordinal));
        Assert.Empty(shortLogger.RawMessages);

        var significantLogger = new CaptureLogger();
        var significantEngine = new GapSignalConfirmationEngine(significantLogger);
        significantEngine.ProcessSnapshot(Snapshot(0, gapBuy: 100), config);
        significantEngine.ProcessSnapshot(Snapshot(1, gapBuy: 110), config);
        significantEngine.ProcessSnapshot(Snapshot(2, gapBuy: 120), config);
        significantEngine.ProcessSnapshot(Snapshot(3, gapBuy: 0), config);

        Assert.DoesNotContain(significantLogger.Messages, message =>
            message.Contains("[CYCLE_COMPLETED]", StringComparison.Ordinal));
        Assert.Contains(significantLogger.Messages, message =>
            message.Contains("[CYCLE_STABLE]", StringComparison.Ordinal));
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
            message.Contains("[CYCLE_STABLE]", StringComparison.Ordinal)
            && message.Contains("action=CLOSE side=BUY slot_id=7", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[TRIGGER_EMITTED]", StringComparison.Ordinal)
            && message.Contains("action=CLOSE side=BUY slot_id=7", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[NORMAL_CLOSE_CYCLE][STARTED]", StringComparison.Ordinal)
            && message.Contains("count=1/3", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[NORMAL_CLOSE_CYCLE][PROGRESS]", StringComparison.Ordinal)
            && message.Contains("count=2/3", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[NORMAL_CLOSE_CYCLE][COMPLETED]", StringComparison.Ordinal)
            && message.Contains("count=3/3", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[NORMAL_CLOSE_CYCLE][TRIGGERED]", StringComparison.Ordinal)
            && message.Contains("slot_id=7", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_RepeatedSnapshot_CountsEverySampleInTimeMode()
    {
        // Nhánh TIME không có khử trùng lặp theo fingerprint: mọi snapshot hợp lệ đều
        // là một mẫu của Cycle. Chu kỳ chỉ chốt khi đủ CẢ MinStableSamples lẫn hold-time.
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);
        var config = OpenConfig();
        var first = Snapshot(0, gapBuy: 100);

        engine.ProcessSnapshot(first, config);
        engine.ProcessSnapshot(first, config);
        engine.ProcessSnapshot(Snapshot(1, gapBuy: 110), config);
        engine.ProcessSnapshot(Snapshot(2, gapBuy: 120), config);

        Assert.Single(logger.Messages.Where(message =>
            message.Contains("[OPEN_CYCLE][STARTED]", StringComparison.Ordinal)));
        Assert.Single(logger.Messages.Where(message =>
            message.Contains("[OPEN_CYCLE][PROGRESS]", StringComparison.Ordinal)
            && message.Contains("count=2/3", StringComparison.Ordinal)));
        // Mẫu thứ 3 tới ở t=1s, chưa đủ hold 2000ms -> vẫn là PROGRESS.
        Assert.Single(logger.Messages.Where(message =>
            message.Contains("[OPEN_CYCLE][PROGRESS]", StringComparison.Ordinal)
            && message.Contains("count=3/3", StringComparison.Ordinal)));
        Assert.Single(logger.Messages.Where(message =>
            message.Contains("[OPEN_CYCLE][COMPLETED]", StringComparison.Ordinal)
            && message.Contains("count=4/3", StringComparison.Ordinal)));
    }

    [Fact]
    public void Tp_LogsTimeBasedCycleStartAndCompletion()
    {
        var logger = new CaptureLogger();
        var engine = new CloseSignalEngine(logger);
        _ = new PositionSlot(7, "PAIR-7", engine);
        var config = CloseConfig() with
        {
            CloseConfirmTpProfit = 5,
            CloseTpProfit = 10
        };

        Assert.Null(engine.ProcessSnapshot(Snapshot(0), config, TradingOpenMode.GapBuy, 5));
        Assert.Null(engine.ProcessSnapshot(Snapshot(1), config, TradingOpenMode.GapBuy, 7));
        var trigger = engine.ProcessSnapshot(Snapshot(2), config, TradingOpenMode.GapBuy, 10);

        Assert.NotNull(trigger);
        Assert.Contains(logger.Messages, message =>
            message.Contains("[TP_CYCLE][STARTED]", StringComparison.Ordinal)
            && message.Contains("slot_id=7", StringComparison.Ordinal)
            && message.Contains("count=1", StringComparison.Ordinal)
            && message.Contains("hold=0/2000ms", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[TP_CYCLE][COMPLETED]", StringComparison.Ordinal)
            && message.Contains("slot_id=7", StringComparison.Ordinal)
            && message.Contains("count=3", StringComparison.Ordinal)
            && message.Contains("hold=2000/2000ms", StringComparison.Ordinal)
            && message.Contains("confirmation_mode=TIME_AND_MIN_SAMPLES", StringComparison.Ordinal)
            && message.Contains("result=TARGET_REACHED", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[TP_CYCLE][PROGRESS]", StringComparison.Ordinal)
            && message.Contains("count=2", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[TP_CYCLE][TRIGGERED]", StringComparison.Ordinal)
            && message.Contains("result=TRIGGERED", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_LongCycle_LogsOnlyLastTwoHundredFiftySixGapsAndMarksTruncation()
    {
        var logger = new CaptureLogger();
        var engine = new GapSignalConfirmationEngine(logger);
        var config = OpenConfig(open: 10_000, signalCycleSize: 3_000);

        for (var index = 0; index < 2_001; index++)
        {
            engine.ProcessSnapshot(
                Snapshot(index, gapBuy: 100 + (index % 2)),
                config);
        }

        engine.Reset();

        var completed = Assert.Single(logger.RawMessages.Where(message =>
            message.Contains("result=RESET_EXPLICIT", StringComparison.Ordinal)));
        Assert.Contains("gaps_truncated=true", completed);
        Assert.Contains("total_sample_count=2001", completed);
        Assert.Contains("logged_sample_count=256", completed);
        Assert.Contains("gaps=\"101|100|101", completed);
    }

    [Fact]
    public void TwoCloseSlots_UseIndependentCycleAndSignalIds()
    {
        var logger = new CaptureLogger();
        var first = new CloseSignalEngine(logger);
        var second = new CloseSignalEngine(logger);
        _ = new PositionSlot(7, "PAIR-7", first);
        _ = new PositionSlot(8, "PAIR-8", second);
        var config = CloseConfig();

        GapSignalTriggerResult? firstTrigger = null;
        GapSignalTriggerResult? secondTrigger = null;
        foreach (var (tick, gap) in new[] { (0, -100), (1, -110), (2, -120) })
        {
            var snapshot = Snapshot(tick, gapSell: gap);
            firstTrigger = first.ProcessSnapshot(snapshot, config, TradingOpenMode.GapBuy);
            secondTrigger = second.ProcessSnapshot(snapshot, config, TradingOpenMode.GapBuy);
        }

        Assert.NotNull(firstTrigger);
        Assert.NotNull(secondTrigger);
        Assert.NotEqual(firstTrigger!.DiagnosticCycleId, secondTrigger!.DiagnosticCycleId);
        Assert.NotEqual(firstTrigger.DiagnosticSignalId, secondTrigger.DiagnosticSignalId);
        Assert.Contains(logger.Messages, message =>
            message.Contains("[TRIGGER_EMITTED]", StringComparison.Ordinal)
            && message.Contains("slot_id=7", StringComparison.Ordinal)
            && message.Contains($"cycle_id={firstTrigger.DiagnosticCycleId}", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("[TRIGGER_EMITTED]", StringComparison.Ordinal)
            && message.Contains("slot_id=8", StringComparison.Ordinal)
            && message.Contains($"cycle_id={secondTrigger.DiagnosticCycleId}", StringComparison.Ordinal));
    }

    private static GapSignalConfirmationConfig OpenConfig(
        int open = 100,
        int limitMaxGap = 0,
        int signalCycleSize = 3) =>
        new(
            ConfirmGapPts: 50,
            OpenPts: open,
            HoldConfirmMs: 2000,
            LimitMaxGap: limitMaxGap,
            OpenGapStability: Stability,
            DiagnosticConfigId: "CONFIG-TEST",
            DiagnosticSymbol: "XAUUSD|XAUUSD",
            DiagnosticMaxGap: 700,
            SignalCycleSize: signalCycleSize);

    private static GapSignalConfirmationConfig CloseConfig() =>
        new(
            ConfirmGapPts: 0,
            OpenPts: 0,
            HoldConfirmMs: 0,
            CloseConfirmGapPts: 50,
            ClosePts: 100,
            CloseHoldConfirmMs: 2000,
            CloseGapStability: Stability,
            DiagnosticConfigId: "CONFIG-TEST",
            DiagnosticSymbol: "XAUUSD|XAUUSD",
            DiagnosticMaxGap: 700,
            SignalCycleSize: 3);

    private static string ReadValue(string message, string key)
    {
        var prefix = $"{key}=";
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += prefix.Length;
        var end = message.IndexOf(' ', start);
        return end < 0 ? message[start..] : message[start..end];
    }

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

    private sealed class CaptureLogger : ISlotLogger, IGapStabilityRawLogger
    {
        public List<string> Messages { get; } = [];
        public List<string> RawMessages { get; } = [];

        public void Log(string message) => Messages.Add(message);

        public void LogGapStabilityRaw(string message) => RawMessages.Add(message);
    }
}
