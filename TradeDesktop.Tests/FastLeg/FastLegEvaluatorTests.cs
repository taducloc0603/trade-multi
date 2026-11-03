using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.FastLeg;

namespace TradeDesktop.Tests.FastLeg;

public sealed class FastLegEvaluatorTests
{
    private const int Point = 100;
    private const long Now = 100_000;

    // Thông số Long: filter, max_move 5, window 200, max_age 1000, fail_open tắt, apply_to_exit bật.
    private static FastLegConfig Config(
        FastLegMode mode = FastLegMode.Filter,
        bool failOpen = false,
        bool applyToExit = true) => new(
            Enabled: true,
            MapName: @"Local\MT_C_Tick",
            HeartbeatMapName: @"Local\MT_C_Trades",
            Symbol: "XAUUSD",
            Mode: mode,
            MaxMovePts: 5m,
            WindowMs: 200,
            MaxAgeMs: 1000,
            MaxDeferMs: 3000,
            FailOpen: failOpen,
            ApplyToExit: applyToExit);

    private static FastLegTickSample Tick(long ts, decimal mid) => new(ts, mid - 0.08m, mid + 0.08m);

    private static FastLegBufferSnapshot Snapshot(
        FastLegTickSample? reference,
        FastLegTickSample? last,
        FastLegHeartbeat? heartbeat = null,
        string? downReason = null) => new(reference, last, heartbeat, downReason, 0);

    private static FastLegVerdict Evaluate(in FastLegBufferSnapshot snapshot, FastLegConfig? config = null, int point = Point)
        => FastLegEvaluator.Evaluate(in snapshot, Now, config ?? Config(), point);

    [Fact]
    public void QuietWindow_IsConfirmed()
    {
        var snapshot = Snapshot(Tick(Now - 250, 4000.00m), Tick(Now - 20, 4000.02m), new FastLegHeartbeat(Now, true));

        var verdict = Evaluate(in snapshot);

        Assert.Equal(FastLegVerdictKind.Confirmed, verdict.Kind);
        Assert.Equal(2m, verdict.MovePts);
        Assert.Equal(230, verdict.SpanMs);
        Assert.Equal(20, verdict.AgeMs);
        Assert.Equal(FastLegHeartbeatState.Alive, verdict.HeartbeatState);
        Assert.False(verdict.BlocksOpen);
        Assert.False(verdict.BlocksClose);
    }

