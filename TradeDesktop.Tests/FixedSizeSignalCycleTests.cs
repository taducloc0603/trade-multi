using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class FixedSizeSignalCycleTests
{
    private static readonly DateTime Start = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Add_CompletesExactlyAtRequiredSize()
    {
        var cycle = new FixedSizeSignalCycle<int>(10);

        for (var index = 1; index <= 9; index++)
        {
            var update = Add(cycle, index);
            Assert.Equal(FixedSizeCycleStatus.Collecting, update.Status);
            Assert.Equal(index, update.Count);
        }

        var completed = Add(cycle, 10);

        Assert.Equal(FixedSizeCycleStatus.Completed, completed.Status);
        Assert.Equal(10, completed.Count);
        Assert.True(cycle.IsComplete);
        Assert.Equal("CYCLE_COMPLETED", completed.Reason);
        Assert.Throws<InvalidOperationException>(() => Add(cycle, 11));
    }

    [Fact]
    public void Reset_DiscardsCurrentCycleAndInvalidElementIsNotAdded()
    {
        var cycle = new FixedSizeSignalCycle<double>(10);
        cycle.Add(5d, Start, "tp-1");
        cycle.Add(6d, Start.AddMilliseconds(1), "tp-2");

        var reset = cycle.Reset("BELOW_CONFIRM");

        Assert.Equal(FixedSizeCycleStatus.Empty, reset.Status);
        Assert.Equal(0, cycle.Count);
        Assert.True(reset.WasReset);
        Assert.Null(cycle.CycleId);
        Assert.Equal("BELOW_CONFIRM", cycle.LastResetReason);
    }

    [Fact]
    public void RestartWith_UsesSplittingElementAsFirstElementOfNewCycle()
    {
        var cycle = new FixedSizeSignalCycle<int>(10);
        cycle.Add(100, Start, "gap-1");
        var oldCycleId = cycle.CycleId;

        var restarted = cycle.RestartWith(
            150,
            Start.AddMilliseconds(1),
            "gap-2",
            "ELEMENT_STARTED_NEW_CYCLE");

        Assert.True(restarted.WasReset);
        Assert.True(restarted.WasRestarted);
        Assert.Equal(1, restarted.Count);
        Assert.Single(cycle.Values);
        Assert.Equal(150, cycle.Values[0]);
        Assert.NotEqual(oldCycleId, cycle.CycleId);
        Assert.Equal("ELEMENT_STARTED_NEW_CYCLE", restarted.Reason);
    }

    [Fact]
    public void Add_DuplicateFingerprintDoesNotIncreaseCount()
    {
        var cycle = new FixedSizeSignalCycle<int>(10);
        cycle.Add(100, Start, "same-event");

        var duplicate = cycle.Add(100, Start, "same-event");

        Assert.True(duplicate.DuplicateIgnored);
        Assert.Equal(1, cycle.Count);
        Assert.Equal("DUPLICATE_IGNORED", duplicate.Reason);
    }

    [Fact]
    public void Add_TimestampRegressionResetsCycleAndDoesNotAddEvent()
    {
        var cycle = new FixedSizeSignalCycle<int>(10);
        cycle.Add(100, Start.AddSeconds(1), "gap-1");

        var update = cycle.Add(101, Start, "gap-2");

        Assert.Equal(FixedSizeCycleStatus.Empty, update.Status);
        Assert.True(update.WasReset);
        Assert.Equal(0, cycle.Count);
        Assert.Equal("TIMESTAMP_REGRESSION", cycle.LastResetReason);
    }

    [Fact]
    public void Add_SameTimestampWithDifferentFingerprintCountsAsDifferentEvent()
    {
        var cycle = new FixedSizeSignalCycle<int>(10);
        cycle.Add(100, Start, "event-1");

        var update = cycle.Add(101, Start, "event-2");

        Assert.Equal(2, update.Count);
        Assert.False(update.DuplicateIgnored);
    }

    [Fact]
    public void Resize_SameSizeKeepsCycleButDifferentSizeResetsIt()
    {
        var cycle = new FixedSizeSignalCycle<int>(10);
        cycle.Add(100, Start, "gap-1");
        var cycleId = cycle.CycleId;

        var unchanged = cycle.Resize(10);
        Assert.Equal(1, unchanged.Count);
        Assert.Equal(cycleId, cycle.CycleId);
        Assert.False(unchanged.WasReset);

        var changed = cycle.Resize(20);
        Assert.Equal(20, cycle.RequiredSize);
        Assert.Equal(0, cycle.Count);
        Assert.Null(cycle.CycleId);
        Assert.True(changed.WasReset);
        Assert.Equal("CYCLE_SIZE_CHANGED", changed.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidRequiredSizeIsRejected(int requiredSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FixedSizeSignalCycle<int>(requiredSize));

        var cycle = new FixedSizeSignalCycle<int>(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => cycle.Resize(requiredSize));
    }

    [Fact]
    public void InstancesMaintainIndependentCycles()
    {
        var first = new FixedSizeSignalCycle<int>(2);
        var second = new FixedSizeSignalCycle<int>(2);

        Add(first, 1);
        Add(first, 2);
        Add(second, 1);

        Assert.True(first.IsComplete);
        Assert.False(second.IsComplete);
        Assert.Equal(1, second.Count);
        Assert.NotEqual(first.CycleId, second.CycleId);
    }

    [Fact]
    public void ValuesNeverExceedRequiredSize()
    {
        var cycle = new FixedSizeSignalCycle<int>(2_000);

        for (var index = 1; index <= cycle.RequiredSize; index++)
        {
            Add(cycle, index);
        }

        Assert.Equal(2_000, cycle.Values.Count);
        Assert.Throws<InvalidOperationException>(() => Add(cycle, 2_001));
    }

    private static FixedSizeCycleUpdate Add(FixedSizeSignalCycle<int> cycle, int index) =>
        cycle.Add(index, Start.AddMilliseconds(index), $"event-{index}");
}
