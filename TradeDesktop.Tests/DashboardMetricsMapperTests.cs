using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Tests;

public sealed class DashboardMetricsMapperTests
{
    // A: bid 100.00 / ask 100.01, B: bid 100.10 / ask 100.11, C: bid 100.04 / ask 100.07 (point = 100).
    // Gap A-B (giữ nguyên): Buy = B.Bid - A.Ask = 9, Sell = B.Ask - A.Bid = 11.
    // Gap A-C (cùng-chân, A trừ C): Buy = A.Ask - C.Ask = -6, Sell = A.Bid - C.Bid = -4.
    // Gap B-C (cùng-chân, B trừ C): Buy = B.Ask - C.Ask = 4, Sell = B.Bid - C.Bid = 6.
    [Fact]
    public void Map_WithSanC_ComputesAcAndBcGaps_SameLeg_PrimaryMinusC()
    {
        var mapper = new DashboardMetricsMapper(new GapCalculator(new StubRuntimeConfigProvider(point: 100)));
        var snapshot = new SharedMemorySnapshot(
            SanA: CreateExchange(bid: 100.00m, ask: 100.01m),
            SanB: CreateExchange(bid: 100.10m, ask: 100.11m),
            TimestampUtc: DateTime.UtcNow,
            SanC: CreateExchange(bid: 100.04m, ask: 100.07m));

        var result = mapper.Map(snapshot);

        // A-B không đổi.
        Assert.Equal(9, result.GapBuy);
        Assert.Equal(11, result.GapSell);
        // A-C: Buy = A.Ask - C.Ask / Sell = A.Bid - C.Bid.
        Assert.Equal(-6, result.GapBuyAC);
        Assert.Equal(-4, result.GapSellAC);
        // B-C: Buy = B.Ask - C.Ask / Sell = B.Bid - C.Bid.
        Assert.Equal(4, result.GapBuyBC);
        Assert.Equal(6, result.GapSellBC);
        Assert.NotNull(result.ExchangeC);
        Assert.Equal(100.04m, result.ExchangeC!.Bid);
    }

    [Fact]
    public void Map_WithoutSanC_LeavesExchangeCAndAcBcGapsNull_AndAbUnchanged()
    {
        var mapper = new DashboardMetricsMapper(new GapCalculator(new StubRuntimeConfigProvider(point: 100)));
        var snapshot = new SharedMemorySnapshot(
            SanA: CreateExchange(bid: 100.00m, ask: 100.01m),
            SanB: CreateExchange(bid: 100.10m, ask: 100.11m),
            TimestampUtc: DateTime.UtcNow);

        var result = mapper.Map(snapshot);

        // Hành vi A/B y hệt trước khi có sàn C.
        Assert.Equal(9, result.GapBuy);
        Assert.Equal(11, result.GapSell);
        Assert.Equal(100.00m, result.ExchangeA.Bid);
        Assert.Equal(100.10m, result.ExchangeB.Bid);
        // C chưa cấu hình => null hết.
        Assert.Null(result.ExchangeC);
        Assert.Null(result.GapBuyAC);
        Assert.Null(result.GapSellAC);
        Assert.Null(result.GapBuyBC);
        Assert.Null(result.GapSellBC);
    }

    private static ExchangeMetrics CreateExchange(decimal bid, decimal ask)
        => new(
            Symbol: "BTCUSDT",
            Bid: bid,
            Ask: ask,
            Spread: ask - bid,
            LatencyMs: 1,
            Tps: 1,
            Time: "00:00:00",
            MaxLatMs: 1,
            AvgLatMs: 1,
            IsConnected: true,
            Error: null);

    private sealed class StubRuntimeConfigProvider(int point) : IRuntimeConfigProvider
    {
        public string CurrentMachineHostName => "test";
        public int CurrentPoint => point;
        public int CurrentOpenPts => 0;
        public int CurrentConfirmGapPts => 0;
        public int CurrentHoldConfirmMs => 0;
        public int CurrentClosePts => 0;
        public int CurrentCloseConfirmGapPts => 0;
        public double CurrentCloseTpProfit => 0;
        public double CurrentCloseConfirmTpProfit => 0;
        public int CurrentCloseHoldConfirmMs => 0;
        public int CurrentStartTimeHold => 0;
        public int CurrentEndTimeHold => 0;
        public int CurrentStartWaitTime => 0;
        public int CurrentEndWaitTime => 0;
        public int CurrentConfirmLatencyMs => 0;
        public int CurrentMaxGap => 0;
        public int CurrentLimitMaxGap => 0;
        public double CurrentLimitMaxTp => 0;
        public int CurrentMaxSpread => 0;
        public int CurrentOpenMaxTimesTick => 0;
        public int CurrentCloseMaxTimesTick => 0;
        public int CurrentOpenPendingTimeMs => 0;
        public int CurrentClosePendingTimeMs => 0;
        public int CurrentDelayOpenAMs => 0;
        public int CurrentDelayOpenBMs => 0;
        public int CurrentDelayCloseAMs => 0;
        public int CurrentDelayCloseBMs => 0;
        public int CurrentOpenNumberOfQualifyingTimes => 1;
        public int CurrentCloseNumberOfQualifyingTimes => 1;
        public int CurrentOpenGapTick => 0;
        public int CurrentCloseGapTick => 0;
        public int CurrentCoolDownGapTick => 0;
        public int CurrentOpenPriceFreezeMs => 0;
        public int CurrentClosePriceFreezeMs => 0;
        public string CurrentMapName1 => "A";
        public string CurrentMapName2 => "B";
        public string CurrentMapName3 => "C";
        public DashboardMetrics? CurrentDashboardMetrics => null;
    }
}
