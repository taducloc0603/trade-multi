using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Rule C — 2 lock độc lập, config từ DB:
//  - opposite_side_lock_seconds (default 300): sau OPEN, block opposite-side OPEN;
//    same-side OPEN refreshes timer; CLOSE unaffected.
//  - post_close_lock_seconds (default 300): sau CLOSE confirm, block MỌI OPEN (cả 2 chiều);
//    CLOSE unaffected.
public sealed class OppositeSideLockTests
{
    private static PortfolioCoordinator CreateCoordinator()
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(42));

    private static GapSignalTriggerResult Trigger(GapSignalSide side)
        => new(true, GapSignalAction.Open,
            side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            side,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    [Fact]
    public void OppositeSideLockSeconds_DefaultsTo300()
    {
        Assert.Equal(300, PortfolioCoordinator.DefaultOppositeSideLockSeconds);
        Assert.Equal(300, CreateCoordinator().OppositeSideLockSeconds);
    }

    [Fact]
    public void OppositeSideLockSeconds_OverriddenFromConfig()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateOppositeSideLockConfig(60);
        Assert.Equal(60, coordinator.OppositeSideLockSeconds);

        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        // -90s > 60s window → opposite lock đã hết hạn theo config mới.
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow.AddSeconds(-90));

        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out _));
    }

    [Fact]
    public void OppositeSideLockConfig_ZeroOrNegative_KeepsDefault()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateOppositeSideLockConfig(0);
        Assert.Equal(300, coordinator.OppositeSideLockSeconds);
        coordinator.UpdateOppositeSideLockConfig(-5);
        Assert.Equal(300, coordinator.OppositeSideLockSeconds);
    }

    [Fact]
    public void OpenBuy_BlocksOpenSellFor300Seconds()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
        Assert.Contains("last=Buy", reason);
    }

    [Fact]
    public void OpenSell_BlocksOpenBuyFor300Seconds()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-sell", Trigger(GapSignalSide.Sell));
        coordinator.MarkSlotOpenConfirmed("p-sell", 1, 2, DateTime.UtcNow);

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
        Assert.Contains("last=Sell", reason);
    }

    [Fact]
    public void SameSideOpen_NotBlockedByOppositeLock()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);

        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }

    [Fact]
    public void SameSideOpen_RefreshesLockTimerForOpposite()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        coordinator.AllocatePendingOpenSlot("p1", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p1", 1, 2, DateTime.UtcNow.AddSeconds(-200));

        coordinator.AllocatePendingOpenSlot("p2", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p2", 3, 4, DateTime.UtcNow); // refreshes

        // Sell now should still be blocked — lock window moved forward.
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
    }

    [Fact]
    public void OppositeLock_AfterLockExpired_AllowsOpposite()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow.AddSeconds(-400));

        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out _));
    }

    [Fact]
    public void OppositeLock_DoesNotBlockClose()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);

        Assert.True(coordinator.CanCloseNow(out _));
    }

    [Fact]
    public void OppositeLock_StateRetained_AfterAllSlotsClosed()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p-buy", DateTime.UtcNow);
        coordinator.MarkSlotCloseConfirmed("p-buy", DateTime.UtcNow);

        // Slot fully closed, but LastOpenConfirmedSide=Buy retained → Sell still blocked.
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
    }

    // ===== Rule C (post-close) — sau CLOSE confirm khoá MỌI open (cả 2 chiều) =====

    [Fact]
    public void PostClose_BlocksSameSideOpen()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p-buy", DateTime.UtcNow);
        coordinator.MarkSlotCloseConfirmed("p-buy", DateTime.UtcNow);

        // Same-side (Buy) không bị opposite-lock, nhưng bị post-close lock chặn.
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out var reason));
        Assert.Contains("POST_CLOSE_LOCK", reason);
    }

    [Fact]
    public void PostClose_BlocksBothSides()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p-buy", DateTime.UtcNow);
        coordinator.MarkSlotCloseConfirmed("p-buy", DateTime.UtcNow);

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out _));
    }

    [Fact]
    public void PostClose_DoesNotBlockClose()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, DateTime.UtcNow);
        coordinator.MarkSlotCloseTriggered("p-buy", DateTime.UtcNow);
        coordinator.MarkSlotCloseConfirmed("p-buy", DateTime.UtcNow);

        Assert.True(coordinator.CanCloseNow(out _));
    }

    [Fact]
    public void PostClose_AfterWindowExpired_AllowsOpen()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        coordinator.UpdatePostCloseLockConfig(60);

        var t0 = DateTime.UtcNow.AddSeconds(-120);
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, t0);
        coordinator.MarkSlotCloseTriggered("p-buy", t0);
        coordinator.MarkSlotCloseConfirmed("p-buy", t0);

        // Reopen same-side Buy: 120s > 60s post-close window → mở lại được.
        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
    }

    [Fact]
    public void PostCloseLockSeconds_DefaultsTo300()
    {
        Assert.Equal(300, PortfolioCoordinator.DefaultPostCloseLockSeconds);
        Assert.Equal(300, CreateCoordinator().PostCloseLockSeconds);
    }

    [Fact]
    public void PostCloseLockConfig_OverrideAndZeroKeepsDefault()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdatePostCloseLockConfig(45);
        Assert.Equal(45, coordinator.PostCloseLockSeconds);
        coordinator.UpdatePostCloseLockConfig(0);
        Assert.Equal(300, coordinator.PostCloseLockSeconds);
        coordinator.UpdatePostCloseLockConfig(-9);
        Assert.Equal(300, coordinator.PostCloseLockSeconds);
    }

    // 2 giá trị độc lập: opposite (sau OPEN) và post-close (sau CLOSE) tách biệt.
    [Fact]
    public void OppositeAndPostClose_UseSeparateValues()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        coordinator.UpdateOppositeSideLockConfig(300); // sau OPEN: dài
        coordinator.UpdatePostCloseLockConfig(30);     // sau CLOSE: ngắn

        var t0 = DateTime.UtcNow.AddSeconds(-50); // 50s: > post-close(30), < opposite(300)
        coordinator.AllocatePendingOpenSlot("p-buy", Trigger(GapSignalSide.Buy));
        coordinator.MarkSlotOpenConfirmed("p-buy", 1, 2, t0);
        coordinator.MarkSlotCloseTriggered("p-buy", t0);
        coordinator.MarkSlotCloseConfirmed("p-buy", t0);

        // Buy (same-side): post-close 30s đã hết → mở được.
        Assert.True(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out _));
        // Sell (opposite): opposite 300s CHƯA hết → vẫn bị chặn bởi OPPOSITE_SIDE_LOCK.
        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Sell, out var reason));
        Assert.Contains("OPPOSITE_SIDE_LOCK", reason);
    }
}
