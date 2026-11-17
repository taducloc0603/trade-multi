using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.HoldConfirm;

/// <summary>
/// Lõi hold_confirm_mode=ticks trong <see cref="GapCycleState.Process"/>: Stable khi đủ MinStableSamples
/// VÀ đủ N tick của chân chậm hơn (min A, B); mẫu đầu Cycle không đếm; trần 200 mẫu khi chờ tick.
/// </summary>
public sealed class HoldConfirmTicksTests
{
    private static readonly DateTime Start = new(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc);

    // MinStableSamples = 3; tolerance rộng để các gap 100..110 luôn cùng Cycle.
    private static readonly GapStabilityConfig Stability = new(10, 0.50, 4.0, 3, 0.45, 0.60);

    private static GapCycleUpdateResult Ticks(
        GapCycleState state,
        int index,
        int gap,
        int holdTicks,
        bool tickA = true,
        bool tickB = true,
        bool confirm = true) =>
        state.Process(
            Start.AddMilliseconds(index * 50),
            gap,
            hasRequiredData: true,
            confirmSatisfied: confirm,
            Stability,
            holdConfirmMs: 60_000,
            limitMaxGap: 0,
            HoldConfirmMode.Ticks,
            holdTicks,
            tickA,
            tickB);

    [Fact]
    public void FirstSample_IsNotCounted()
    {
        var state = new GapCycleState();

        var started = Ticks(state, 0, 100, holdTicks: 1);

        Assert.Equal(GapCycleTransition.Started, started.Transition);
        Assert.Equal(0, started.CurrentCycle.TickCount);
    }

    [Fact]
    public void EnoughSamplesButSlowLegNotTicked_StaysCollecting()
    {
        var state = new GapCycleState();

        Ticks(state, 0, 100, holdTicks: 2);
        Ticks(state, 1, 101, holdTicks: 2, tickA: true, tickB: false);
        Ticks(state, 2, 102, holdTicks: 2, tickA: true, tickB: false);
        var update = Ticks(state, 3, 103, holdTicks: 2, tickA: true, tickB: true);

        Assert.Equal(GapCycleStatus.Collecting, update.CurrentCycle.Status);
        Assert.Equal(1, update.CurrentCycle.TickCount);
        Assert.Equal("Chưa đủ Hold Confirm: 1/2 tick.", update.CurrentCycle.Reason);

        var stable = Ticks(state, 4, 104, holdTicks: 2, tickA: false, tickB: true);

        Assert.Equal(GapCycleTransition.BecameStable, stable.Transition);
        Assert.Equal(2, stable.CurrentCycle.TickCount);
    }

    [Fact]
    public void OnlyFastLegTicks_SlowLegCountStaysZero()
    {
        var state = new GapCycleState();

        Ticks(state, 0, 100, holdTicks: 1);
        GapCycleUpdateResult last = default!;
        for (var i = 1; i <= 10; i++)
        {
            last = Ticks(state, i, 100 + (i % 3), holdTicks: 1, tickA: true, tickB: false);
        }

        Assert.Equal(GapCycleStatus.Collecting, last.CurrentCycle.Status);
        Assert.Equal(0, last.CurrentCycle.TickCount);
    }

    [Fact]
    public void FrozenPrices_AreSamplesButNotTicks()
    {
        var state = new GapCycleState();

        Ticks(state, 0, 100, holdTicks: 1);
        Ticks(state, 1, 100, holdTicks: 1, tickA: false, tickB: false);
        var update = Ticks(state, 2, 100, holdTicks: 1, tickA: false, tickB: false);

        Assert.Equal(3, update.CurrentCycle.SampleCount);
        Assert.Equal(0, update.CurrentCycle.TickCount);
        Assert.Equal(GapCycleStatus.Collecting, update.CurrentCycle.Status);
    }

    [Fact]
    public void HoldMsIsIgnoredInTicksMode()
    {
        var state = new GapCycleState();

        // holdConfirmMs = 60 s nhưng chỉ 150 ms trôi qua: mode ticks vẫn Stable khi đủ tick.
        Ticks(state, 0, 100, holdTicks: 2);
        Ticks(state, 1, 101, holdTicks: 2);
        var stable = Ticks(state, 2, 102, holdTicks: 2);

        Assert.Equal(GapCycleTransition.BecameStable, stable.Transition);
        Assert.Equal(100d, stable.CurrentCycle.DurationMs);
    }

    [Fact]
    public void ZeroTicks_OnlyMinStableSamplesApplies()
    {
        var state = new GapCycleState();

        Ticks(state, 0, 100, holdTicks: 0, tickA: false, tickB: false);
        Ticks(state, 1, 101, holdTicks: 0, tickA: false, tickB: false);
        var stable = Ticks(state, 2, 102, holdTicks: 0, tickA: false, tickB: false);

        Assert.Equal(GapCycleTransition.BecameStable, stable.Transition);
    }

    [Fact]
    public void NewCycleFromDelta_ResetsTickCount()
    {
        var state = new GapCycleState();

        Ticks(state, 0, 100, holdTicks: 5);
        Ticks(state, 1, 101, holdTicks: 5);
        Ticks(state, 2, 102, holdTicks: 5);
        var split = Ticks(state, 3, 400, holdTicks: 5);

        Assert.Equal(GapCycleTransition.NewCycle, split.Transition);
        Assert.Equal(2, split.CompletedCycle!.TickCount);
        Assert.Equal(0, split.CurrentCycle.TickCount);
    }

