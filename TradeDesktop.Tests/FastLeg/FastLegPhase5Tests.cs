using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.FastLeg;

namespace TradeDesktop.Tests.FastLeg;

public sealed class FastLegPhase5Tests
{
    private static FastLegVerdict Verdict(FastLegVerdictKind kind, bool blocksOpen, string? downReason = null) => new(
        kind,
        MovePts: kind == FastLegVerdictKind.TooFast ? 9m : 0m,
        VelocityPtsPerSec: 0m,
        SpanMs: 200,
        AgeMs: 100,
        CBid: 3946.1m,
        CAsk: 3946.27m,
        FastLegMode.Filter,
        blocksOpen,
        BlocksClose: false,
        downReason,
        FastLegHeartbeatState.Alive);

    // ---------- FastLegOpenPolicy (router re-check) ----------

    [Fact]
    public void OpenPolicy_NullVerdict_Allows()
    {
        Assert.Equal((true, "ALLOWED"), FastLegOpenPolicy.Check(null));
    }

    [Theory]
    [InlineData(FastLegVerdictKind.TooFast, FastLegOpenPolicy.TooFastCode)]
    [InlineData(FastLegVerdictKind.Stale, FastLegOpenPolicy.StaleCode)]
    [InlineData(FastLegVerdictKind.Down, FastLegOpenPolicy.DownCode)]
    public void OpenPolicy_BlockingVerdict_ReturnsSpecificCode(FastLegVerdictKind kind, string code)
    {
        Assert.Equal((false, code), FastLegOpenPolicy.Check(Verdict(kind, blocksOpen: true)));
    }

    [Theory]
    [InlineData(FastLegVerdictKind.TooFast)]   // observe
    [InlineData(FastLegVerdictKind.Stale)]     // fail_open
    [InlineData(FastLegVerdictKind.Confirmed)]
    public void OpenPolicy_NotBlocking_Allows(FastLegVerdictKind kind)
    {
        Assert.True(FastLegOpenPolicy.Check(Verdict(kind, blocksOpen: false)).Allowed);
    }

    // ---------- FastLegHealthTracker ----------

    [Fact]
    public void Health_DownForThreshold_AlertsOnce_ThenRecovers()
    {
        var tracker = new FastLegHealthTracker(thresholdMs: 30_000);
        var down = Verdict(FastLegVerdictKind.Down, true, "heartbeat_stale");

        Assert.Equal(FastLegHealthEvent.None, tracker.Observe(down, 0));
        Assert.Equal(FastLegHealthEvent.None, tracker.Observe(down, 29_999));
        Assert.Equal(FastLegHealthEvent.Down, tracker.Observe(down, 30_000));
        Assert.Equal("heartbeat_stale", tracker.LastBadReason);
        // Không spam trong cùng một đợt hỏng.
        Assert.Equal(FastLegHealthEvent.None, tracker.Observe(down, 60_000));
        Assert.Equal(FastLegHealthEvent.None, tracker.Observe(down, 120_000));

        Assert.Equal(FastLegHealthEvent.Recovered, tracker.Observe(Verdict(FastLegVerdictKind.Confirmed, false), 121_000));
        Assert.Equal(FastLegHealthEvent.None, tracker.Observe(Verdict(FastLegVerdictKind.Confirmed, false), 122_000));
    }

    [Fact]
    public void Health_FlickeringStale_NeverAlerts()
    {
        // Feed thưa: Stale chập chờn rồi lại có tick — mỗi lần có tick là đếm lại từ đầu.
        var tracker = new FastLegHealthTracker(thresholdMs: 30_000);
        for (var t = 0L; t < 300_000; t += 1_000)
        {
            var kind = (t / 1_000) % 10 < 7 ? FastLegVerdictKind.Stale : FastLegVerdictKind.Confirmed;
            Assert.Equal(FastLegHealthEvent.None, tracker.Observe(Verdict(kind, kind != FastLegVerdictKind.Confirmed), t));
        }
    }

    [Fact]
    public void Health_TooFastCountsAsHealthy()
    {
        var tracker = new FastLegHealthTracker(thresholdMs: 1_000);
        var stale = Verdict(FastLegVerdictKind.Stale, true);

        tracker.Observe(stale, 0);
        Assert.Equal(FastLegHealthEvent.Down, tracker.Observe(stale, 1_000));
        Assert.Equal(FastLegHealthEvent.Recovered, tracker.Observe(Verdict(FastLegVerdictKind.TooFast, true), 2_000));
    }

    [Fact]
    public void Health_DisabledFastLeg_ResetsWithoutEvents()
    {
        var tracker = new FastLegHealthTracker(thresholdMs: 1_000);
        var down = Verdict(FastLegVerdictKind.Down, true, "map_not_found");

        tracker.Observe(down, 0);
        Assert.Equal(FastLegHealthEvent.Down, tracker.Observe(down, 1_000));
        // Người dùng tắt sàn C: không phải sự cố ⇒ không phát Recovered, và đợt hỏng sau đếm lại từ đầu.
        Assert.Equal(FastLegHealthEvent.None, tracker.Observe(null, 2_000));
        Assert.Equal(FastLegHealthEvent.None, tracker.Observe(down, 3_000));
        Assert.Equal(FastLegHealthEvent.None, tracker.Observe(down, 3_999));
        Assert.Equal(FastLegHealthEvent.Down, tracker.Observe(down, 4_000));
    }
}
