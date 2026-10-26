using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Tests;

// b_bid_offset_pts / b_ask_offset_pts: offset (point, có thể âm) cộng vào giá B sau khi đổi sang point,
// RIÊNG cho Bid và Ask. Spread giữ giá trị thật của nguồn.
public sealed class BPriceOffsetApplierTests
{
    [Fact]
    public void Apply_ZeroOffsets_ReturnsSameInstance()
    {
        var sanB = CreateExchange(bid: 2650.45m, ask: 2650.61m);

        Assert.Same(sanB, BPriceOffsetApplier.Apply(sanB, 0, 0, 100));
    }

    [Theory]
    [InlineData(20, -10, 2650.65, 2650.51)]
    [InlineData(-7, 3, 2650.38, 2650.64)]
    [InlineData(5, 0, 2650.50, 2650.61)]
    [InlineData(0, -5, 2650.45, 2650.56)]
    public void Apply_ShiftsBidAndAskIndependently(int bidOffset, int askOffset, double expectedBid, double expectedAsk)
    {
        var sanB = CreateExchange(bid: 2650.45m, ask: 2650.61m);

        var result = BPriceOffsetApplier.Apply(sanB, bidOffset, askOffset, 100);

        Assert.Equal((decimal)expectedBid, result.Bid);
        Assert.Equal((decimal)expectedAsk, result.Ask);
    }

    [Fact]
    public void Apply_KeepsSpreadAndOtherFieldsUnchanged()
    {
        var sanB = CreateExchange(bid: 2650.45m, ask: 2650.61m);

        var result = BPriceOffsetApplier.Apply(sanB, 20, -10, 100);

        Assert.Equal(sanB.Spread, result.Spread);
        Assert.Equal(sanB with { Bid = result.Bid, Ask = result.Ask }, result);
    }

    [Fact]
    public void Apply_NullPrices_StayNull()
    {
        var sanB = CreateExchange(bid: 2650.45m, ask: 2650.61m) with { Bid = null, Ask = null, IsConnected = false };

        var result = BPriceOffsetApplier.Apply(sanB, 20, -10, 100);

        Assert.Null(result.Bid);
        Assert.Null(result.Ask);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Apply_InvalidPoint_TreatedAsOne(int point)
    {
        var sanB = CreateExchange(bid: 100m, ask: 101m);

        var result = BPriceOffsetApplier.Apply(sanB, 2, -3, point);

        Assert.Equal(102m, result.Bid);
        Assert.Equal(98m, result.Ask);
    }

    [Theory]
    [InlineData(100, 20, -10)]
    [InlineData(100, -35, 17)]
    [InlineData(1000, 7, -250)]
    public void GapCalculator_AfterApply_EqualsRawGapPlusOffset(int point, int bidOffset, int askOffset)
    {
        var calculator = new GapCalculator(new StubRuntimeConfigProvider(point));
        var sanA = CreateExchange(bid: 2650.123m, ask: 2650.287m);
        var sanB = CreateExchange(bid: 2650.456m, ask: 2650.619m);

        var raw = calculator.Calculate(sanA, sanB);
        var shifted = calculator.Calculate(sanA, BPriceOffsetApplier.Apply(sanB, bidOffset, askOffset, point));

        Assert.Equal(raw.GapBuy + bidOffset, shifted.GapBuy);
        Assert.Equal(raw.GapSell + askOffset, shifted.GapSell);
    }

    private static ExchangeMetrics CreateExchange(decimal bid, decimal ask)
        => new(
            Symbol: "XAUUSD",
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
        public int CurrentClosePts => 0;
        public int CurrentCloseConfirmGapPts => 0;
        public double CurrentCloseTpProfit => 0;
        public double CurrentCloseConfirmTpProfit => 0;
        public int CurrentStartTimeHold => 0;
        public int CurrentEndTimeHold => 0;
        public int CurrentConfirmLatencyMs => 0;
        public int CurrentMaxGap => 0;
        public int CurrentLimitMaxGap => 0;
        public double CurrentLimitMaxTp => 0;
        public double CurrentSosTriggerAOpenDistancePts => 0;
        public int CurrentSosTriggerAfterSeconds => 0;
        public int CurrentSosCloseConfirmGapPts => 0;
        public int CurrentSosCloseGapPts => 0;
        public int CurrentMaxSpread => 0;
        public int CurrentOpenPendingTimeMs => 0;
        public int CurrentClosePendingTimeMs => 0;
        public int CurrentDelayOpenAMs => 0;
        public int CurrentDelayOpenBMs => 0;
        public int CurrentDelayCloseAMs => 0;
        public int CurrentDelayCloseBMs => 0;
        public int CurrentOpenNumberOfQualifyingTimes => 1;
        public int CurrentCloseNumberOfQualifyingTimes => 1;
        public int CurrentOppositeOpenMinDistancePts => 0;
        public int CurrentOpenPriceFreezeMs => 0;
        public int CurrentClosePriceFreezeMs => 0;
        public string CurrentMapName1 => "A";
        public string CurrentMapName2 => "B";
        public DashboardMetrics? CurrentDashboardMetrics => null;
    }
}
