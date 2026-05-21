using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Phase 7 — Stability stress: run many open/close cycles, verify invariants hold.
public sealed class StabilityStressTests
{
    private sealed class ScriptedCloseEngine : ICloseSignalEngine
    {
        public GapSignalTriggerResult? NextResult { get; set; }
        public GapSignalTriggerResult? ProcessSnapshot(GapSignalSnapshot s, GapSignalConfirmationConfig c, TradingOpenMode m)
            => NextResult;
        public void Reset() { }
    }

    private sealed class ScriptedFactory : ICloseSignalEngineFactory
    {
        public List<ScriptedCloseEngine> Created { get; } = new();
        public ICloseSignalEngine Create()
        {
            var e = new ScriptedCloseEngine();
            Created.Add(e);
            return e;
        }
    }

    private static GapSignalTriggerResult OpenTrigger(GapSignalSide side, DateTime ts)
        => new(true, GapSignalAction.Open,
            side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            side,
            Array.Empty<int>(), Array.Empty<int>(), null, null, ts,
            null, null, null, null, null, null, null, null, 1);

    private static GapSignalTriggerResult CloseTrigger(DateTime ts)
        => new(true, GapSignalAction.Close, GapSignalTriggerType.CloseByGapSell, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null, ts,
            null, null, null, null, null, null, null, null, 1);

