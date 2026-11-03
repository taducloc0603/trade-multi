using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.FastLeg;

namespace TradeDesktop.Tests.FastLeg;

public sealed class FastLegStatsTests
{
    private static FastLegVerdict Verdict(
        FastLegVerdictKind kind,
        decimal move = 0m,
        long? age = 10,
        FastLegHeartbeatState heartbeat = FastLegHeartbeatState.Alive) => new(
            kind, move, 0m, 0, age, 1m, 2m, FastLegMode.Filter, false, false, null, heartbeat);

    [Fact]
    public void BeforeInterval_ReturnsNull()
    {
        var stats = new FastLegStats(intervalMs: 1000);

        Assert.Null(stats.Observe(Verdict(FastLegVerdictKind.Confirmed), 0, 0));
        Assert.Null(stats.Observe(Verdict(FastLegVerdictKind.Confirmed), 999, 5));
    }

    [Fact]
    public void AtInterval_ReportsPercentagesAndTickDelta()
    {
        var stats = new FastLegStats(intervalMs: 1000);
        stats.Observe(Verdict(FastLegVerdictKind.Confirmed, move: 1m, age: 10), 0, 100);
        stats.Observe(Verdict(FastLegVerdictKind.TooFast, move: -6m, age: 20), 100, 110);
        stats.Observe(Verdict(FastLegVerdictKind.Stale, move: 0m, age: 2000), 200, 110);

        var line = stats.Observe(Verdict(FastLegVerdictKind.Down, age: null), 1000, 130);

        Assert.NotNull(line);
        Assert.StartsWith("[FAST_LEG][STATS] ", line);
        Assert.Contains("ticks=30 ", line);
        Assert.Contains("samples=4 ", line);
        Assert.Contains("toofast_pct=25 ", line);
        Assert.Contains("stale_pct=25 ", line);
        Assert.Contains("stale_hb_alive_pct=25 ", line);
        Assert.Contains("down_pct=25", line);
        Assert.Contains("max_age=2000 ", line);
        Assert.Contains("p50_abs_move=1 ", line);
        Assert.Contains("p90_abs_move=6 ", line);
    }

    [Fact]
    public void Down_IsExcludedFromMoveAndAge()
    {
        var stats = new FastLegStats(intervalMs: 1000);
        stats.Observe(Verdict(FastLegVerdictKind.Down, move: 999m, age: 999_999), 0, 0);

        var line = stats.Observe(Verdict(FastLegVerdictKind.Down, move: 999m, age: 999_999), 1000, 0);

        Assert.Contains("p50_abs_move=0 ", line);
        Assert.Contains("max_age=0 ", line);
        Assert.Contains("down_pct=100", line);
    }

    [Fact]
    public void StaleWithoutLiveHeartbeat_IsNotCountedAsAlive()
    {
        var stats = new FastLegStats(intervalMs: 1000);
        stats.Observe(Verdict(FastLegVerdictKind.Stale, heartbeat: FastLegHeartbeatState.NotAvailable), 0, 0);

        var line = stats.Observe(Verdict(FastLegVerdictKind.Stale, heartbeat: FastLegHeartbeatState.NotAvailable), 1000, 0);

        Assert.Contains("stale_pct=100 ", line);
        Assert.Contains("stale_hb_alive_pct=0 ", line);
    }

    [Fact]
    public void Flush_StartsANewWindow()
    {
        var stats = new FastLegStats(intervalMs: 1000);
        stats.Observe(Verdict(FastLegVerdictKind.TooFast), 0, 0);
        Assert.NotNull(stats.Observe(Verdict(FastLegVerdictKind.TooFast), 1000, 0));

        Assert.Null(stats.Observe(Verdict(FastLegVerdictKind.Confirmed), 1001, 0));
        var line = stats.Observe(Verdict(FastLegVerdictKind.Confirmed), 2001, 0);

        Assert.Contains("samples=2 ", line);
        Assert.Contains("toofast_pct=0 ", line);
    }

    [Fact]
    public void Reset_DiscardsCurrentWindow()
    {
        var stats = new FastLegStats(intervalMs: 1000);
        stats.Observe(Verdict(FastLegVerdictKind.TooFast), 0, 0);

        stats.Reset();

        Assert.Null(stats.Observe(Verdict(FastLegVerdictKind.Confirmed), 5000, 0));
    }
}
