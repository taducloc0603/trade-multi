using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Tests;

public sealed class GapCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsExpectedGapSigns_ForBuyAndSellContracts()
    {
        var sut = new GapCalculator(new StubRuntimeConfigProvider(point: 100));

        // BUY opportunity: SanB.Bid > SanA.Ask => positive buy gap
        // SELL opportunity: SanB.Ask > SanA.Bid => positive sell gap
        var sanA = CreateExchange(bid: 100.00m, ask: 100.01m);
        var sanB = CreateExchange(bid: 100.10m, ask: 100.11m);

        var (gapBuy, gapSell) = sut.Calculate(sanA, sanB);

        Assert.Equal(9, gapBuy);
        Assert.Equal(11, gapSell);
    }

    [Fact]
    public void Calculate_UsesPointMultiplier_AndDefaultsToOneWhenInvalid()
    {
        var sanA = CreateExchange(bid: 100.00m, ask: 100.02m);
        var sanB = CreateExchange(bid: 100.04m, ask: 100.06m);

        var sutWithPoint = new GapCalculator(new StubRuntimeConfigProvider(point: 100));
        var sutFallback = new GapCalculator(new StubRuntimeConfigProvider(point: 0));

        var withPoint = sutWithPoint.Calculate(sanA, sanB);
        var fallback = sutFallback.Calculate(sanA, sanB);

        Assert.Equal(2, withPoint.GapBuy);
        Assert.Equal(6, withPoint.GapSell);

        // (100.04 - 100.02) * 1 = 0.02 -> int cast => 0
        // (100.06 - 100.00) * 1 = 0.06 -> int cast => 0
        Assert.Equal(0, fallback.GapBuy);
        Assert.Equal(0, fallback.GapSell);
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
        public int CurrentCloseHoldConfirmMs => 0;
        public int CurrentStartTimeHold => 0;
        public int CurrentEndTimeHold => 0;
        public int CurrentStartWaitTime => 0;
        public int CurrentEndWaitTime => 0;
        public int CurrentConfirmLatencyMs => 0;
        public int CurrentMaxGap => 0;
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
        public string CurrentMapName1 => "A";
        public string CurrentMapName2 => "B";
        public DashboardMetrics? CurrentDashboardMetrics => null;
    }
}