    [Theory]
    [InlineData(0.05, FastLegVerdictKind.TooFast)]   // đúng biên: |move| == max_move là TooFast.
    [InlineData(-0.05, FastLegVerdictKind.TooFast)]  // đối xứng hai chiều.
    [InlineData(0.04, FastLegVerdictKind.Confirmed)]
    [InlineData(-0.04, FastLegVerdictKind.Confirmed)]
    public void MoveBoundary_IsSymmetricAndInclusive(double priceMove, FastLegVerdictKind expected)
    {
        var snapshot = Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4000m + (decimal)priceMove));

        Assert.Equal(expected, Evaluate(in snapshot).Kind);
    }

    [Fact]
    public void Age_EqualToMax_IsNotStale_ButAboveIs()
    {
        var atMax = Snapshot(Tick(Now - 1000, 4000m), Tick(Now - 1000, 4000m));
        var aboveMax = Snapshot(Tick(Now - 1001, 4000m), Tick(Now - 1001, 4000m));

        Assert.Equal(FastLegVerdictKind.Confirmed, Evaluate(in atMax).Kind);
        Assert.Equal(FastLegVerdictKind.Stale, Evaluate(in aboveMax).Kind);
    }

    [Fact]
    public void NoTickYet_IsStale()
    {
        var snapshot = Snapshot(null, null);

        var verdict = Evaluate(in snapshot);

        Assert.Equal(FastLegVerdictKind.Stale, verdict.Kind);
        Assert.Null(verdict.AgeMs);
        Assert.Null(verdict.CBid);
    }

    [Fact]
    public void StaleWins_OverTooFast()
    {
        // Tick cuối đã quá tuổi thì biên độ đo được không còn ý nghĩa thời sự.
        var snapshot = Snapshot(Tick(Now - 3000, 4000m), Tick(Now - 2000, 4010m));

        Assert.Equal(FastLegVerdictKind.Stale, Evaluate(in snapshot).Kind);
    }

    [Fact]
    public void SingleSample_HasZeroMoveAndZeroVelocity()
    {
        var tick = Tick(Now - 10, 4000m);
        var snapshot = Snapshot(tick, tick);

        var verdict = Evaluate(in snapshot);

        Assert.Equal(FastLegVerdictKind.Confirmed, verdict.Kind);
        Assert.Equal(0m, verdict.MovePts);
        Assert.Equal(0, verdict.SpanMs);
        Assert.Equal(0m, verdict.VelocityPtsPerSec);
    }

    [Fact]
    public void SameQuantumSamples_DoNotDivideByZero()
    {
        // Hai tick trong cùng nhịp GetTickCount64 (~15.6 ms) có span = 0.
        var snapshot = Snapshot(Tick(Now - 10, 4000m), Tick(Now - 10, 4000.20m));

        var verdict = Evaluate(in snapshot);

        Assert.Equal(FastLegVerdictKind.TooFast, verdict.Kind);
        Assert.Equal(0m, verdict.VelocityPtsPerSec);
    }

    [Fact]
    public void Velocity_IsPointsPerSecond()
    {
        var snapshot = Snapshot(Tick(Now - 200, 4000m), Tick(Now, 4000.02m));

        Assert.Equal(10m, Evaluate(in snapshot).VelocityPtsPerSec);
    }

    [Fact]
    public void ClockAhead_AgeClampsAtZero()
    {
        var snapshot = Snapshot(Tick(Now + 50, 4000m), Tick(Now + 50, 4000m));

        Assert.Equal(0, Evaluate(in snapshot).AgeMs);
    }

    [Fact]
    public void DownReasonFromSampler_WinsOverEverything()
    {
        var snapshot = Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4010m), new FastLegHeartbeat(Now, true), "map_not_found");

        var verdict = Evaluate(in snapshot);

        Assert.Equal(FastLegVerdictKind.Down, verdict.Kind);
        Assert.Equal("map_not_found", verdict.DownReason);
        Assert.Equal(0m, verdict.MovePts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void BadPoint_IsDown(int point)
    {
        var snapshot = Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4000m));

        var verdict = Evaluate(in snapshot, point: point);

        Assert.Equal(FastLegVerdictKind.Down, verdict.Kind);
        Assert.Equal("bad_point", verdict.DownReason);
    }

    [Fact]
    public void DisabledConfig_IsDown_AndNeverBlocks()
    {
        var snapshot = Snapshot(null, null);

        var verdict = Evaluate(in snapshot, FastLegConfig.Disabled);

        Assert.Equal(FastLegVerdictKind.Down, verdict.Kind);
        Assert.Equal("disabled", verdict.DownReason);
        Assert.False(verdict.BlocksOpen);
        Assert.False(verdict.BlocksClose);
    }

    [Fact]
    public void HeartbeatTooOld_IsDown_EvenWithFreshTick()
    {
        var snapshot = Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4000m), new FastLegHeartbeat(Now - FastLegEvaluator.HeartbeatMaxAgeMs - 1, true));

        var verdict = Evaluate(in snapshot);

        Assert.Equal(FastLegVerdictKind.Down, verdict.Kind);
        Assert.Equal("heartbeat_stale", verdict.DownReason);
        Assert.Equal(FastLegHeartbeatState.Dead, verdict.HeartbeatState);
    }

    [Fact]
    public void HeartbeatAtExactLimit_IsAlive()
    {
        var snapshot = Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4000m), new FastLegHeartbeat(Now - FastLegEvaluator.HeartbeatMaxAgeMs, true));

        Assert.Equal(FastLegHeartbeatState.Alive, Evaluate(in snapshot).HeartbeatState);
    }

    [Fact]
    public void TerminalDisconnected_IsDown()
    {
        var snapshot = Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4000m), new FastLegHeartbeat(Now, false));

        var verdict = Evaluate(in snapshot);

        Assert.Equal(FastLegVerdictKind.Down, verdict.Kind);
        Assert.Equal("terminal_disconnected", verdict.DownReason);
    }

    [Fact]
    public void LiveHeartbeat_WithOldTick_IsStaleNotDown()
    {
        // D9: cuối tuần / thị trường rất yên. Terminal sống, không có tick.
        var snapshot = Snapshot(Tick(Now - 60_000, 4000m), Tick(Now - 60_000, 4000m), new FastLegHeartbeat(Now, true));

        var verdict = Evaluate(in snapshot);

        Assert.Equal(FastLegVerdictKind.Stale, verdict.Kind);
        Assert.Equal(FastLegHeartbeatState.Alive, verdict.HeartbeatState);
        Assert.Null(verdict.DownReason);
    }

    [Fact]
    public void NoHeartbeatMap_DoesNotCauseDown()
    {
        var snapshot = Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4000m), heartbeat: null);

        var verdict = Evaluate(in snapshot);

        Assert.Equal(FastLegVerdictKind.Confirmed, verdict.Kind);
        Assert.Equal(FastLegHeartbeatState.NotAvailable, verdict.HeartbeatState);
    }

    // Ma trận chính sách. kind = verdict tạo ra bởi snapshot tương ứng.
    [Theory]
    // mode, failOpen, applyToExit, kind, blocksOpen, blocksClose
    [InlineData(FastLegMode.Filter, false, true, FastLegVerdictKind.Confirmed, false, false)]
    [InlineData(FastLegMode.Filter, false, true, FastLegVerdictKind.TooFast, true, true)]
    [InlineData(FastLegMode.Filter, false, true, FastLegVerdictKind.Stale, true, false)]  // D6
    [InlineData(FastLegMode.Filter, false, true, FastLegVerdictKind.Down, true, false)]   // D6
    [InlineData(FastLegMode.Filter, true, true, FastLegVerdictKind.TooFast, true, true)]  // fail-open không tha TooFast
    [InlineData(FastLegMode.Filter, true, true, FastLegVerdictKind.Stale, false, false)]
    [InlineData(FastLegMode.Filter, true, true, FastLegVerdictKind.Down, false, false)]
    [InlineData(FastLegMode.Filter, false, false, FastLegVerdictKind.TooFast, true, false)]
    [InlineData(FastLegMode.Filter, false, false, FastLegVerdictKind.Stale, true, false)]
    [InlineData(FastLegMode.Observe, false, true, FastLegVerdictKind.TooFast, false, false)]
    [InlineData(FastLegMode.Observe, false, true, FastLegVerdictKind.Stale, false, false)]
    [InlineData(FastLegMode.Observe, false, true, FastLegVerdictKind.Down, false, false)]
    public void PolicyMatrix(
        FastLegMode mode,
        bool failOpen,
        bool applyToExit,
        FastLegVerdictKind kind,
        bool expectedBlocksOpen,
        bool expectedBlocksClose)
    {
        var snapshot = kind switch
        {
            FastLegVerdictKind.Confirmed => Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4000m)),
            FastLegVerdictKind.TooFast => Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4001m)),
            FastLegVerdictKind.Stale => Snapshot(null, null),
            _ => Snapshot(null, null, downReason: "map_not_found")
        };

        var verdict = Evaluate(in snapshot, Config(mode, failOpen, applyToExit));

        Assert.Equal(kind, verdict.Kind);
        Assert.Equal(expectedBlocksOpen, verdict.BlocksOpen);
        Assert.Equal(expectedBlocksClose, verdict.BlocksClose);
        Assert.Equal(mode, verdict.Mode);
    }

    [Fact]
    public void LogFields_AreStableAndInvariant()
    {
        var snapshot = Snapshot(Tick(Now - 200, 4000m), Tick(Now - 10, 4000.5m), new FastLegHeartbeat(Now, true));

        var fields = Evaluate(in snapshot).ToLogFields();

        Assert.Contains("verdict=TooFast", fields);
        Assert.Contains("move_pts=50 ", fields);
        Assert.Contains("span_ms=190", fields);
        Assert.Contains("age_ms=10", fields);
        Assert.Contains("c_bid=4000.42", fields);
        Assert.Contains("c_ask=4000.58", fields);
        Assert.Contains("hb=alive", fields);
        Assert.EndsWith("down=-", fields);
    }
}
