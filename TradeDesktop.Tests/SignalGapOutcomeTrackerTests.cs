using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class SignalGapOutcomeTrackerTests
{
    private static readonly DateTime Start =
        new(2026, 8, 31, 3, 15, 32, DateTimeKind.Utc);

    // ---------- Pha attach ----------

    [Fact]
    public void OnSignalPending_WritesNothingUntilAttach()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger);

        tracker.OnSignalPending(OpenSignal());
        tracker.OnTick(Tick(1, gapBuy: 41));

        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void AttachStt_WritesSignalLine()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");

        var line = Assert.Single(logger.Lines);
        Assert.Contains("[SIGNAL_OUTCOME][SIGNAL][EXEC]", line, StringComparison.Ordinal);
        Assert.Contains("stt=7", line, StringComparison.Ordinal);
        Assert.Contains("pair_id=AUTO-0003-8412553", line, StringComparison.Ordinal);
        Assert.Contains("gap_at_signal=40", line, StringComparison.Ordinal);
        Assert.Contains("signal_gaps=\"38|39|40\"", line, StringComparison.Ordinal);
        Assert.Contains("track_gap=BUY", line, StringComparison.Ordinal);
        Assert.Contains("action=OPEN side=BUY", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachStt_FillsSlotIdKnownOnlyAtDispatch()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 2);

        // OPEN: lúc signal chưa có slot; slot chỉ được cấp trong luồng dispatch.
        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553", slotId: 5);
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));

        Assert.All(logger.Lines, line => Assert.Contains("slot_id=5", line, StringComparison.Ordinal));
    }

    [Fact]
    public void AttachStt_WithoutSlotId_KeepsSignalSlotId()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 1);

        // CLOSE: slot đã có sẵn từ lúc mở trace, không cần override.
        tracker.OnSignalPending(NormalCloseSignal());
        tracker.AttachStt("sig-1", 4, "AUTO-0001-8399102");
        tracker.OnTick(Tick(1, gapSell: -21));

        Assert.All(logger.Lines, line => Assert.Contains("slot_id=3", line, StringComparison.Ordinal));
    }

    [Fact]
    public void AttachStt_WithUnknownStt_WritesDashAndKeepsPairId()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 1);

        // UI chưa cấp số cho pair này: đường log KHÔNG được tự cấp số mới,
        // nên ghi stt=- và dò ngược bằng pair_id.
        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", null, "AUTO-0003-8412553");
        tracker.OnTick(Tick(1, gapBuy: 41));

        Assert.Equal(2, logger.Lines.Count);
        Assert.All(logger.Lines, line =>
        {
            Assert.Contains("stt=-", line, StringComparison.Ordinal);
            Assert.Contains("pair_id=AUTO-0003-8412553", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AttachGrace_IsWiderThanObservationWindow()
    {
        var logger = new CaptureLogger();
        var tracker = new SignalGapOutcomeTracker(logger, futureTickCount: 3);

        // Dispatch chậm (chờ physical mutex, click native) vẫn phải ghi được:
        // grace mặc định phải rộng hơn nhiều so với cửa sổ 50 tick.
        tracker.OnSignalPending(OpenSignal());
        for (var i = 1; i <= 120; i++)
        {
            tracker.OnTick(Tick(i, gapBuy: 40 + (i % 3)));
        }

        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");

        Assert.Equal(2, logger.Lines.Count);
        Assert.Contains("captured=3/3", logger.Lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Trace không dispatch cũng không báo chặn => ghi UNRESOLVED làm chỉ báo coverage,
    /// thay vì bỏ im lặng (sẽ che mất gate chưa hook).
    /// </summary>
    [Fact]
    public void UnattachedTrace_AfterGrace_EmitsBlockedUnresolved()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3, attachGraceTicks: 4);

        tracker.OnSignalPending(OpenSignal());
        for (var i = 1; i <= 10; i++)
        {
            tracker.OnTick(Tick(i, gapBuy: 40 + i));
        }

        Assert.Equal(2, logger.Lines.Count);
        Assert.Contains("[SIGNAL_OUTCOME][SIGNAL][BLOCKED]", logger.Lines[0], StringComparison.Ordinal);
        Assert.Contains("[SIGNAL_OUTCOME][END][BLOCKED]", logger.Lines[1], StringComparison.Ordinal);
        Assert.All(logger.Lines, line =>
            Assert.Contains("block_reason=UNRESOLVED", line, StringComparison.Ordinal));
        Assert.Contains("captured=3/3", logger.Lines[1], StringComparison.Ordinal);

        // Trace đã đóng: attach muộn cũng không sinh dòng nào.
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        Assert.Equal(2, logger.Lines.Count);
    }

    [Fact]
    public void AttachStt_IsIdempotent()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.AttachStt("sig-1", 9, "AUTO-9999-1");

        var line = Assert.Single(logger.Lines);
        Assert.Contains("stt=7", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachAfterWindowFilled_WritesBothLines()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3, attachGraceTicks: 10);

        tracker.OnSignalPending(OpenSignal());
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));
        tracker.OnTick(Tick(3, gapBuy: 43));
        Assert.Empty(logger.Lines);

        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");

        Assert.Equal(2, logger.Lines.Count);
        Assert.Contains("[SIGNAL_OUTCOME][SIGNAL][EXEC]", logger.Lines[0], StringComparison.Ordinal);
        Assert.Contains("[SIGNAL_OUTCOME][END][EXEC]", logger.Lines[1], StringComparison.Ordinal);
        Assert.Contains("captured=3/3", logger.Lines[1], StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"41|42|43\"", logger.Lines[1], StringComparison.Ordinal);
    }

    // ---------- Cửa sổ quan sát ----------

    [Fact]
    public void ExactlyWindowTicks_EmitsSingleEndLine()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");

        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));
        Assert.Single(logger.Lines);

        tracker.OnTick(Tick(3, gapBuy: 43));
        Assert.Equal(2, logger.Lines.Count);

        // Tick sau khi trace đóng không sinh thêm dòng nào.
        tracker.OnTick(Tick(4, gapBuy: 44));
        Assert.Equal(2, logger.Lines.Count);
    }

    [Fact]
    public void TickAtSignalTime_IsNotCounted()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3);

        // gap tại signal = 40; tick đầu tiên feed vào là tick SAU signal.
        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));
        tracker.OnTick(Tick(3, gapBuy: 43));

        var end = logger.Lines[1];
        Assert.Contains("gap_at_signal=40", end, StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"41|42|43\"", end, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstantGap_StillRecordsEveryTick()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.OnTick(Tick(1, gapBuy: 40));
        tracker.OnTick(Tick(2, gapBuy: 40));
        tracker.OnTick(Tick(3, gapBuy: 40));

        Assert.Contains("captured=3/3", logger.Lines[1], StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"40|40|40\"", logger.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NullGapTick_IsSkipped()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: null));
        tracker.OnTick(Tick(3, gapBuy: 42));
        tracker.OnTick(Tick(4, gapBuy: 43));

        var end = logger.Lines[1];
        Assert.Contains("captured=3/3", end, StringComparison.Ordinal);
        Assert.Contains("skipped_null_ticks=1", end, StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"41|42|43\"", end, StringComparison.Ordinal);
    }

    [Fact]
    public void EndLine_RepeatsSignalGapsBeforeFutureGaps()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 2);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));

        var end = logger.Lines[1];
        var signalGapsIndex = end.IndexOf("signal_gaps=\"38|39|40\"", StringComparison.Ordinal);
        var futureGapsIndex = end.IndexOf("future_gaps=\"41|42\"", StringComparison.Ordinal);
        Assert.True(signalGapsIndex >= 0);
        Assert.True(futureGapsIndex > signalGapsIndex);
    }

    [Fact]
    public void EndLine_ReportsElapsedMs()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));
        tracker.OnTick(Tick(3, gapBuy: 43));

        // Tick cách nhau 50ms: tick1 -> tick3 là 100ms.
        Assert.Contains("elapsed_ms=100", logger.Lines[1], StringComparison.Ordinal);
    }

    // ---------- STT giữa OPEN và CLOSE ----------

    [Fact]
    public void SameStt_ForOpenAndCloseOfSamePair()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 2);
        const string PairId = "AUTO-0003-8412553";

        tracker.OnSignalPending(OpenSignal(signalId: "sig-open"));
        tracker.AttachStt("sig-open", 7, PairId);
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));

        tracker.OnSignalPending(NormalCloseSignal(signalId: "sig-close"));
        tracker.AttachStt("sig-close", 7, PairId);
        tracker.OnTick(Tick(3, gapSell: -21));
        tracker.OnTick(Tick(4, gapSell: -22));

        Assert.Equal(4, logger.Lines.Count);
        Assert.All(logger.Lines, line =>
        {
            Assert.Contains("stt=7", line, StringComparison.Ordinal);
            Assert.Contains($"pair_id={PairId}", line, StringComparison.Ordinal);
        });
        Assert.Contains("action=OPEN", logger.Lines[0], StringComparison.Ordinal);
        Assert.Contains("action=CLOSE", logger.Lines[2], StringComparison.Ordinal);
        Assert.Contains("slot_id=3", logger.Lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Stt_MatchesBetweenSignalAndEnd_WhenTracesOverlap()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3);

        tracker.OnSignalPending(OpenSignal(signalId: "sig-a"));
        tracker.AttachStt("sig-a", 1, "AUTO-0001-1");
        tracker.OnTick(Tick(1, gapBuy: 41));

        tracker.OnSignalPending(OpenSignal(signalId: "sig-b"));
        tracker.AttachStt("sig-b", 2, "AUTO-0002-2");

        tracker.OnTick(Tick(2, gapBuy: 42));
        tracker.OnTick(Tick(3, gapBuy: 43)); // sig-a đủ 3 tick -> END
        tracker.OnTick(Tick(4, gapBuy: 44)); // sig-b đủ 3 tick -> END

        var endA = Assert.Single(logger.Lines.Where(l =>
            l.Contains("[SIGNAL_OUTCOME][END][EXEC]", StringComparison.Ordinal)
            && l.Contains("signal_id=sig-a", StringComparison.Ordinal)));
        var endB = Assert.Single(logger.Lines.Where(l =>
            l.Contains("[SIGNAL_OUTCOME][END][EXEC]", StringComparison.Ordinal)
            && l.Contains("signal_id=sig-b", StringComparison.Ordinal)));

        Assert.Contains("stt=1", endA, StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"41|42|43\"", endA, StringComparison.Ordinal);
        Assert.Contains("stt=2", endB, StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"42|43|44\"", endB, StringComparison.Ordinal);
    }

    // ---------- Vòng đời / an toàn ----------

    [Fact]
    public void OverlappingSignals_TrackedIndependently()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 2);

        for (var i = 1; i <= 3; i++)
        {
            tracker.OnSignalPending(OpenSignal(signalId: $"sig-{i}"));
            tracker.AttachStt($"sig-{i}", i, $"AUTO-000{i}-{i}");
            tracker.OnTick(Tick(i, gapBuy: 40 + i));
        }

        tracker.OnTick(Tick(4, gapBuy: 44));
        tracker.OnTick(Tick(5, gapBuy: 45));

        var ends = logger.Lines
            .Where(l => l.Contains("[SIGNAL_OUTCOME][END][EXEC]", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, ends.Count);
        Assert.All(ends, line => Assert.Contains("captured=2/2", line, StringComparison.Ordinal));
    }

    [Fact]
    public void MaxConcurrentTraces_EvictsOldest()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50, maxConcurrentTraces: 2);

        tracker.OnSignalPending(OpenSignal(signalId: "sig-1"));
        tracker.AttachStt("sig-1", 1, "AUTO-0001-1");
        tracker.OnSignalPending(OpenSignal(signalId: "sig-2"));
        tracker.AttachStt("sig-2", 2, "AUTO-0002-2");
        tracker.OnSignalPending(OpenSignal(signalId: "sig-3"));

        var evicted = Assert.Single(logger.Lines.Where(l =>
            l.Contains("status=EVICTED", StringComparison.Ordinal)));
        Assert.Contains("signal_id=sig-1", evicted, StringComparison.Ordinal);
        Assert.Contains("captured=0/50", evicted, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushAll_EmitsPartialEnd()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));

        tracker.FlushAll("SESSION_STOP");

        var end = logger.Lines[1];
        Assert.Contains("status=SESSION_STOP", end, StringComparison.Ordinal);
        Assert.Contains("captured=2/50", end, StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"41|42\"", end, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushAll_SkipsTracesNotYetAttached()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal());
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.FlushAll("SESSION_STOP");

        Assert.Empty(logger.Lines);
    }

    // ---------- Signal bị chặn ----------

    [Fact]
    public void MarkBlocked_WritesSignalLineWithBlockedTagAndReason()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger);

        tracker.OnSignalPending(OpenSignal());
        tracker.MarkBlocked("sig-1", "POST_CLOSE_LOCK");

        var line = Assert.Single(logger.Lines);
        Assert.Contains("[SIGNAL_OUTCOME][SIGNAL][BLOCKED]", line, StringComparison.Ordinal);
        Assert.Contains("block_reason=POST_CLOSE_LOCK", line, StringComparison.Ordinal);
        Assert.Contains("stt=-", line, StringComparison.Ordinal);
        Assert.Contains("signal_gaps=\"38|39|40\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkBlocked_ThenTicks_WritesEndBlockedWithFullFutureGaps()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3);

        tracker.OnSignalPending(OpenSignal());
        tracker.MarkBlocked("sig-1", "POST_CLOSE_LOCK");
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));
        tracker.OnTick(Tick(3, gapBuy: 43));

        Assert.Equal(2, logger.Lines.Count);
        Assert.Contains("[SIGNAL_OUTCOME][END][BLOCKED]", logger.Lines[1], StringComparison.Ordinal);
        Assert.Contains("block_reason=POST_CLOSE_LOCK", logger.Lines[1], StringComparison.Ordinal);
        Assert.Contains("captured=3/3", logger.Lines[1], StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"41|42|43\"", logger.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void MarkBlocked_SameReasonStreak_OnlyFirstLogged()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        for (var i = 1; i <= 5; i++)
        {
            tracker.OnSignalPending(OpenSignal(signalId: $"sig-{i}"));
            tracker.MarkBlocked($"sig-{i}", "POST_CLOSE_LOCK");
        }

        var signalLines = logger.Lines
            .Where(l => l.Contains("[SIGNAL_OUTCOME][SIGNAL][BLOCKED]", StringComparison.Ordinal))
            .ToList();
        Assert.Single(signalLines);
        Assert.Contains("signal_id=sig-1", signalLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void BlockStreak_ClosedByIdle_EmitsSummaryWithCounts()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50, blockStreakIdleMs: 1);

        for (var i = 1; i <= 5; i++)
        {
            tracker.OnSignalPending(OpenSignal(signalId: $"sig-{i}"));
            tracker.MarkBlocked($"sig-{i}", "POST_CLOSE_LOCK");
        }

        Thread.Sleep(20);
        tracker.OnTick(Tick(1, gapBuy: 41));

        var summary = Assert.Single(logger.Lines.Where(l =>
            l.Contains("[SIGNAL_OUTCOME][BLOCK_STREAK]", StringComparison.Ordinal)));
        Assert.Contains("block_reason=POST_CLOSE_LOCK", summary, StringComparison.Ordinal);
        Assert.Contains("blocked_count=5", summary, StringComparison.Ordinal);
        Assert.Contains("suppressed_count=4", summary, StringComparison.Ordinal);
        Assert.Contains("logged_signal_id=sig-1", summary, StringComparison.Ordinal);
        Assert.Contains("last_signal_id=sig-5", summary, StringComparison.Ordinal);
        Assert.Contains("closed_by=IDLE", summary, StringComparison.Ordinal);
        Assert.Contains("action=OPEN side=BUY", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockStreak_ClosedByExecOnSameActionSide()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        // Cần >= 2 signal bị chặn thì mới có dòng tổng kết (1 signal thì 2 dòng chi tiết là đủ).
        tracker.OnSignalPending(OpenSignal(signalId: "sig-blocked-1"));
        tracker.MarkBlocked("sig-blocked-1", "POST_CLOSE_LOCK");
        tracker.OnSignalPending(OpenSignal(signalId: "sig-blocked-2"));
        tracker.MarkBlocked("sig-blocked-2", "POST_CLOSE_LOCK");

        tracker.OnSignalPending(OpenSignal(signalId: "sig-exec"));
        tracker.AttachStt("sig-exec", 7, "AUTO-0003-8412553");

        var summary = Assert.Single(logger.Lines.Where(l =>
            l.Contains("[SIGNAL_OUTCOME][BLOCK_STREAK]", StringComparison.Ordinal)));
        Assert.Contains("closed_by=EXEC", summary, StringComparison.Ordinal);
        Assert.Contains("blocked_count=2", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockStreak_ExecOnDifferentSide_DoesNotCloseStreak()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal(signalId: "sig-blocked"));
        tracker.MarkBlocked("sig-blocked", "POST_CLOSE_LOCK");

        // Close vị thế Buy => Action khác OPEN, không đóng streak của OPEN/BUY.
        tracker.OnSignalPending(NormalCloseSignal(signalId: "sig-exec"));
        tracker.AttachStt("sig-exec", 4, "AUTO-0001-8399102");

        Assert.DoesNotContain(logger.Lines, l =>
            l.Contains("[SIGNAL_OUTCOME][BLOCK_STREAK]", StringComparison.Ordinal));
    }

    [Fact]
    public void BlockStreak_DifferentReason_OpensSecondStreak()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal(signalId: "sig-1"));
        tracker.MarkBlocked("sig-1", "POST_CLOSE_LOCK");
        tracker.OnSignalPending(OpenSignal(signalId: "sig-2"));
        tracker.MarkBlocked("sig-2", "QUOTA_BUY_FULL");

        var signalLines = logger.Lines
            .Where(l => l.Contains("[SIGNAL_OUTCOME][SIGNAL][BLOCKED]", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, signalLines.Count);
        Assert.Contains("block_reason=POST_CLOSE_LOCK", signalLines[0], StringComparison.Ordinal);
        Assert.Contains("block_reason=QUOTA_BUY_FULL", signalLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void MarkBlocked_AfterAttachStt_IsIgnored()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.MarkBlocked("sig-1", "POST_CLOSE_LOCK");

        var line = Assert.Single(logger.Lines);
        Assert.Contains("[SIGNAL_OUTCOME][SIGNAL][EXEC]", line, StringComparison.Ordinal);
        Assert.DoesNotContain("block_reason=", line, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkBlocked_UnknownSignalId_IsNoOp()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.MarkBlocked("khong-ton-tai", "POST_CLOSE_LOCK");

        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void MarkBlocked_Twice_SameSignal_IsIdempotent()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal());
        tracker.MarkBlocked("sig-1", "POST_CLOSE_LOCK");
        tracker.MarkBlocked("sig-1", "QUOTA_BUY_FULL");

        var line = Assert.Single(logger.Lines);
        Assert.Contains("block_reason=POST_CLOSE_LOCK", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushAll_EmitsPendingStreakSummaries()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal(signalId: "sig-1"));
        tracker.MarkBlocked("sig-1", "POST_CLOSE_LOCK");
        tracker.OnSignalPending(OpenSignal(signalId: "sig-2"));
        tracker.MarkBlocked("sig-2", "POST_CLOSE_LOCK");

        tracker.FlushAll("SESSION_STOP");

        var summary = Assert.Single(logger.Lines.Where(l =>
            l.Contains("[SIGNAL_OUTCOME][BLOCK_STREAK]", StringComparison.Ordinal)));
        Assert.Contains("closed_by=FLUSH", summary, StringComparison.Ordinal);
        Assert.Contains("blocked_count=2", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_ClearsStreaksWithoutEmitting()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal());
        tracker.MarkBlocked("sig-1", "POST_CLOSE_LOCK");
        logger.Lines.Clear();

        tracker.Reset();
        tracker.OnTick(Tick(1, gapBuy: 41));

        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void StreakCap_EvictsOldest_EmitsEvictedSummary()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50, maxConcurrentStreaks: 2);

        // REASON_A cần 2 signal thì dòng tổng kết lúc bị evict mới được ghi.
        tracker.OnSignalPending(OpenSignal(signalId: "sig-1a"));
        tracker.MarkBlocked("sig-1a", "REASON_A");
        tracker.OnSignalPending(OpenSignal(signalId: "sig-1b"));
        tracker.MarkBlocked("sig-1b", "REASON_A");
        tracker.OnSignalPending(OpenSignal(signalId: "sig-2"));
        tracker.MarkBlocked("sig-2", "REASON_B");
        tracker.OnSignalPending(OpenSignal(signalId: "sig-3"));
        tracker.MarkBlocked("sig-3", "REASON_C");

        var evicted = Assert.Single(logger.Lines.Where(l =>
            l.Contains("closed_by=EVICTED", StringComparison.Ordinal)));
        Assert.Contains("block_reason=REASON_A", evicted, StringComparison.Ordinal);
    }

    // ---------- Regression: stale không được nuốt trace chưa gắn nhãn ----------

    /// <summary>
    /// attachGraceTicks(600) x PollInterval(50ms) = đúng staleTraceMs(30_000), nên trước đây hai
    /// ngưỡng tranh nhau và STALE thường thắng — gỡ trace chưa gắn nhãn mà không ghi gì, khiến
    /// UNRESOLVED thành code chết. STALE giờ chỉ áp cho trace đã gắn nhãn.
    /// </summary>
    [Fact]
    public void UnattachedTrace_IsNotRemovedByStale()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 2, staleTraceMs: 1, attachGraceTicks: 3);

        tracker.OnSignalPending(OpenSignal());
        Thread.Sleep(20);

        // Các tick này đều đã quá staleTraceMs nhưng trace chưa gắn nhãn -> không được gỡ im lặng.
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));
        Assert.Empty(logger.Lines);

        tracker.OnTick(Tick(3, gapBuy: 43));
        tracker.OnTick(Tick(4, gapBuy: 44));

        Assert.All(logger.Lines, line =>
            Assert.Contains("block_reason=UNRESOLVED", line, StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l =>
            l.Contains("[SIGNAL_OUTCOME][END][BLOCKED]", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l =>
            l.Contains("status=STALE", StringComparison.Ordinal));
    }

    [Fact]
    public void AttachedTrace_StillTimesOutAsStale()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50, staleTraceMs: 1);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        Thread.Sleep(20);
        tracker.OnTick(Tick(1, gapBuy: 41));

        var end = Assert.Single(logger.Lines.Where(l =>
            l.Contains("[SIGNAL_OUTCOME][END]", StringComparison.Ordinal)));
        Assert.Contains("status=STALE", end, StringComparison.Ordinal);
    }

    [Fact]
    public void GraceBranch_StillCollectsGapOfSameTick()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 3, attachGraceTicks: 1);

        tracker.OnSignalPending(OpenSignal());
        tracker.OnTick(Tick(1, gapBuy: 41));   // tick 1: chưa quá grace
        tracker.OnTick(Tick(2, gapBuy: 42));   // tick 2: quá grace -> ghi SIGNAL, VẪN thu gap này
        tracker.OnTick(Tick(3, gapBuy: 43));

        var end = Assert.Single(logger.Lines.Where(l =>
            l.Contains("[SIGNAL_OUTCOME][END]", StringComparison.Ordinal)));
        Assert.Contains("captured=3/3", end, StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"41|42|43\"", end, StringComparison.Ordinal);
    }

    // ---------- Regression: đầu vào bất thường ----------

    [Fact]
    public void OnSignalPending_WithEmptySignalId_IsIgnored()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 1, attachGraceTicks: 1);

        tracker.OnSignalPending(OpenSignal() with { SignalId = "" });
        tracker.OnTick(Tick(1, gapBuy: 41));
        tracker.OnTick(Tick(2, gapBuy: 42));

        // Không mở trace => không có UNRESOLVED giả, không chiếm chỗ trong danh sách.
        Assert.Empty(logger.Lines);
    }

    [Theory]
    [InlineData("QUOTA_BUY_FULL (3/3) description=\"day\"", "QUOTA_BUY_FULL")]
    [InlineData(" (quota) 3/3", "UNKNOWN")]
    [InlineData("  POST_CLOSE_LOCK  ", "POST_CLOSE_LOCK")]
    [InlineData("   ", "UNKNOWN")]
    public void BlockReason_IsNormalizedToSingleToken(string raw, string expected)
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal());
        tracker.MarkBlocked("sig-1", raw);

        var line = Assert.Single(logger.Lines);
        Assert.Contains($"block_reason={expected} ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleBlockedSignal_DoesNotEmitStreakSummary()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 1, blockStreakIdleMs: 1);

        tracker.OnSignalPending(OpenSignal());
        tracker.MarkBlocked("sig-1", "LATENCY_GUARD");
        tracker.OnTick(Tick(1, gapBuy: 41));

        Thread.Sleep(20);
        tracker.OnTick(Tick(2, gapBuy: 42));

        Assert.Equal(2, logger.Lines.Count);
        Assert.DoesNotContain(logger.Lines, l =>
            l.Contains("[SIGNAL_OUTCOME][BLOCK_STREAK]", StringComparison.Ordinal));
    }

    [Fact]
    public void Reset_DropsOpenTracesWithoutEmitting()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.OnTick(Tick(1, gapBuy: 41));
        logger.Lines.Clear();

        tracker.Reset();
        tracker.OnTick(Tick(2, gapBuy: 42));

        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void StaleTrace_TimesOut()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 50, staleTraceMs: 0 + 1);

        tracker.OnSignalPending(OpenSignal());
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        Thread.Sleep(20);
        tracker.OnTick(Tick(1, gapBuy: 41));

        var end = Assert.Single(logger.Lines.Where(l =>
            l.Contains("[SIGNAL_OUTCOME][END][EXEC]", StringComparison.Ordinal)));
        Assert.Contains("status=STALE", end, StringComparison.Ordinal);
    }

    [Fact]
    public void NullBaselineGap_DoesNotOpenTrace()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger);

        tracker.OnSignalPending(OpenSignal() with { GapBuy = null });
        tracker.AttachStt("sig-1", 7, "AUTO-0003-8412553");
        tracker.OnTick(Tick(1, gapBuy: 41));

        Assert.Empty(logger.Lines);
    }

    /// <summary>
    /// Regression: close một vị thế BUY có PrimarySide=Buy nhưng được kích hoạt bởi gap Sell.
    /// Nếu chọn gap list/chiều theo PrimarySide thì signal_gaps sẽ RỖNG (BuyGaps rỗng) và
    /// future_gaps bám sai chiều gap. Phải khoá theo TriggerType.
    /// </summary>
    [Fact]
    public void TrackGap_FollowsTriggerType_NotPrimarySide()
    {
        var logger = new CaptureLogger();
        var tracker = NewTracker(logger, futureTickCount: 2);

        tracker.OnSignalPending(NormalCloseSignal());
        tracker.AttachStt("sig-1", 4, "AUTO-0001-8399102");
        tracker.OnTick(Tick(1, gapBuy: 41, gapSell: -21));
        tracker.OnTick(Tick(2, gapBuy: 42, gapSell: -22));

        // side là chiều VỊ THẾ (Buy), track_gap là chiều GAP kích hoạt close (Sell).
        Assert.Contains("side=BUY", logger.Lines[0], StringComparison.Ordinal);
        Assert.Contains("track_gap=SELL", logger.Lines[0], StringComparison.Ordinal);
        Assert.Contains("signal_gaps=\"-18|-2|-4|-6\"", logger.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("signal_gaps=\"\"", logger.Lines[0], StringComparison.Ordinal);

        Assert.Contains("gap_at_signal=-20", logger.Lines[1], StringComparison.Ordinal);
        Assert.Contains("future_gaps=\"-21|-22\"", logger.Lines[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GapSignalTriggerType.OpenByGapBuy, true)]
    [InlineData(GapSignalTriggerType.OpenByGapSell, false)]
    [InlineData(GapSignalTriggerType.CloseByGapBuy, true)]
    [InlineData(GapSignalTriggerType.CloseByGapSell, false)]
    public void TracksBuyGap_KeysOnTriggerType(GapSignalTriggerType triggerType, bool expected)
        => Assert.Equal(expected, SignalGapOutcomeTracker.TracksBuyGap(triggerType));

    // ---------- Phạm vi theo dõi ----------

    [Theory]
    [InlineData(GapSignalAction.Open, CloseSignalReason.Gap, CloseGapMode.Normal, true)]
    [InlineData(GapSignalAction.Open, CloseSignalReason.Tp, CloseGapMode.Sos, true)]
    [InlineData(GapSignalAction.Close, CloseSignalReason.Gap, CloseGapMode.Normal, true)]
    [InlineData(GapSignalAction.Close, CloseSignalReason.Gap, CloseGapMode.Sos, false)]
    [InlineData(GapSignalAction.Close, CloseSignalReason.Tp, CloseGapMode.Normal, false)]
    [InlineData(GapSignalAction.Close, CloseSignalReason.Tp, CloseGapMode.Sos, false)]
    public void ShouldTrack_CoversOpenAndNormalCloseOnly(
        GapSignalAction action,
        CloseSignalReason reason,
        CloseGapMode mode,
        bool expected)
        => Assert.Equal(expected, SignalGapOutcomeTracker.ShouldTrack(action, reason, mode));

    // ---------- Helpers ----------

    private static SignalGapOutcomeTracker NewTracker(
        CaptureLogger logger,
        int futureTickCount = 50,
        int maxConcurrentTraces = 16,
        int staleTraceMs = 30_000,
        int attachGraceTicks = 600,
        int blockStreakIdleMs = 3_000,
        int maxConcurrentStreaks = 16)
        => new(
            logger,
            futureTickCount,
            maxConcurrentTraces,
            staleTraceMs,
            attachGraceTicks,
            blockStreakIdleMs,
            maxConcurrentStreaks);

    private static SignalOutcomeTick Tick(int index, int? gapBuy = null, int? gapSell = null)
        => new(Start.AddMilliseconds(index * 50), gapBuy, gapSell);

    private static SignalOutcomeSignal OpenSignal(string signalId = "sig-1")
        => new(
            SignalId: signalId,
            CycleId: "OPEN-BUY-000417",
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
            SignalGaps: new[] { 38, 39, 40 },
            ConfirmGapPts: 25,
            OpenPts: 30,
            CloseConfirmGapPts: 20,
            ClosePts: 15,
            LimitMaxGap: 120,
            MaxGap: 80,
            SignalCycleSize: 3);

    /// <summary>
    /// Close một vị thế BUY: engine đóng bằng gap Sell đảo chiều, nên
    /// <c>PrimarySide=Buy</c> (chiều vị thế) nhưng gaps nằm ở <c>SellGaps</c>
    /// và <c>BuyGaps</c> rỗng — đúng như CloseSignalEngine.cs:320-321.
    /// </summary>
    private static SignalOutcomeSignal NormalCloseSignal(string signalId = "sig-1")
        => OpenSignal(signalId) with
        {
            CycleId = "NORMAL_CLOSE-SELL-000512",
            SlotId = 3,
            Action = GapSignalAction.Close,
            Side = GapSignalSide.Buy,
            TriggerType = GapSignalTriggerType.CloseByGapSell,
            SignalGaps = new[] { -18, -2, -4, -6 }
        };

    private sealed class CaptureLogger : ISignalOutcomeRawLogger
    {
        public List<string> Lines { get; } = new();

        public void LogSignalOutcomeRaw(string message) => Lines.Add(message);
    }
}
