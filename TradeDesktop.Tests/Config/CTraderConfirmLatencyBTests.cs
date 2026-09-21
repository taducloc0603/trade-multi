using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Tests.Config;

// Task R8-B — ngưỡng LATENCY riêng cho chân B (`ctrader_confirm_latency_b`, chỉ khi platform_b = ctrader).
// null = dùng chung `confirm_latency_ms` (hành vi trước task này, phải giữ nguyên cho cặp MT-MT).
// Guard (SignalEntryGuard) và router (TradeExecutionRouter) phải cho cùng kết luận — router re-check sau mutex.
public sealed class CTraderConfirmLatencyBTests
{
    private static ConfigLoadResult BuildResult(int? confirmLatencyMsB)
        => ConfigLoadResult.Success(
            machineHostName: "host",
            mapName1: "A",
            mapName2: "B",
            manualHwndColumns: null,
            platformA: "mt5",
            platformB: "ctrader",
            point: 100,
            openPts: 1,
            confirmGapPts: 0,
            holdConfirmMs: 0, openPriceFreezeMs: 0,
            closePts: 1,
            closeConfirmGapPts: 0,
            closeTpProfit: 1,
            closeConfirmTpProfit: 0,
            closeMaxTpProfit: 35,
            closeHoldConfirmMs: 0, closePriceFreezeMs: 0,
            startTimeHold: 5,
            endTimeHold: 15,
            configId: "id",
            sansJson: "{}",
            confirmLatencyMs: 100,
            ctraderConfirmLatencyB: confirmLatencyMsB);

    private static DashboardMetrics Metrics(decimal latencyA, decimal latencyB)
    {
        ExchangeDashboardMetrics Leg(decimal latency)
            => new("XAUUSD", 4300m, 4300.1m, 0.1m, latency, 5f, "t", latency, latency, true, null);

        return new DashboardMetrics(
            ExchangeA: Leg(latencyA),
            ExchangeB: Leg(latencyB),
            GapBuy: 0,
            GapSell: 0,
            IsConnectedA: true,
            IsConnectedB: true,
            TimestampUtc: DateTime.UtcNow);
    }

    private static SignalEntryGuard.GuardResult CheckLatency(int confirmLatencyMs, int? confirmLatencyMsB, decimal latA, decimal latB)
        => SignalEntryGuard.Check(
            new GapSignalTriggerResult(
                Triggered: true,
                Action: GapSignalAction.Open,
                TriggerType: GapSignalTriggerType.OpenByGapBuy,
                PrimarySide: GapSignalSide.Buy,
                BuyGaps: [],
                SellGaps: [],
                LastBuyGap: 10,
                LastSellGap: null,
                TriggeredAtUtc: DateTime.UtcNow,
                LastABid: 4300m,
                LastAAsk: 4300.1m,
                LastBBid: 4300.2m,
                LastBAsk: 4300.3m,
                GapBuySourceBBid: 4300.2m,
                GapBuySourceAAsk: 4300.1m,
                GapSellSourceBAsk: null,
                GapSellSourceABid: null,
                PointMultiplier: 100),
            Metrics(latA, latB),
            new SignalEntryGuard.GuardConfig(
                ConfirmLatencyMs: confirmLatencyMs,
                MaxGap: 0,
                MaxSpread: 0,
                PointMultiplier: 100,
                ConfirmLatencyMsB: confirmLatencyMsB ?? -1),
            new Queue<SignalEntryGuard.PriceHistoryEntry>(),
            priceFreezeMs: 0);

    [Fact]
    public void ConfigLoadResult_KeepsNullAndValue_NoClamp()
    {
        Assert.Null(BuildResult(null).CTraderConfirmLatencyB);
        Assert.Equal(3000, BuildResult(3000).CTraderConfirmLatencyB);
        Assert.Equal(0, BuildResult(0).CTraderConfirmLatencyB);
    }

