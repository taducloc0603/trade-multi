using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.HoldConfirm;

public sealed class GapTickChangeTrackerTests
{
    private static readonly DateTime Start = new(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc);

    private static GapSignalSnapshot Quote(
        int index,
        decimal? aBid = 2411.00m,
        decimal? aAsk = 2411.20m,
        decimal? bBid = 2411.50m,
        decimal? bAsk = 2411.70m) =>
        new(Start.AddMilliseconds(index * 50), aBid, aAsk, bBid, bAsk, 30, -50, 100);

    [Fact]
    public void FirstSnapshot_IsTickOnBothLegs()
    {
        var tracker = new GapTickChangeTracker();

        var marked = tracker.Mark(Quote(0) with { IsNewTickA = false, IsNewTickB = false });

        Assert.True(marked.IsNewTickA);
        Assert.True(marked.IsNewTickB);
    }

    [Fact]
    public void FrozenPrices_AreNotTicks()
    {
        var tracker = new GapTickChangeTracker();
        tracker.Mark(Quote(0));

        var marked = tracker.Mark(Quote(1));

        Assert.False(marked.IsNewTickA);
        Assert.False(marked.IsNewTickB);
    }

    [Fact]
    public void OnlyLegAChanges()
    {
        var tracker = new GapTickChangeTracker();
        tracker.Mark(Quote(0));

        var marked = tracker.Mark(Quote(1, aAsk: 2411.25m));

        Assert.True(marked.IsNewTickA);
        Assert.False(marked.IsNewTickB);
    }

    [Fact]
    public void OnlyLegBBidChanges()
    {
        var tracker = new GapTickChangeTracker();
        tracker.Mark(Quote(0));

        var marked = tracker.Mark(Quote(1, bBid: 2411.45m));

        Assert.False(marked.IsNewTickA);
        Assert.True(marked.IsNewTickB);
    }

    [Fact]
    public void ComparesWithImmediatelyPreviousSnapshot()
    {
        var tracker = new GapTickChangeTracker();
        tracker.Mark(Quote(0));
        tracker.Mark(Quote(1, aBid: 2411.05m));

        // Quay lại giá cũ vẫn là một lần đổi so với snapshot ngay trước.
        var back = tracker.Mark(Quote(2));
        var frozen = tracker.Mark(Quote(3));

        Assert.True(back.IsNewTickA);
        Assert.False(frozen.IsNewTickA);
    }

    [Fact]
    public void MissingPrice_ToPrice_IsTick()
    {
        var tracker = new GapTickChangeTracker();
        tracker.Mark(Quote(0, bBid: null, bAsk: null));

        var stillMissing = tracker.Mark(Quote(1, bBid: null, bAsk: null));
        var recovered = tracker.Mark(Quote(2));

        Assert.False(stillMissing.IsNewTickB);
        Assert.True(recovered.IsNewTickB);
    }

    [Fact]
    public void KeepsOtherFields()
    {
        var tracker = new GapTickChangeTracker();
        tracker.Mark(Quote(0));
        var input = Quote(1, aBid: 2411.10m);

        var marked = tracker.Mark(input);

        Assert.Equal(input with { IsNewTickA = true, IsNewTickB = false }, marked);
    }

    [Fact]
    public void Coordinator_MarksTicksBeforeNonAutoBarrierEarlyReturn()
    {
        var openEngine = new RecordingOpenEngine();
        var coordinator = new PortfolioCoordinator(openEngine, new CloseSignalEngineFactory());
        var config = new GapSignalConfirmationConfig(ConfirmGapPts: 50, OpenPts: 100);

        coordinator.ProcessSnapshot(Quote(0), config);

        coordinator.BeginNonAutoCloseOperation("manual-pair");
        coordinator.ProcessSnapshot(Quote(1, aBid: 2411.05m), config);
        coordinator.EndNonAutoCloseOperation("manual-pair");

        // Cùng giá với snapshot bị chặn: nếu tracker chạy SAU early-return thì A sẽ bị coi là tick mới.
        coordinator.ProcessSnapshot(Quote(2, aBid: 2411.05m), config);
        coordinator.ProcessSnapshot(Quote(3, aBid: 2411.05m, bAsk: 2411.75m), config);

        Assert.Equal(3, openEngine.Seen.Count);
        Assert.True(openEngine.Seen[0].IsNewTickA && openEngine.Seen[0].IsNewTickB);
        Assert.False(openEngine.Seen[1].IsNewTickA);
        Assert.False(openEngine.Seen[1].IsNewTickB);
        Assert.False(openEngine.Seen[2].IsNewTickA);
        Assert.True(openEngine.Seen[2].IsNewTickB);
    }

    private sealed class RecordingOpenEngine : IOpenSignalEngine
    {
        public List<GapSignalSnapshot> Seen { get; } = [];

        public IReadOnlyList<GapSignalTriggerResult> ProcessSnapshot(
            GapSignalSnapshot snapshot,
            GapSignalConfirmationConfig config)
        {
            Seen.Add(snapshot);
            return [];
        }

        public void Reset() { }
    }
}
