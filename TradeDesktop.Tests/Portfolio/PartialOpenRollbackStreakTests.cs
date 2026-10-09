using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// F4-3 (docs/plans/primexbt phase-4): 2 partial-open rollback LIÊN TIẾP ⇒ chặn Auto Open tới khi Stop/Start.
// Cặp mở đủ hai chân reset bộ đếm. Chỉ chặn Open — không đụng Close.
public sealed class PartialOpenRollbackStreakTests
{
    private static PortfolioCoordinator CreateCoordinator()
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 5, maxBuy: 3, maxSell: 3);
        return coordinator;
    }

    private static GapSignalTriggerResult Trigger(GapSignalSide side)
        => new(
            Triggered: true,
            Action: GapSignalAction.Open,
            TriggerType: side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            PrimarySide: side,
            BuyGaps: Array.Empty<int>(),
            SellGaps: Array.Empty<int>(),
            LastBuyGap: null,
            LastSellGap: null,
            TriggeredAtUtc: new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc),
            LastABid: null, LastAAsk: null, LastBBid: null, LastBAsk: null,
            GapBuySourceBBid: null, GapBuySourceAAsk: null,
            GapSellSourceBAsk: null, GapSellSourceABid: null,
            PointMultiplier: 1);

    [Fact]
    public void Limit_IsTwo()
    {
        Assert.Equal(2, PortfolioCoordinator.PartialOpenRollbackStreakLimit);
    }

    [Fact]
    public void OneRollback_DoesNotBlock()
    {
        var coordinator = CreateCoordinator();

        Assert.False(coordinator.RecordPartialOpenRollback("p1"));

        Assert.Equal(1, coordinator.ConsecutivePartialOpenRollbacks);
        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }

    [Fact]
    public void TwoRollbacks_BlockBothSides_TripReportedOnce()
    {
        var coordinator = CreateCoordinator();

        Assert.False(coordinator.RecordPartialOpenRollback("p1"));
        Assert.True(coordinator.RecordPartialOpenRollback("p2"));
        Assert.False(coordinator.RecordPartialOpenRollback("p3")); // đã chặn — không báo lại

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out var buyReason));
        Assert.Contains("PARTIAL_OPEN_ROLLBACK_STREAK", buyReason);
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var sellReason));
        Assert.Contains("PARTIAL_OPEN_ROLLBACK_STREAK", sellReason);
    }

    [Fact]
    public void FullOpenConfirmed_ResetsStreak()
    {
        var coordinator = CreateCoordinator();
        coordinator.RecordPartialOpenRollback("p1");
        coordinator.AllocatePendingOpenSlot("p2", Trigger(GapSignalSide.Buy));

        coordinator.MarkSlotOpenConfirmed("p2", 11, 22, DateTime.UtcNow);
        Assert.Equal(0, coordinator.ConsecutivePartialOpenRollbacks);

        Assert.False(coordinator.RecordPartialOpenRollback("p3")); // chuỗi bắt đầu lại từ 1
        Assert.Equal(1, coordinator.ConsecutivePartialOpenRollbacks);
    }

    [Fact]
    public void StartStop_ClearAllSlots_ReleasesBlock()
    {
        var coordinator = CreateCoordinator();
        coordinator.RecordPartialOpenRollback("p1");
        coordinator.RecordPartialOpenRollback("p2");

        coordinator.ClearAllSlots();

        Assert.Equal(0, coordinator.ConsecutivePartialOpenRollbacks);
        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }

    [Fact]
    public void Reset_ReleasesBlock()
    {
        var coordinator = CreateCoordinator();
        coordinator.RecordPartialOpenRollback("p1");
        coordinator.RecordPartialOpenRollback("p2");

        coordinator.Reset();

        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out _));
    }

    [Fact]
    public void Block_DoesNotTouchExistingLiveSlot()
    {
        var coordinator = CreateCoordinator();
        coordinator.AllocatePendingOpenSlot("live", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("live", 11, 22, DateTime.UtcNow);
        coordinator.RecordPartialOpenRollback("p1");
        coordinator.RecordPartialOpenRollback("p2");

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
        Assert.Equal(PositionSlotStatus.Live, coordinator.GetSlotByPairId("live")!.Status);
    }
}