    // `RuntimeConfigState` nằm ở TradeDesktop.App mà test project KHÔNG reference (CLAUDE.md §3), nên bẫy
    // sentinel (-1 = giữ nguyên) chỉ khoá được ở mức quy ước + review. Ở đây khoá phần thuần: hàm resolve.
    [Theory]
    [InlineData(100, -1, 100)]   // không cấu hình → dùng chung
    [InlineData(100, 3000, 3000)]
    [InlineData(100, 0, 0)]      // 0 = tắt riêng cho B
    [InlineData(0, 3000, 3000)]
    public void ResolveLatencyLimitB_PicksOwnThresholdOrShared(int shared, int ownOrMinusOne, int expected)
        => Assert.Equal(expected, SignalEntryGuard.ResolveLatencyLimitB(shared, ownOrMinusOne));

    [Fact]
    public void Guard_NullMeansShared_OldBehaviourUnchanged()
    {
        Assert.True(CheckLatency(100, null, latA: 50, latB: 50).CanTrade);

        var blocked = CheckLatency(100, null, latA: 50, latB: 500);
        Assert.False(blocked.CanTrade);
        Assert.Contains("sàn B", blocked.SkipReason);
        Assert.Contains("confirm_latency=100", blocked.SkipReason);
    }

    [Fact]
    public void Guard_SeparateThresholds_EachLegUsesItsOwn()
    {
        // B được nới tới 3000 ms: 2500 ms qua được, còn A vẫn bị chặn ở 100 ms.
        Assert.True(CheckLatency(100, 3000, latA: 50, latB: 2500).CanTrade);

        var blockedB = CheckLatency(100, 3000, latA: 50, latB: 3500);
        Assert.False(blockedB.CanTrade);
        Assert.Contains("confirm_latency_b=3000", blockedB.SkipReason);

        var blockedA = CheckLatency(100, 3000, latA: 150, latB: 50);
        Assert.False(blockedA.CanTrade);
        Assert.Contains("sàn A", blockedA.SkipReason);
    }

    [Fact]
    public void Guard_ZeroDisablesOnlyThatLeg()
    {
        // Ngưỡng B = 0 → tắt guard riêng cho B, chân A vẫn bị chặn như cũ.
        Assert.True(CheckLatency(100, 0, latA: 50, latB: 99_999).CanTrade);
        Assert.False(CheckLatency(100, 0, latA: 150, latB: 50).CanTrade);

        // Ngưỡng chung = 0 nhưng B có ngưỡng riêng → chỉ B bị chặn.
        Assert.False(CheckLatency(0, 3000, latA: 99_999, latB: 3500).CanTrade);
        Assert.True(CheckLatency(0, 3000, latA: 99_999, latB: 2000).CanTrade);
    }

    // Router dùng cùng luật (ResolveLatencyLimitB). Khoá việc guard và router phải sửa cùng nhịp.
    [Theory]
    [InlineData(100, null, 50, 50, true)]
    [InlineData(100, null, 50, 500, false)]
    [InlineData(100, 3000, 50, 2500, true)]
    [InlineData(100, 3000, 50, 3500, false)]
    [InlineData(100, 3000, 150, 50, false)]
    [InlineData(100, 0, 50, 99999, true)]
    public void RouterRule_MatchesGuard(int confirmLatencyMs, int? confirmLatencyMsB, int latA, int latB, bool expectedAllowed)
    {
        var limitB = SignalEntryGuard.ResolveLatencyLimitB(confirmLatencyMs, confirmLatencyMsB ?? -1);
        var routerAllowed = !((confirmLatencyMs > 0 && latA > confirmLatencyMs) || (limitB > 0 && latB > limitB));

        Assert.Equal(expectedAllowed, routerAllowed);
        Assert.Equal(CheckLatency(confirmLatencyMs, confirmLatencyMsB, latA, latB).CanTrade, routerAllowed);
    }
}
