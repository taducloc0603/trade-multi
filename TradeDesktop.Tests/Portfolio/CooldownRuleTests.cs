using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Rule B — Global cooldown: random Uniform(min, max) seconds after open/close confirm.
// Locks the whole system.
public sealed class CooldownRuleTests
{
    private static PortfolioCoordinator CreateCoordinator(int seed = 42)
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(seed));

    private static GapSignalTriggerResult Trigger()
        => new(true, GapSignalAction.Open, GapSignalTriggerType.OpenByGapBuy, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    [Fact]
    public void MarkSlotOpenConfirmed_SetsGlobalCooldownLock()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 5, maxSec: 5);
        coordinator.AllocatePendingOpenSlot("p1", Trigger());

        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);

        Assert.False(coordinator.CanCloseNow(out var reason));
        Assert.Contains("GLOBAL_COOLDOWN", reason);
    }

    [Fact]
    public void MarkSlotCloseConfirmed_SetsGlobalCooldownLock()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 10, maxSec: 10);
        coordinator.AllocatePendingOpenSlot("p1", Trigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow.AddSeconds(-30));
        coordinator.MarkSlotCloseTriggered("p1", DateTime.UtcNow);

        coordinator.MarkSlotCloseConfirmed("p1", DateTime.UtcNow);

        Assert.False(coordinator.CanCloseNow(out var reason));
        Assert.Contains("GLOBAL_COOLDOWN", reason);
    }

    [Fact]
    public void CooldownRandomization_AlwaysBetweenMinAndMax()
    {
        var coordinator = CreateCoordinator(seed: 12345);
        coordinator.UpdateCooldownConfig(minSec: 3, maxSec: 125);

        for (var i = 0; i < 20; i++)
        {
            var pairId = $"p{i}";
            coordinator.AllocatePendingOpenSlot(pairId, Trigger());
            var confirmedAt = new DateTime(2026, 5, 21, 10, i, 0, DateTimeKind.Utc);
            coordinator.MarkSlotOpenConfirmed(pairId, (ulong)(i * 2 + 1), (ulong)(i * 2 + 2), confirmedAt);

            var elapsedSec = (coordinator.GlobalActionLockUntilUtc!.Value - confirmedAt).TotalSeconds;
            Assert.InRange(elapsedSec, 3, 125);

            coordinator.MarkSlotCloseTriggered(pairId, confirmedAt.AddSeconds(125));
            coordinator.MarkSlotCloseConfirmed(pairId, confirmedAt.AddSeconds(125));
        }
    }

    [Fact]
    public void CooldownExpiry_ImmediatelyForZero()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        coordinator.AllocatePendingOpenSlot("p1", Trigger());

        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);

        Assert.True(coordinator.CanCloseNow(out _));
    }

    [Fact]
    public void CooldownStartsFromConfirmTime_NotDispatchTime()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 10, maxSec: 10);
        var trigger = Trigger();
        coordinator.AllocatePendingOpenSlot("p1", trigger);

        // Wait a few seconds (simulate execution latency), then confirm with EARLIER time.
        var confirmedAt = DateTime.UtcNow.AddSeconds(-5);
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, confirmedAt);

        // Lock should end at confirmedAt + 10s, not utcNow + 10s.
        Assert.Equal(confirmedAt.AddSeconds(10), coordinator.GlobalActionLockUntilUtc);
    }

    [Fact]
    public void UpdateCooldownConfig_ClampsMinToZero_AndMaxToMin()
    {
        var coordinator = CreateCoordinator();

        coordinator.UpdateCooldownConfig(minSec: -10, maxSec: 5);
        // min clamped to 0; max clamped to >= min.
        coordinator.AllocatePendingOpenSlot("p1", Trigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow);

        // Lock should be at most 5s.
        var elapsedMax = (coordinator.GlobalActionLockUntilUtc!.Value - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(elapsedMax, 0, 5.5);
    }
}