    [Fact]
    public void ConfirmReset_ResetsTickCount()
    {
        var state = new GapCycleState();

        Ticks(state, 0, 100, holdTicks: 5);
        Ticks(state, 1, 101, holdTicks: 5);
        Ticks(state, 2, 50, holdTicks: 5, confirm: false);
        Ticks(state, 3, 100, holdTicks: 5);
        var update = Ticks(state, 4, 101, holdTicks: 5);

        Assert.Equal(1, update.CurrentCycle.TickCount);
    }

    [Fact]
    public void ExplicitReset_ResetsTickCount()
    {
        var state = new GapCycleState();

        Ticks(state, 0, 100, holdTicks: 5);
        Ticks(state, 1, 101, holdTicks: 5);
        state.Reset("test");
        Ticks(state, 2, 100, holdTicks: 5);
        var update = Ticks(state, 3, 101, holdTicks: 5);

        Assert.Equal(1, update.CurrentCycle.TickCount);
    }

    [Fact]
    public void UnstableRestart_ResetsTickCount()
    {
        var state = new GapCycleState();

        // 100, 150, 50: MAD 50 / center 100 = 0.5 > 0.45 -> Unstable, Cycle mới bắt đầu từ 50.
        Ticks(state, 0, 100, holdTicks: 0);
        Ticks(state, 1, 150, holdTicks: 0);
        var unstable = Ticks(state, 2, 50, holdTicks: 0);

        Assert.Equal(GapCycleTransition.BecameUnstable, unstable.Transition);
        Assert.Equal(0, unstable.CurrentCycle.TickCount);
    }

    [Fact]
    public void WaitingForTicks_HitsSampleCap_StartsNewCycle()
    {
        var state = new GapCycleState();

        Ticks(state, 0, 100, holdTicks: 1);
        GapCycleUpdateResult update = default!;
        for (var i = 1; i < GapCycleState.TicksModeMaxCollectingSamples - 1; i++)
        {
            update = Ticks(state, i, 100, holdTicks: 1, tickA: true, tickB: false);
            Assert.Equal(GapCycleTransition.Joined, update.Transition);
        }

        Assert.Equal(GapCycleState.TicksModeMaxCollectingSamples - 1, update.CurrentCycle.SampleCount);

        var capped = Ticks(state, GapCycleState.TicksModeMaxCollectingSamples, 100, holdTicks: 1, tickA: true, tickB: false);

        Assert.Equal(GapCycleTransition.NewCycle, capped.Transition);
        Assert.Equal(GapCycleState.TicksModeMaxCollectingSamples, capped.CompletedCycle!.SampleCount);
        Assert.Equal(1, capped.CurrentCycle.SampleCount);
        Assert.Equal(0, capped.CurrentCycle.TickCount);
        Assert.Contains("Chân chậm chưa đủ tick (0/1) sau 200 mẫu", capped.CurrentCycle.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleCap_DoesNotApplyOnceStable()
    {
        var state = new GapCycleState();

        Ticks(state, 0, 100, holdTicks: 1);
        GapCycleUpdateResult update = default!;
        for (var i = 1; i <= 500; i++)
        {
            update = Ticks(state, i, 100, holdTicks: 1);
        }

        Assert.Equal(GapCycleStatus.Stable, update.CurrentCycle.Status);
        Assert.Equal(501, update.CurrentCycle.SampleCount);
    }

    [Fact]
    public void SampleCap_DoesNotApplyInMsMode()
    {
        var state = new GapCycleState();
        GapCycleUpdateResult update = default!;

        // holdConfirmMs 60 s, mẫu cách 50 ms: 500 mẫu (~25 s) vẫn chưa đủ hold -> Collecting, KHÔNG reset.
        for (var i = 0; i <= 500; i++)
        {
            update = state.Process(
                Start.AddMilliseconds(i * 50), 100, true, true, Stability, 60_000, 0,
                HoldConfirmMode.Ms, holdConfirmTicks: 1, isNewTickA: true, isNewTickB: false);
        }

        Assert.Equal(GapCycleTransition.Joined, update.Transition);
        Assert.Equal(501, update.CurrentCycle.SampleCount);
        Assert.Equal(GapCycleStatus.Collecting, update.CurrentCycle.Status);
    }

    [Fact]
    public void MsMode_IgnoresTickFlags()
    {
        var withTicks = new GapCycleState();
        var withoutTicks = new GapCycleState();

        for (var i = 0; i < 30; i++)
        {
            var gap = 100 + (i % 4);
            var a = withTicks.Process(Start.AddMilliseconds(i * 50), gap, true, true, Stability, 500, 0,
                HoldConfirmMode.Ms, 3, isNewTickA: true, isNewTickB: true);
            var b = withoutTicks.Process(Start.AddMilliseconds(i * 50), gap, true, true, Stability, 500, 0,
                HoldConfirmMode.Ms, 3, isNewTickA: false, isNewTickB: false);

            Assert.Equal(a.Transition, b.Transition);
            Assert.Equal(a.CurrentCycle.Status, b.CurrentCycle.Status);
            Assert.Equal(a.CurrentCycle.Reason, b.CurrentCycle.Reason);
        }
    }

    [Fact]
    public void NegativeTicks_Throws()
    {
        var state = new GapCycleState();

        Assert.Throws<ArgumentOutOfRangeException>(() => Ticks(state, 0, 100, holdTicks: -1));
    }
}
