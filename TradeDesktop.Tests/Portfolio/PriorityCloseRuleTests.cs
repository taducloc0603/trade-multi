using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Rule D — Priority close: when multiple slots trigger close in same tick,
// coordinator picks slot with highest LastProfitSnapshot. Losers keep window.
public sealed class PriorityCloseRuleTests
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

        public void Reset() { /* no-op for scripted engine */ }
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

    private static GapSignalTriggerResult OpenTrigger(GapSignalSide side)
        => new(true, GapSignalAction.Open,
            side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            side,
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

    [Fact]
    public void MultipleEligibleCloses_PicksHighestProfit()
    {
        var factory = new ScriptedFactory();
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), factory, logger: null, random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        // Open 3 Buy slots, fast-forward holding time.
        var openTime = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
        {
            var pairId = $"p{i + 1}";
            var slot = coordinator.AllocatePendingOpenSlot(pairId, OpenTrigger(GapSignalSide.Buy))!;
            // Confirm with timestamp 10 seconds back so holding (1s) is well elapsed.
            coordinator.MarkSlotOpenConfirmed(pairId, (ulong)(100 + i), (ulong)(200 + i),
                openTime.AddSeconds(-10));
        }

        // Set profits.
        coordinator.UpdateProfit(100, 1.5);  // slot 1
        coordinator.UpdateProfit(200, 0.0);
        coordinator.UpdateProfit(101, 3.2);  // slot 2 ← WINNER
        coordinator.UpdateProfit(201, 0.0);
        coordinator.UpdateProfit(102, 2.1);  // slot 3
        coordinator.UpdateProfit(202, 0.0);

        // Set all 3 scripted engines to return a close trigger.
        foreach (var engine in factory.Created)
        {
            engine.NextResult = CloseTrigger();
        }

        // Force no cooldown.
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        var result = coordinator.ProcessSnapshot(Snapshot(openTime.AddSeconds(20)), Config());

        Assert.NotNull(result.CloseTrigger);
        Assert.NotNull(result.CloseTargetSlot);
        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
        Assert.Equal(3.2, result.CloseTargetSlot.LastProfitSnapshot);
    }

    [Fact]
    public void OnlyOneEligibleClose_PicksIt()
    {
        var factory = new ScriptedFactory();
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), factory, logger: null, random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        var openTime = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 2; i++)
        {
            var pairId = $"p{i + 1}";
            coordinator.AllocatePendingOpenSlot(pairId, OpenTrigger(GapSignalSide.Buy));
            coordinator.MarkSlotOpenConfirmed(pairId, (ulong)(100 + i), (ulong)(200 + i),
                openTime.AddSeconds(-10));
        }

        // Only slot 1 (factory.Created[0]) emits close trigger.
        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = null;

        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        var result = coordinator.ProcessSnapshot(Snapshot(openTime.AddSeconds(20)), Config());

        Assert.NotNull(result.CloseTargetSlot);
        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void NoEligibleCloses_ReturnsEmpty()
    {
        var factory = new ScriptedFactory();
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), factory, logger: null, random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        var openTime = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, openTime.AddSeconds(-10));

        factory.Created[0].NextResult = null;  // no close

        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        var result = coordinator.ProcessSnapshot(Snapshot(openTime.AddSeconds(20)), Config());

        Assert.Null(result.CloseTrigger);
        Assert.Null(result.CloseTargetSlot);
    }

    [Fact]
    public void EligibleCloseWithNullProfit_TreatedAsMinValue()
    {
        var factory = new ScriptedFactory();
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), factory, logger: null, random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        var openTime = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger(GapSignalSide.Buy));
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, openTime.AddSeconds(-10));
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, openTime.AddSeconds(-10));

        coordinator.UpdateProfit(101, -5.0);  // slot 2: negative profit but defined
        coordinator.UpdateProfit(201, 0.0);
        // slot 1 has null profit (no UpdateProfit call) → treated as MinValue

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        var result = coordinator.ProcessSnapshot(Snapshot(openTime.AddSeconds(20)), Config());

        // slot 2 (profit -5) beats slot 1 (null = MinValue).
        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void SlotPendingClose_ExcludedFromCandidates()
    {
        var factory = new ScriptedFactory();
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), factory, logger: null, random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        var openTime = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger(GapSignalSide.Buy));
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, openTime.AddSeconds(-10));
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, openTime.AddSeconds(-10));

        // Slot 1 already triggered close (PendingClose status); only slot 2 eligible now.
        coordinator.MarkSlotCloseTriggered("p1", openTime);
        coordinator.UpdateProfit(100, 100.0);  // slot 1 has high profit but excluded
        coordinator.UpdateProfit(200, 0.0);
        coordinator.UpdateProfit(101, 1.0);
        coordinator.UpdateProfit(201, 0.0);

        factory.Created[1].NextResult = CloseTrigger();  // slot 2

        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        var result = coordinator.ProcessSnapshot(Snapshot(openTime.AddSeconds(20)), Config());

        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }
}
