using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Self-heal watchdog: quyết định resync vs pause khi invariant violation.
// Bug gốc: coordinator under-count (toolOverTracking) → pause vĩnh viễn dù trạng thái vật lý hợp lệ.
// Self-heal rebuild slot từ MMF khi pair mở thật ≤ cap; vẫn pause cho over-trading thật / close đang chạy.
public sealed class WatchdogSelfHealTests
{
    private const int MaxTotal = 7;
    private const int MaxBuy = 4;
    private const int MaxSell = 4;

    [Fact]
    public void NoViolation_ReturnsNoViolation()
    {
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation: false,
            quotaSideViolation: false,
            autoPairCapViolation: false,
            toolOverTrackingViolation: false,
            coordinatorActiveCount: 2,
            rebuiltCount: 2,
            rebuiltBuyCount: 1,
            rebuiltSellCount: 1,
            maxTotal: MaxTotal,
            maxBuy: MaxBuy,
            maxSell: MaxSell,
            hasOpenOrCloseInFlight: false);

        Assert.Equal(WatchdogAction.NoViolation, action);
    }

    [Fact]
    public void UnderCountDrift_WithinCaps_NotInFlight_ReturnsSelfHeal()
    {
        // 2 pair mở thật trên MMF nhưng coordinator chỉ giữ 1 slot → toolOverTracking.
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation: false,
            quotaSideViolation: false,
            autoPairCapViolation: false,
            toolOverTrackingViolation: true,
            coordinatorActiveCount: 1,
            rebuiltCount: 2,
            rebuiltBuyCount: 1,
            rebuiltSellCount: 1,
            maxTotal: MaxTotal,
            maxBuy: MaxBuy,
            maxSell: MaxSell,
            hasOpenOrCloseInFlight: false);

        Assert.Equal(WatchdogAction.SelfHeal, action);
    }

    [Fact]
    public void GenuineOverTrading_QuotaTotal_ReturnsPause()
    {
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation: true,
            quotaSideViolation: false,
            autoPairCapViolation: false,
            toolOverTrackingViolation: true,
            coordinatorActiveCount: 8,
            rebuiltCount: 8,
            rebuiltBuyCount: 4,
            rebuiltSellCount: 4,
            maxTotal: MaxTotal,
            maxBuy: MaxBuy,
            maxSell: MaxSell,
            hasOpenOrCloseInFlight: false);

        Assert.Equal(WatchdogAction.Pause, action);
    }

    [Fact]
    public void GenuineOverTrading_QuotaSide_ReturnsPause()
    {
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation: false,
            quotaSideViolation: true,
            autoPairCapViolation: false,
            toolOverTrackingViolation: false,
            coordinatorActiveCount: 5,
            rebuiltCount: 5,
            rebuiltBuyCount: 5,
            rebuiltSellCount: 0,
            maxTotal: MaxTotal,
            maxBuy: MaxBuy,
            maxSell: MaxSell,
            hasOpenOrCloseInFlight: false);

        Assert.Equal(WatchdogAction.Pause, action);
    }

    [Fact]
    public void AutoPairCapViolation_ReturnsPause()
    {
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation: false,
            quotaSideViolation: false,
            autoPairCapViolation: true,
            toolOverTrackingViolation: false,
            coordinatorActiveCount: 3,
            rebuiltCount: 3,
            rebuiltBuyCount: 2,
            rebuiltSellCount: 1,
            maxTotal: MaxTotal,
            maxBuy: MaxBuy,
            maxSell: MaxSell,
            hasOpenOrCloseInFlight: false);

        Assert.Equal(WatchdogAction.Pause, action);
    }

    [Fact]
    public void UnderCountDrift_ButCloseInFlight_ReturnsPause()
    {
        // Drift hợp lệ nhưng đang có close/open dang dở → KHÔNG được đè resync.
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation: false,
            quotaSideViolation: false,
            autoPairCapViolation: false,
            toolOverTrackingViolation: true,
            coordinatorActiveCount: 1,
            rebuiltCount: 2,
            rebuiltBuyCount: 1,
            rebuiltSellCount: 1,
            maxTotal: MaxTotal,
            maxBuy: MaxBuy,
            maxSell: MaxSell,
            hasOpenOrCloseInFlight: true);

        Assert.Equal(WatchdogAction.Pause, action);
    }

    [Fact]
    public void ToolOverTracking_ButRebuildExceedsCap_ReturnsPause()
    {
        // Rebuild ra nhiều pair hơn cap → over-trading thật, không hợp thức hoá bằng resync.
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation: false,
            quotaSideViolation: false,
            autoPairCapViolation: false,
            toolOverTrackingViolation: true,
            coordinatorActiveCount: 0,
            rebuiltCount: 8,
            rebuiltBuyCount: 4,
            rebuiltSellCount: 4,
            maxTotal: MaxTotal,
            maxBuy: MaxBuy,
            maxSell: MaxSell,
            hasOpenOrCloseInFlight: false);

        Assert.Equal(WatchdogAction.Pause, action);
    }

    [Fact]
    public void ToolOverTracking_ButRebuildSideExceedsCap_ReturnsPause()
    {
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation: false,
            quotaSideViolation: false,
            autoPairCapViolation: false,
            toolOverTrackingViolation: true,
            coordinatorActiveCount: 1,
            rebuiltCount: 6,
            rebuiltBuyCount: 5,
            rebuiltSellCount: 1,
            maxTotal: MaxTotal,
            maxBuy: MaxBuy,
            maxSell: MaxSell,
            hasOpenOrCloseInFlight: false);

        Assert.Equal(WatchdogAction.Pause, action);
    }

    [Fact]
    public void ToolOverTracking_ButRebuildNotGreaterThanCoordinator_ReturnsPause()
    {
        // Không phải under-count thật (rebuild ≤ coordinator) → không resync.
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation: false,
            quotaSideViolation: false,
            autoPairCapViolation: false,
            toolOverTrackingViolation: true,
            coordinatorActiveCount: 2,
            rebuiltCount: 2,
            rebuiltBuyCount: 1,
            rebuiltSellCount: 1,
            maxTotal: MaxTotal,
            maxBuy: MaxBuy,
            maxSell: MaxSell,
            hasOpenOrCloseInFlight: false);

        Assert.Equal(WatchdogAction.Pause, action);
    }
}
