using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class GapCycleStateTests
{
    private static readonly GapStabilityConfig Config =
        new(10, 0.50, 4.0, 3, 0.35, 0.40);

    private static readonly DateTime Start =
        new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstValidSample_StartsCollectingCycle()
    {
        var state = new GapCycleState();

        var result = Process(state, 100, 0);

        Assert.Equal(GapCycleTransition.Started, result.Transition);
        Assert.Equal(GapCycleStatus.Collecting, result.CurrentCycle.Status);
        Assert.Equal([100], result.CurrentCycle.Gaps);
        Assert.Equal(100d, result.CurrentCycle.Center);
        Assert.Equal(0d, result.CurrentCycle.DurationMs);
        Assert.Null(result.CompletedCycle);
    }

    [Fact]
    public void EnoughSamplesAndHold_BecomesStable()
    {
        var state = new GapCycleState();
        Process(state, 100, 0, holdMs: 1000);
        Process(state, 105, 500, holdMs: 1000);

        var result = Process(state, 102, 1000, holdMs: 1000);

        Assert.Equal(GapCycleTransition.BecameStable, result.Transition);
        Assert.Equal(GapCycleStatus.Stable, result.CurrentCycle.Status);
        Assert.Equal(3, result.CurrentCycle.SampleCount);
        Assert.Equal(1000d, result.CurrentCycle.DurationMs);
    }

    [Fact]
    public void EnoughSamplesButNotHold_RemainsCollecting()
    {
        var state = new GapCycleState();
        Process(state, 100, 0, holdMs: 2000);
        Process(state, 105, 500, holdMs: 2000);

        var result = Process(state, 102, 1000, holdMs: 2000);

        Assert.Equal(GapCycleTransition.Joined, result.Transition);
        Assert.Equal(GapCycleStatus.Collecting, result.CurrentCycle.Status);
        Assert.Contains("Hold Confirm", result.CurrentCycle.Reason);
    }

    [Fact]
    public void DeltaAboveTolerance_CompletesOldAndStartsNewCycle()
    {
        var state = new GapCycleState();
        Process(state, 2, 0, holdMs: 10000);
        Process(state, 4, 100, holdMs: 10000);
        Process(state, 3, 200, holdMs: 10000);

        var result = Process(state, 20, 300, holdMs: 10000);

        Assert.Equal(GapCycleTransition.NewCycle, result.Transition);
        Assert.Equal([2, 4, 3], result.CompletedCycle!.Gaps);
        Assert.Equal([20], result.CurrentCycle.Gaps);
        Assert.Equal(GapCycleStatus.Collecting, result.CurrentCycle.Status);
        Assert.Contains("Delta", result.CurrentCycle.Reason);
    }

    [Fact]
    public void DriftAboveLimit_CompletesUnstableAndRestartsFromLastSample()
    {
        var state = new GapCycleState();
        var gaps = new[] { 20, 30, 40, 50, 60, 70 };
        GapCycleUpdateResult? result = null;
        for (var index = 0; index < gaps.Length; index++)
        {
            result = Process(state, gaps[index], index * 1000, holdMs: 5000);
        }

        Assert.NotNull(result);
        Assert.Equal(GapCycleTransition.BecameUnstable, result.Transition);
        Assert.Equal(GapCycleStatus.Unstable, result.CompletedCycle!.Status);
        Assert.Equal(gaps, result.CompletedCycle.Gaps);
        Assert.Equal(2d / 3d, result.CompletedCycle.Drift!.Value, precision: 12);
        Assert.Contains("Drift", result.CompletedCycle.Reason);
        Assert.Equal([70], result.CurrentCycle.Gaps);
        Assert.Equal(GapCycleStatus.Collecting, result.CurrentCycle.Status);
    }

    [Fact]
    public void DispersionAboveLimit_CompletesUnstable()
    {
        var state = new GapCycleState();
        var dispersionConfig = Config with
        {
            MadMultiplier = 10,
            MaxDispersion = 0.10,
            MaxDrift = 10
        };
        Process(state, 100, 0, dispersionConfig, holdMs: 1000);
        Process(state, 150, 500, dispersionConfig, holdMs: 1000);

        var result = Process(state, 200, 1000, dispersionConfig, holdMs: 1000);

        Assert.Equal(GapCycleTransition.BecameUnstable, result.Transition);
        Assert.Equal(GapCycleStatus.Unstable, result.CompletedCycle!.Status);
        Assert.True(result.CompletedCycle.Dispersion > dispersionConfig.MaxDispersion);
        Assert.Contains("Dispersion", result.CompletedCycle.Reason);
        Assert.Equal([200], result.CurrentCycle.Gaps);
    }

    [Fact]
    public void GapAboveEnabledLimit_IsRejectedAndNotUsedAsNewCenter()
    {
        var state = new GapCycleState();
        Process(state, 100, 0);

        var result = Process(state, 550, 100, limitMaxGap: 500);

        Assert.Equal(GapCycleTransition.Rejected, result.Transition);
        Assert.Equal(GapCycleStatus.Rejected, result.CurrentCycle.Status);
        Assert.Empty(result.CurrentCycle.Gaps);
        Assert.Null(result.CurrentCycle.Center);
        Assert.Equal([100], result.CompletedCycle!.Gaps);
    }

    [Fact]
    public void ZeroLimit_DisablesAbsoluteGapRejection()
    {
        var state = new GapCycleState();

        var result = Process(state, 550, 0, limitMaxGap: 0);

        Assert.Equal(GapCycleTransition.Started, result.Transition);
        Assert.Equal([550], result.CurrentCycle.Gaps);
    }

    [Fact]
    public void MissingData_ResetsWithoutAddingZero()
    {
        var state = new GapCycleState();
        Process(state, 100, 0);

        var result = state.Process(
            Start.AddMilliseconds(100),
            gap: null,
            hasRequiredData: false,
            confirmSatisfied: false,
            Config,
            holdConfirmMs: 1000);

        Assert.Equal(GapCycleTransition.ResetMissingData, result.Transition);
        Assert.Equal(GapCycleStatus.Empty, result.CurrentCycle.Status);
        Assert.Empty(result.CurrentCycle.Gaps);
        Assert.Equal([100], result.CompletedCycle!.Gaps);
    }

    [Fact]
    public void ConfirmNotSatisfied_ResetsCycle()
    {
        var state = new GapCycleState();
        Process(state, 100, 0);

        var result = state.Process(
            Start.AddMilliseconds(100),
            gap: 90,
            hasRequiredData: true,
            confirmSatisfied: false,
            Config,
            holdConfirmMs: 1000);

        Assert.Equal(GapCycleTransition.ResetConfirmNotSatisfied, result.Transition);
        Assert.Equal(GapCycleStatus.Empty, result.CurrentCycle.Status);
        Assert.Empty(result.CurrentCycle.Gaps);
    }

    [Fact]
    public void BackwardTimestamp_StartsNewCycle()
    {
        var state = new GapCycleState();
        Process(state, 100, 1000);

        var result = Process(state, 105, 500);

        Assert.Equal(GapCycleTransition.ResetTimestamp, result.Transition);
        Assert.Equal([100], result.CompletedCycle!.Gaps);
        Assert.Equal([105], result.CurrentCycle.Gaps);
        Assert.Equal(Start.AddMilliseconds(500), result.CurrentCycle.StartedAtUtc);
    }

    [Fact]
    public void ExplicitReset_ClearsCycleAndPreservesCompletedSnapshot()
    {
        var state = new GapCycleState();
        Process(state, -100, 0);

        var result = state.Reset("Engine reset.");

        Assert.Equal(GapCycleTransition.ResetExplicitly, result.Transition);
        Assert.Equal([-100], result.CompletedCycle!.Gaps);
        Assert.Empty(result.CurrentCycle.Gaps);
        Assert.Equal("Engine reset.", result.CurrentCycle.Reason);
    }

    [Fact]
    public void CurrentSnapshot_IsDetachedFromFutureMutations()
    {
        var state = new GapCycleState();
        Process(state, 100, 0, holdMs: 10000);
        var snapshot = state.Current;

        Process(state, 105, 100, holdMs: 10000);

        Assert.Equal([100], snapshot.Gaps);
        Assert.Equal([100, 105], state.Current.Gaps);
    }

    private static GapCycleUpdateResult Process(
        GapCycleState state,
        int gap,
        int elapsedMs,
        GapStabilityConfig? config = null,
        int holdMs = 1000,
        int limitMaxGap = 0) =>
        state.Process(
            Start.AddMilliseconds(elapsedMs),
            gap,
            hasRequiredData: true,
            confirmSatisfied: true,
            config ?? Config,
            holdMs,
            limitMaxGap);
}
