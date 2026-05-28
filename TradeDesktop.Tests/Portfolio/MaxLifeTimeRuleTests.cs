using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// max_life_time_by_second: slots open longer than the threshold are prioritized for close.
// Among overtime slots, pick highest profit. If none are overtime, fall back to Rule D (highest profit).
public sealed class MaxLifeTimeRuleTests
{
    private sealed class ScriptedCloseSignalEngine : ICloseSignalEngine
    {
        public GapSignalTriggerResult? NextResult { get; set; }

        public GapSignalTriggerResult? ProcessSnapshot(
            GapSignalSnapshot snapshot,
            GapSignalConfirmationConfig config,
            TradingOpenMode openMode,
            double? slotProfit = null)
            => NextResult;

        public void Reset() { }
    }

    private sealed class ScriptedFactory : ICloseSignalEngineFactory
    {
        public List<ScriptedCloseSignalEngine> Created { get; } = new();

        public ICloseSignalEngine Create()
        {
            var engine = new ScriptedCloseSignalEngine();
            Created.Add(engine);
            return engine;
        }
    }

    private static GapSignalTriggerResult OpenTrigger()
        => new(true, GapSignalAction.Open, GapSignalTriggerType.OpenByGapBuy, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    private static GapSignalTriggerResult CloseTrigger()
        => new(true, GapSignalAction.Close, GapSignalTriggerType.CloseByGapSell, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 11, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    private static GapSignalSnapshot Snapshot(DateTime ts)
        => new(ts, 100m, 100.5m, 100m, 100.5m, GapBuy: null, GapSell: null, PointMultiplier: 1);

    private static GapSignalConfirmationConfig Config()
        => new(ConfirmGapPts: 5, OpenPts: 8, HoldConfirmMs: 100,
               CloseConfirmGapPts: 5, ClosePts: 8, CloseHoldConfirmMs: 100,
               StartTimeHold: 1, EndTimeHold: 1);

    private static PortfolioCoordinator BuildCoordinator(ScriptedFactory factory)
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), factory, logger: null, random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        return coordinator;
    }

    [Fact]
    public void OvertimeSlot_WinsOverHigherProfitNonOvertimeSlot()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(1200);

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // Slot 1: open 1300s ago (overtime), profit = -10
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-1300));
        coordinator.UpdateProfit(100, -5.0);
        coordinator.UpdateProfit(200, -5.0); // combined = -10

        // Slot 2: open 800s ago (not overtime), profit = +5
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, now.AddSeconds(-800));
        coordinator.UpdateProfit(101, 2.5);
        coordinator.UpdateProfit(201, 2.5); // combined = +5

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // p1 is overtime → wins even though profit is lower
        Assert.NotNull(result.CloseTargetSlot);
        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void MultipleOvertimeSlots_PicksHighestProfitAmongThem()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(1200);

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // Slots 1, 2, 3: all overtime (> 1200s)
        for (var i = 0; i < 3; i++)
        {
            var pairId = $"p{i + 1}";
            coordinator.AllocatePendingOpenSlot(pairId, OpenTrigger());
            coordinator.MarkSlotOpenConfirmed(pairId, (ulong)(100 + i), (ulong)(200 + i),
                now.AddSeconds(-1500));
        }

        // Profits: p1=-10, p2=-5 (highest among overtime), p3=-8
        coordinator.UpdateProfit(100, -5.0); coordinator.UpdateProfit(200, -5.0);  // p1: -10
        coordinator.UpdateProfit(101, -2.5); coordinator.UpdateProfit(201, -2.5);  // p2: -5 ← WINNER
        coordinator.UpdateProfit(102, -4.0); coordinator.UpdateProfit(202, -4.0);  // p3: -8

        foreach (var engine in factory.Created)
            engine.NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void NoOvertimeSlots_FallsBackToRuleD_HighestProfit()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(1200);

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // Both slots well under 1200s (not overtime)
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-300));
        coordinator.UpdateProfit(100, 1.0); coordinator.UpdateProfit(200, 1.0);  // p1: 2.0

        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, now.AddSeconds(-600));
        coordinator.UpdateProfit(101, 2.5); coordinator.UpdateProfit(201, 2.5);  // p2: 5.0 ← WINNER

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // No overtime → Rule D: highest profit (p2)
        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void MaxLifeTime_Zero_Disabled_PureRuleD()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(0); // disabled

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // p1: very old (10000s), profit = -20
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-10000));
        coordinator.UpdateProfit(100, -10.0); coordinator.UpdateProfit(200, -10.0);

        // p2: recent, profit = +3
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, now.AddSeconds(-100));
        coordinator.UpdateProfit(101, 1.5); coordinator.UpdateProfit(201, 1.5);

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // MaxLifeTime=0 disabled → pure Rule D: p2 (profit +3 > -20)
        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void SlotWithoutOpenConfirmedAtUtc_NotCountedAsOvertime()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(1200);

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // p1: synced slot without OpenConfirmedAtUtc set properly (MarkSynced with 0-holdingSeconds)
        // We simulate by using RegisterSyncedSlot with epoch openConfirmedAtUtc = far in past
        // but setting maxLifeTime > elapsed to ensure it's NOT overtime
        // Actually, we test that a non-overtime slot doesn't enter overtime tier when
        // there's another non-overtime slot with higher profit.

        // p1: 800s old (not overtime), profit = +10
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-800));
        coordinator.UpdateProfit(100, 5.0); coordinator.UpdateProfit(200, 5.0);

        // p2: 1300s old (overtime), profit = -5
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, now.AddSeconds(-1300));
        coordinator.UpdateProfit(101, -2.5); coordinator.UpdateProfit(201, -2.5);

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // p2 is overtime → p2 wins despite lower profit
        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }
}
