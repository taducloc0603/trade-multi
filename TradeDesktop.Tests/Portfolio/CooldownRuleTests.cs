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
            // Phase 8: cooldown được set tại allocate (dispatch time), không phải confirm.
            // Capture dispatchTime ngay trước allocate để đo elapsed chính xác.
            var dispatchTime = DateTime.UtcNow;
            coordinator.AllocatePendingOpenSlot(pairId, Trigger());
            var confirmedAt = dispatchTime.AddMilliseconds(500);
            coordinator.MarkSlotOpenConfirmed(pairId, (ulong)(i * 2 + 1), (ulong)(i * 2 + 2), confirmedAt);

            var elapsedSec = (coordinator.GlobalActionLockUntilUtc!.Value - dispatchTime).TotalSeconds;
            Assert.InRange(elapsedSec, 3, 126);

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
    public void CooldownStartsFromDispatchTime_NotConfirmTime()
    {
        // Phase 8: cooldown bắt đầu tại dispatch (AllocatePendingOpenSlot), không phải confirm.
        // User intent: min 3-10s giữa bất kỳ 2 trade events — race window dispatch→confirm
        // (~500ms broker latency) phải được bảo vệ.
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 10, maxSec: 10);
        var trigger = Trigger();

        var dispatchTime = DateTime.UtcNow;
        coordinator.AllocatePendingOpenSlot("p1", trigger);

        // Confirm với EARLIER time — không được rút ngắn lock (MAX semantics).
        var confirmedAt = dispatchTime.AddSeconds(-5);
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, confirmedAt);

        // Lock = dispatchTime + 10s (allocate-time), KHÔNG phải confirmedAt + 10s.
        var elapsed = (coordinator.GlobalActionLockUntilUtc!.Value - dispatchTime).TotalSeconds;
        Assert.InRange(elapsed, 9.5, 10.5);
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
