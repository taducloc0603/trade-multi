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

    [Theory]
    [InlineData("OPEN", "OPEN")]
    [InlineData("OPEN", "CLOSE")]
    [InlineData("CLOSE", "OPEN")]
    [InlineData("CLOSE", "CLOSE")]
    public void PostActionLocks_BlockEveryFollowingActionAcrossSlots(
        string firstAction,
        string secondAction)
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(0, 0);
        coordinator.UpdatePostOpenLockConfig(11);
        coordinator.UpdatePostCloseLockConfig(17);
        var now = DateTime.UtcNow;

        var first = coordinator.TryAcquireTradeAction(now, firstAction, "slot-1");
        var second = coordinator.TryAcquireTradeAction(now.AddSeconds(1), secondAction, "slot-2");

        Assert.True(first.Acquired);
        Assert.Equal(firstAction == "OPEN" ? 11 : 17, first.CooldownSeconds);
        Assert.False(second.Acquired);
        Assert.True(second.Remaining > TimeSpan.Zero);
    }

    [Theory]
    [InlineData("OPEN", 11)]
    [InlineData("CLOSE", 17)]
    public void PostActionLock_AfterWindowExpires_AllowsNextAction(string firstAction, int waitSeconds)
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(0, 0);
        coordinator.UpdatePostOpenLockConfig(11);
        coordinator.UpdatePostCloseLockConfig(17);
        var now = DateTime.UtcNow;

        Assert.True(coordinator.TryAcquireTradeAction(now, firstAction, "slot-1").Acquired);
        var next = coordinator.TryAcquireTradeAction(
            now.AddSeconds(waitSeconds).AddMilliseconds(1),
            "OPEN",
            "slot-2");

        Assert.True(next.Acquired);
    }

    [Fact]
    public void OpenConfirm_ExtendsGlobalLockFromCompletionTime()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdatePostOpenLockConfig(10);
        var dispatchAt = DateTime.UtcNow;
        coordinator.TryAcquireTradeAction(dispatchAt, "OPEN", "slot-1");
        coordinator.AllocatePendingOpenSlot("p1", Trigger());

        var confirmedAt = dispatchAt.AddSeconds(3);
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, confirmedAt);

        Assert.Equal(confirmedAt.AddSeconds(10), coordinator.GlobalActionLockUntilUtc);
        Assert.False(coordinator.TryAcquireTradeAction(
            confirmedAt.AddSeconds(9), "CLOSE", "slot-2").Acquired);
    }

    [Fact]
    public void CloseConfirm_ExtendsGlobalLockFromCompletionTime()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdatePostCloseLockConfig(10);
        var dispatchAt = DateTime.UtcNow;
        coordinator.AllocatePendingOpenSlot("p1", Trigger());
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, dispatchAt.AddSeconds(-30));
        coordinator.TryAcquireTradeAction(dispatchAt, "CLOSE", "slot-1");
        coordinator.MarkSlotCloseTriggered("p1", dispatchAt);

        var confirmedAt = dispatchAt.AddSeconds(3);
        coordinator.MarkSlotCloseConfirmed("p1", confirmedAt);

        Assert.Equal(confirmedAt.AddSeconds(10), coordinator.GlobalActionLockUntilUtc);
        Assert.False(coordinator.TryAcquireTradeAction(
            confirmedAt.AddSeconds(9), "CLOSE", "slot-2").Acquired);
    }
}
