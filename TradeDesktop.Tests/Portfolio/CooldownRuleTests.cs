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
    public void TryAcquireTradeAction_SetsGlobalCooldownLock()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 5, maxSec: 5);
        var acquired = coordinator.TryAcquireTradeAction(DateTime.UtcNow, "OPEN", "test");

        Assert.True(acquired.Acquired);
        Assert.False(coordinator.CanCloseNow(out var reason));
        Assert.Contains("GLOBAL_COOLDOWN", reason);
    }

    [Fact]
    public void TryAcquireTradeAction_BlocksAnyFollowingAction()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 10, maxSec: 10);
        var first = coordinator.TryAcquireTradeAction(DateTime.UtcNow, "OPEN", "slot-1");
        var second = coordinator.TryAcquireTradeAction(DateTime.UtcNow, "CLOSE", "slot-2");

        Assert.True(first.Acquired);
        Assert.False(second.Acquired);
        Assert.True(second.Remaining > TimeSpan.Zero);
    }

    [Fact]
    public void CooldownRandomization_AlwaysBetweenMinAndMax()
    {
        var coordinator = CreateCoordinator(seed: 12345);
        coordinator.UpdateCooldownConfig(minSec: 3, maxSec: 125);

        for (var i = 0; i < 20; i++)
        {
            var dispatchTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i * 3);
            var result = coordinator.TryAcquireTradeAction(dispatchTime, "OPEN", $"p{i}");
            Assert.True(result.Acquired);
            Assert.InRange(result.CooldownSeconds, 3, 125);
        }
    }

    [Fact]
    public void CooldownExpiry_ImmediatelyForZero()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        var result = coordinator.TryAcquireTradeAction(DateTime.UtcNow, "OPEN", "test");

        Assert.True(result.Acquired);
        Assert.True(coordinator.CanCloseNow(out _));
    }

    [Fact]
    public void CooldownStartsFromDispatchTime_NotConfirmTime()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 10, maxSec: 10);
        var dispatchTime = DateTime.UtcNow;
        coordinator.TryAcquireTradeAction(dispatchTime, "OPEN", "test");

        var elapsed = (coordinator.GlobalActionLockUntilUtc!.Value - dispatchTime).TotalSeconds;
        Assert.InRange(elapsed, 9.5, 10.5);
    }

    [Fact]
    public void UpdateCooldownConfig_NormalizesReversedRange()
    {
        var coordinator = CreateCoordinator();

        coordinator.UpdateCooldownConfig(minSec: 10, maxSec: 3);

        Assert.Equal(3, coordinator.GlobalCooldownMinSec);
        Assert.Equal(10, coordinator.GlobalCooldownMaxSec);
    }

    [Fact]
    public void TryAcquireTradeAction_IsAtomicAcrossConcurrentCallers()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 10, maxSec: 10);
        var now = DateTime.UtcNow;

        var results = Enumerable.Range(0, 32)
            .AsParallel()
            .Select(i => coordinator.TryAcquireTradeAction(now, "OPEN", $"caller-{i}"))
            .ToArray();

        Assert.Single(results.Where(x => x.Acquired));
        Assert.Equal(31, results.Count(x => !x.Acquired));
    }
}
