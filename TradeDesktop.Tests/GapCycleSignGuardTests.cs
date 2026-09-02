using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

/// <summary>
/// Guard "cùng dấu trong một Cycle". Stability đo trên |gap| nên một Cycle trộn hai dấu
/// (ví dụ -5, +5, -5) bị chấm là hoàn toàn ổn định. Với ngưỡng dương, confirm gate đã ép
/// Cycle về cùng dấu nên guard không bao giờ chạm; guard chỉ có tác dụng khi ngưỡng âm
/// cho phép Cycle chứa cả hai dấu.
/// </summary>
public sealed class GapCycleSignGuardTests
{
    private static readonly GapStabilityConfig Config =
        new(10, 0.50, 4.0, 3, 0.35, 0.40);

    private static readonly DateTime Start =
        new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FixedSizeCycle_SignFlip_StartsNewCycle()
    {
        var state = new GapCycleState();
        ProcessFixed(state, -5, 0);

        var result = ProcessFixed(state, 5, 50);

        Assert.Equal(GapCycleTransition.NewCycle, result.Transition);
        Assert.Equal([-5], result.CompletedCycle!.Gaps);
        Assert.Equal([5], result.CurrentCycle.Gaps);
        Assert.Contains("sign flipped", result.CurrentCycle.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FixedSizeCycle_OscillatingSigns_NeverBecomesStable()
    {
        var state = new GapCycleState();

        var gaps = new[] { -5, 5, -5, 5, -5, 5 };
        for (var i = 0; i < gaps.Length; i++)
        {
            var result = ProcessFixed(state, gaps[i], i * 50);

            Assert.NotEqual(GapCycleStatus.Stable, result.CurrentCycle.Status);
            Assert.Single(result.CurrentCycle.Gaps);
        }
    }

    [Fact]
    public void FixedSizeCycle_LeadingZeroIsNeutral()
    {
        var state = new GapCycleState();
        ProcessFixed(state, 0, 0);

        var result = ProcessFixed(state, -5, 50);

        Assert.NotEqual(GapCycleTransition.NewCycle, result.Transition);
        Assert.Equal([0, -5], result.CurrentCycle.Gaps);
    }

    [Fact]
    public void FixedSizeCycle_MiddleZeroIsNeutral_AndCycleCompletes()
    {
        var state = new GapCycleState();
        ProcessFixed(state, -5, 0);
        ProcessFixed(state, 0, 50);

        var result = ProcessFixed(state, -4, 100);

        Assert.Equal(GapCycleTransition.BecameStable, result.Transition);
        Assert.Equal([-5, 0, -4], result.CurrentCycle.Gaps);
    }

    [Theory]
    [InlineData(5, 6, 5)]
    [InlineData(-5, -6, -5)]
    public void FixedSizeCycle_SameSignSequence_Unchanged(int first, int second, int third)
    {
        var state = new GapCycleState();
        ProcessFixed(state, first, 0);
        ProcessFixed(state, second, 50);

        var result = ProcessFixed(state, third, 100);

        Assert.Equal(GapCycleTransition.BecameStable, result.Transition);
        Assert.Equal(GapCycleStatus.Stable, result.CurrentCycle.Status);
        Assert.Equal([first, second, third], result.CurrentCycle.Gaps);
    }

    [Fact]
    public void LegacyProcess_SignFlip_StartsNewCycle()
    {
        var state = new GapCycleState();
        Process(state, -5, 0);

        var result = Process(state, 5, 50);

        Assert.Equal(GapCycleTransition.NewCycle, result.Transition);
        Assert.Equal([-5], result.CompletedCycle!.Gaps);
        Assert.Equal([5], result.CurrentCycle.Gaps);
    }

    [Fact]
    public void LegacyProcess_SameSignSequence_Unchanged()
    {
        var state = new GapCycleState();
        Process(state, 100, 0);
        Process(state, 105, 500);

        var result = Process(state, 102, 1000);

        Assert.Equal(GapCycleTransition.BecameStable, result.Transition);
        Assert.Equal([100, 105, 102], result.CurrentCycle.Gaps);
    }

    private static GapCycleUpdateResult ProcessFixed(
        GapCycleState state,
        int gap,
        int elapsedMs,
        int requiredSize = 3) =>
        state.ProcessFixedSize(
            Start.AddMilliseconds(elapsedMs),
            gap,
            hasRequiredData: true,
            confirmSatisfied: true,
            Config,
            requiredSize,
            $"SIGN_GUARD|{elapsedMs}|{gap}");

    private static GapCycleUpdateResult Process(
        GapCycleState state,
        int gap,
        int elapsedMs) =>
        state.Process(
            Start.AddMilliseconds(elapsedMs),
            gap,
            hasRequiredData: true,
            confirmSatisfied: true,
            Config,
            holdConfirmMs: 1000);
}
