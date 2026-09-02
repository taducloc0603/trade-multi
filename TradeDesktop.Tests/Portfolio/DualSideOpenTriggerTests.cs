using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Với ngưỡng Open ÂM, GapSignalConfirmationEngine có thể phát cả OpenByGapBuy và OpenByGapSell
// trong cùng một snapshot (điều bất khả thi khi ngưỡng dương, vì GapSell >= GapBuy luôn đúng).
// PortfolioCoordinator vẫn chỉ được mở đúng một chiều.
public sealed class DualSideOpenTriggerTests
{
    private static readonly GapStabilityConfig OpenStability =
        new(10, 0.50, 4.0, 3, 0.35, 0.40);

    private static readonly GapStabilityConfig CloseStability =
        new(10, 0.60, 4.0, 3, 0.45, 0.60);

    private static readonly DateTime Start =
        new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    private static PortfolioCoordinator CreateCoordinator()
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(42));

    private static GapSignalConfirmationConfig Config(int confirm, int open) =>
        new(
            ConfirmGapPts: confirm,
            OpenPts: open,
            OpenGapStability: OpenStability,
            CloseGapStability: CloseStability,
            SignalCycleSize: 3);

    private static GapSignalSnapshot Snapshot(int second) =>
        new(
            Start.AddSeconds(second),
            ExchangeABid: 1.1000m,
            ExchangeAAsk: 1.1001m,
            ExchangeBBid: 1.1101m,
            ExchangeBAsk: 1.1102m,
            GapBuy: -3,
            GapSell: 2,
            PointMultiplier: 100);

    private static GapSignalSnapshot BuyOnlySnapshot(int second) =>
        new(
            Start.AddSeconds(second),
            ExchangeABid: 1.1000m,
            ExchangeAAsk: 1.1001m,
            ExchangeBBid: 1.1101m,
            ExchangeBAsk: 1.1102m,
            GapBuy: -3,
            GapSell: null,
            PointMultiplier: 100);

    [Fact]
    public void NegativeOpenThresholds_EngineEmitsBothSides_CoordinatorOpensOnlyOne()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 5, maxBuy: 3, maxSell: 3);
        var config = Config(confirm: -5, open: -3);

        Assert.Null(coordinator.ProcessSnapshot(Snapshot(0), config).OpenTrigger);
        Assert.Null(coordinator.ProcessSnapshot(Snapshot(1), config).OpenTrigger);

        var result = coordinator.ProcessSnapshot(Snapshot(2), config);

        // Engine phát 2 trigger nhưng coordinator chỉ trả về đúng 1 OpenTrigger.
        Assert.NotNull(result.OpenTrigger);
        Assert.Null(result.CloseTrigger);
        Assert.Equal(GapSignalAction.Open, result.OpenTrigger!.Action);
    }

    [Fact]
    public void NegativeOpenThresholds_SecondSideCannotOpenWhileQuotaIsFull()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 1, maxBuy: 1, maxSell: 1);
        var config = Config(confirm: -5, open: -3);

        coordinator.ProcessSnapshot(Snapshot(0), config);
        coordinator.ProcessSnapshot(Snapshot(1), config);
        var result = coordinator.ProcessSnapshot(Snapshot(2), config);

        Assert.NotNull(result.OpenTrigger);
        coordinator.AllocatePendingOpenSlot("pair-1", result.OpenTrigger!);

        // Quota tổng đã đầy: chiều còn lại không được mở.
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out var buyReason));
        Assert.Contains("QUOTA_TOTAL_FULL", buyReason);
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var sellReason));
        Assert.Contains("QUOTA_TOTAL_FULL", sellReason);
    }

    [Fact]
    public void DualSideTrigger_DroppedSideIsReportedInBlockedSignals()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 5, maxBuy: 3, maxSell: 3);
        var config = Config(confirm: -5, open: -3);

        coordinator.ProcessSnapshot(Snapshot(0), config);
        coordinator.ProcessSnapshot(Snapshot(1), config);
        var result = coordinator.ProcessSnapshot(Snapshot(2), config);

        Assert.NotNull(result.OpenTrigger);
        Assert.NotNull(result.BlockedSignals);

        var dropped = Assert.Single(
            result.BlockedSignals!,
            b => b.BlockReason.StartsWith("DUAL_SIDE_TRIGGER_DROPPED", StringComparison.Ordinal));

        // Engine tính nhánh Buy trước nên Buy được chọn, Sell là chiều bị bỏ.
        Assert.Equal(GapSignalTriggerType.OpenByGapBuy, result.OpenTrigger!.TriggerType);
        Assert.Equal(GapSignalTriggerType.OpenByGapSell, dropped.Trigger.TriggerType);
        Assert.Null(dropped.CloseTargetSlot);
    }

    [Fact]
    public void SingleSideTrigger_ProducesNoDualDropSignal()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 5, maxBuy: 3, maxSell: 3);
        var config = Config(confirm: -5, open: -3);

        // Chỉ có GapBuy → chỉ một nhánh chạy được.
        coordinator.ProcessSnapshot(BuyOnlySnapshot(0), config);
        coordinator.ProcessSnapshot(BuyOnlySnapshot(1), config);
        var result = coordinator.ProcessSnapshot(BuyOnlySnapshot(2), config);

        Assert.NotNull(result.OpenTrigger);
        Assert.False(HasDualDropSignal(result), "Không được có DUAL_SIDE_TRIGGER_DROPPED.");
    }

    [Fact]
    public void PositiveOpenThresholds_NeverProduceDualDrop()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 5, maxBuy: 3, maxSell: 3);
        var config = Config(confirm: 5, open: 3);

        for (var second = 0; second < 5; second++)
        {
            var result = coordinator.ProcessSnapshot(Snapshot(second), config);
            Assert.False(HasDualDropSignal(result), $"Tick {second} không được có DUAL_SIDE_TRIGGER_DROPPED.");
        }
    }

    private static bool HasDualDropSignal(PortfolioSnapshotResult result)
        => result.BlockedSignals is not null
            && result.BlockedSignals.Any(
                b => b.BlockReason.StartsWith("DUAL_SIDE_TRIGGER_DROPPED", StringComparison.Ordinal));

    [Fact]
    public void PositiveOpenThresholds_NeverEmitBothSides()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 5, maxBuy: 3, maxSell: 3);
        var config = Config(confirm: 5, open: 3);

        for (var second = 0; second < 5; second++)
        {
            Assert.Null(coordinator.ProcessSnapshot(Snapshot(second), config).OpenTrigger);
        }
    }
}