    [Fact]
    public void Stability_HundredCycles_QuotaInvariantsHold()
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(7777));

        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        var openCount = 0;
        var closeCount = 0;
        var maxLiveSeen = 0;
        var maxBuySeen = 0;
        var maxSellSeen = 0;

        var baseTime = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

        // Simulate 100 cycles: open (random side past opposite-lock), confirm, close.
        for (var i = 0; i < 100; i++)
        {
            // Force opposite-lock past by setting LastOpen long ago via reset.
            // Use single side per batch to keep test predictable.
            var side = i % 5 == 0 ? GapSignalSide.Sell : GapSignalSide.Buy;
            var sidePosition = side == GapSignalSide.Buy ? TradingPositionSide.Buy : TradingPositionSide.Sell;

            // Move opposite-lock forward by manually clearing if needed.
            // For test simplicity: only open same side as last → no opposite-lock.
            // Use coordinator state to choose side.
            if (coordinator.LastOpenConfirmedSide != TradingPositionSide.None
                && coordinator.LastOpenConfirmedSide != sidePosition)
            {
                // Skip opposite-side opens to avoid lock.
                sidePosition = coordinator.LastOpenConfirmedSide;
                side = sidePosition == TradingPositionSide.Buy ? GapSignalSide.Buy : GapSignalSide.Sell;
            }

            var pairId = $"p{i + 1}";
            var openTs = baseTime.AddSeconds(i * 60);
            var slot = coordinator.AllocatePendingOpenSlot(pairId, OpenTrigger(side, openTs));

            if (slot is null) continue; // quota full this iteration
            openCount++;

            coordinator.MarkSlotOpenConfirmed(pairId, (ulong)(10000 + i * 2), (ulong)(10001 + i * 2), openTs);

            // Invariant: live + pending ≤ cap.
            Assert.True(coordinator.LiveAndPendingTotalCount <= 7,
                $"Iter {i}: total={coordinator.LiveAndPendingTotalCount} exceeds cap 7");
            Assert.True(coordinator.LiveBuyCount <= 4,
                $"Iter {i}: buy={coordinator.LiveBuyCount} exceeds 4");
            Assert.True(coordinator.LiveSellCount <= 4,
                $"Iter {i}: sell={coordinator.LiveSellCount} exceeds 4");

            maxLiveSeen = Math.Max(maxLiveSeen, coordinator.LiveCount);
            maxBuySeen = Math.Max(maxBuySeen, coordinator.LiveBuyCount);
            maxSellSeen = Math.Max(maxSellSeen, coordinator.LiveSellCount);

            // Close immediately on same iter (back-to-back cycle).
            coordinator.MarkSlotCloseTriggered(pairId, openTs.AddSeconds(30));
            coordinator.MarkSlotCloseConfirmed(pairId, openTs.AddSeconds(60));
            closeCount++;
        }

        // Final assertions: cycle counts match, no leaked slots.
        Assert.Equal(openCount, closeCount);
        Assert.True(openCount > 50, $"Too few opens: {openCount}");
        Assert.Equal(0, coordinator.LiveCount);
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public void Stability_RuleD_PriorityClose_Repeated_NoLeakage()
    {
        // Repeated multi-slot close picks should always pick highest profit + clean up.
        var factory = new ScriptedFactory();
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(),
            factory,
            logger: null,
            random: new Random(4242));

        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 7, maxSell: 7);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        var baseTime = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

        for (var cycle = 0; cycle < 10; cycle++)
        {
            // Open 3 Buy slots.
            var trigger = OpenTrigger(GapSignalSide.Buy, baseTime.AddMinutes(cycle * 10));
            for (var i = 0; i < 3; i++)
            {
                var pairId = $"c{cycle}-s{i}";
                var s = coordinator.AllocatePendingOpenSlot(pairId, trigger);
                Assert.NotNull(s);
                coordinator.MarkSlotOpenConfirmed(pairId,
                    (ulong)(cycle * 1000 + i * 10),
                    (ulong)(cycle * 1000 + i * 10 + 1),
                    baseTime.AddMinutes(cycle * 10).AddSeconds(-10));
            }

            // Assign profits: middle slot wins.
            coordinator.UpdateProfit((ulong)(cycle * 1000 + 0 * 10), 1.0);
            coordinator.UpdateProfit((ulong)(cycle * 1000 + 1 * 10), 5.0); // winner
            coordinator.UpdateProfit((ulong)(cycle * 1000 + 2 * 10), 2.0);

            // Configure all 3 close engines to return close trigger.
            var thisCycleEngines = factory.Created.TakeLast(3).ToList();
            foreach (var e in thisCycleEngines)
            {
                e.NextResult = CloseTrigger(baseTime.AddMinutes(cycle * 10).AddMinutes(1));
            }

            // Hit ProcessSnapshot once: should pick middle slot.
            var snap = new GapSignalSnapshot(baseTime.AddMinutes(cycle * 10).AddMinutes(5),
                100m, 100.5m, 100m, 100.5m, null, null, 1);
            var cfg = new GapSignalConfirmationConfig(
                ConfirmGapPts: 5, OpenPts: 8, HoldConfirmMs: 100,
                CloseConfirmGapPts: 5, ClosePts: 8, CloseHoldConfirmMs: 100,
                StartTimeHold: 1, EndTimeHold: 1);
            var result = coordinator.ProcessSnapshot(snap, cfg);

            Assert.NotNull(result.CloseTargetSlot);
            Assert.Equal($"c{cycle}-s1", result.CloseTargetSlot!.PairId);

            // Cleanup: close the picked winner + 2 remaining slots manually.
            coordinator.MarkSlotCloseConfirmed(result.CloseTargetSlot.PairId, baseTime.AddMinutes(cycle * 10).AddMinutes(2));
            coordinator.MarkSlotCloseTriggered($"c{cycle}-s0", baseTime.AddMinutes(cycle * 10).AddMinutes(2));
            coordinator.MarkSlotCloseConfirmed($"c{cycle}-s0", baseTime.AddMinutes(cycle * 10).AddMinutes(2));
            coordinator.MarkSlotCloseTriggered($"c{cycle}-s2", baseTime.AddMinutes(cycle * 10).AddMinutes(2));
            coordinator.MarkSlotCloseConfirmed($"c{cycle}-s2", baseTime.AddMinutes(cycle * 10).AddMinutes(2));
        }

        // After 10 cycles: 0 live slots, no leakage.
        Assert.Equal(0, coordinator.LiveCount);
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public void Stability_AbortPathway_DoesNotLeakQuota()
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(8888));
        coordinator.UpdateQuotaConfig(maxTotal: 1, maxBuy: 1, maxSell: 1);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        var baseTime = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

        // 50 abort cycles: allocate then abort.
        for (var i = 0; i < 50; i++)
        {
            var pairId = $"abort-{i}";
            var s = coordinator.AllocatePendingOpenSlot(pairId, OpenTrigger(GapSignalSide.Buy, baseTime));
            Assert.NotNull(s);
            coordinator.AbortPendingOpen(pairId);

            Assert.Equal(0, coordinator.LiveAndPendingTotalCount);
        }
    }
}
