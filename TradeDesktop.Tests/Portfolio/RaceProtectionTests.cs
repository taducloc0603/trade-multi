using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Phase 3 — Race protection: AllocatePendingOpenSlot must double-check quota under lock.
public sealed class RaceProtectionTests
{
    private static PortfolioCoordinator CreateCoordinator()
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(42));

    private static GapSignalTriggerResult OpenTrigger()
        => new(true, GapSignalAction.Open, GapSignalTriggerType.OpenByGapBuy, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    [Fact]
    public void ConcurrentAllocations_RespectQuota()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 3, maxBuy: 3, maxSell: 3);

        var allocated = new System.Collections.Concurrent.ConcurrentBag<PositionSlot?>();
        Parallel.For(0, 10, i =>
        {
            var pairId = $"p{i}";
            var slot = coordinator.AllocatePendingOpenSlot(pairId, OpenTrigger());
            allocated.Add(slot);
        });

        var successCount = allocated.Count(s => s is not null);
        Assert.Equal(3, successCount);  // Quota strictly enforced.
        Assert.Equal(3, coordinator.LiveAndPendingTotalCount);
    }

    [Fact]
    public void Allocate_AfterAbort_RefillsQuota()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 1, maxBuy: 1, maxSell: 1);

        var s1 = coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        Assert.NotNull(s1);
        var s2 = coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        Assert.Null(s2);  // quota full

        coordinator.AbortPendingOpen("p1");

        var s3 = coordinator.AllocatePendingOpenSlot("p3", OpenTrigger());
        Assert.NotNull(s3);  // quota slot freed
    }
}
