using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.Portfolio;

public sealed class OppositeOpenPriceGuardTests
{
    private static TradeSharedRecord Trade(ulong ticket, int type, double price, ulong time)
        => new(ticket, "XAUUSD", type, 1, price, 0, 0, 0, time, time);

    [Fact]
    public void FirstOpen_SkipsGuard()
    {
        var result = OppositeOpenPriceGuard.Evaluate([], new HashSet<ulong>(), 0, 100, 101, 100, 20);

        Assert.True(result.Allowed);
        Assert.True(result.Skipped);
        Assert.Equal("FIRST_OPEN", result.ReasonCode);
    }

    [Fact]
    public void SameSideOpen_SkipsGuard()
    {
        var trades = new[] { Trade(1, 0, 100, 10) };
        var result = OppositeOpenPriceGuard.Evaluate(trades, new HashSet<ulong> { 1 }, 0, 101, 102, 100, 20);

        Assert.True(result.Allowed);
        Assert.True(result.Skipped);
        Assert.Equal("SAME_SIDE_OPEN", result.ReasonCode);
    }

    [Theory]
    [InlineData(100.42, false, 12)]
    [InlineData(100.55, true, 25)]
    public void BuyToSell_UsesAverageBuyAndCurrentBid(double bid, bool allowed, int expectedDistance)
    {
        var trades = new[]
        {
            Trade(1, 0, 100.10, 10),
            Trade(2, 0, 100.30, 20),
            Trade(3, 0, 100.50, 30)
        };
        var result = OppositeOpenPriceGuard.Evaluate(
            trades, new HashSet<ulong> { 1, 2, 3 }, requestedTradeType: 1,
            currentBidA: (decimal)bid, currentAskA: 999, pointMultiplier: 100, requiredDistancePts: 20);

        Assert.Equal(allowed, result.Allowed);
        Assert.Equal(expectedDistance, result.DistancePts);
        Assert.Equal(100.30, result.AverageOpenPriceA!.Value, 5);
        Assert.Equal("BID", result.CurrentPriceType);
    }

    [Theory]
    [InlineData(100.82, false, 11)]
    [InlineData(100.65, true, 28)]
    public void SellToBuy_UsesAverageSellAndCurrentAsk(double ask, bool allowed, int expectedDistance)
    {
        var trades = new[]
        {
            Trade(1, 1, 101.10, 10),
            Trade(2, 1, 100.90, 20),
            Trade(3, 1, 100.80, 30)
        };
        var result = OppositeOpenPriceGuard.Evaluate(
            trades, new HashSet<ulong> { 1, 2, 3 }, requestedTradeType: 0,
            currentBidA: 999, currentAskA: (decimal)ask, pointMultiplier: 100, requiredDistancePts: 20);

        Assert.Equal(allowed, result.Allowed);
        Assert.Equal(expectedDistance, result.DistancePts);
        Assert.Equal("ASK", result.CurrentPriceType);
    }

    [Theory]
    [InlineData(98.50, 1, true, -150)]
    [InlineData(98.40, 1, true, -160)]
    [InlineData(98.501, 1, false, -150)]
    [InlineData(101.50, 0, true, -150)]
    [InlineData(101.60, 0, true, -160)]
    [InlineData(101.499, 0, false, -150)]
    public void OppositeDirectionDistance_UsesAbsolutePointDistance(
        double currentPrice,
        int requestedTradeType,
        bool allowed,
        int expectedDistance)
    {
        var existingTradeType = requestedTradeType == 1 ? 0 : 1;
        var trades = new[] { Trade(1, existingTradeType, 100, 10) };
        var result = OppositeOpenPriceGuard.Evaluate(
            trades,
            new HashSet<ulong> { 1 },
            requestedTradeType,
            currentBidA: requestedTradeType == 1 ? (decimal)currentPrice : 999,
            currentAskA: requestedTradeType == 0 ? (decimal)currentPrice : 999,
            pointMultiplier: 100,
            requiredDistancePts: 150);

        Assert.Equal(allowed, result.Allowed);
        Assert.Equal(expectedDistance, result.DistancePts);
    }

    [Fact]
    public void MixedBook_UsesSideOfLatestTradeOnly()
    {
        var trades = new[]
        {
            Trade(1, 0, 100, 10),
            Trade(2, 1, 110, 20),
            Trade(3, 1, 112, 30)
        };
        var result = OppositeOpenPriceGuard.Evaluate(
            trades, new HashSet<ulong> { 1, 2, 3 }, requestedTradeType: 0,
            currentBidA: 0, currentAskA: 110.70m, pointMultiplier: 100, requiredDistancePts: 20);

        Assert.True(result.Allowed);
        Assert.Equal(1, result.LastTradeType);
        Assert.Equal(111, result.AverageOpenPriceA);
        Assert.Equal(30, result.DistancePts);
        Assert.Equal(2, result.PositionCount);
    }

    [Fact]
    public void MissingCurrentPrice_BlocksOppositeOpen()
    {
        var trades = new[] { Trade(1, 0, 100, 10) };
        var result = OppositeOpenPriceGuard.Evaluate(
            trades, new HashSet<ulong> { 1 }, requestedTradeType: 1,
            currentBidA: null, currentAskA: 101, pointMultiplier: 100, requiredDistancePts: 20);

        Assert.False(result.Allowed);
        Assert.Equal("CURRENT_PRICE_A_MISSING", result.ReasonCode);
    }

    [Fact]
    public void ZeroThreshold_DisablesGuard()
    {
        var result = OppositeOpenPriceGuard.Evaluate([], new HashSet<ulong>(), 1, null, null, 100, 0);

        Assert.True(result.Allowed);
        Assert.True(result.Skipped);
        Assert.Equal("GUARD_DISABLED", result.ReasonCode);
    }
}
