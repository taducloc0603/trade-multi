using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Application.Services.PrimeXbt;
using TradeDesktop.Application.Services.TradeLocker;

namespace TradeDesktop.Tests.TradeLocker;

// Phase 1 (docs/plans/tradelocker): luật nhận diện platform/kênh map của TradeLocker, tách bạch với cTrader/PrimeXBT.
public sealed class TradeLockerRoutingRulesTests
{
    [Fact]
    public void ChannelAndMapNames_AreFixedAndDistinctFromOtherPlatforms()
    {
        Assert.Equal("TRADELOCKER_B", TradeLockerRoutingRules.ChannelMapName);
        Assert.Equal("TRADELOCKER_B_Trades", TradeLockerRoutingRules.TradeMapName);
        Assert.Equal("TRADELOCKER_B_History", TradeLockerRoutingRules.HistoryMapName);
        Assert.NotEqual(CTraderRoutingRules.TradeMapName, TradeLockerRoutingRules.TradeMapName);
        Assert.NotEqual(PrimeXbtRoutingRules.TradeMapName, TradeLockerRoutingRules.TradeMapName);
        Assert.NotEqual(PrimeXbtRoutingRules.HistoryMapName, TradeLockerRoutingRules.HistoryMapName);
    }

    [Theory]
    [InlineData("tradelocker", true)]
    [InlineData("TRADELOCKER", true)]
    [InlineData(" TradeLocker ", true)]
    [InlineData("trade-locker", false)]
    [InlineData("primexbt", false)]
    [InlineData("ctrader", false)]
    [InlineData("mt5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTradeLockerPlatform_MatchesOnlyTradeLocker(string? platform, bool expected)
    {
        Assert.Equal(expected, TradeLockerRoutingRules.IsTradeLockerPlatform(platform));
    }

    [Fact]
    public void TradeLocker_IsNotRecognisedByOtherPlatformRules()
    {
        Assert.False(CTraderRoutingRules.IsCTraderPlatform("tradelocker"));
        Assert.False(PrimeXbtRoutingRules.IsPrimeXbtPlatform("tradelocker"));
    }

    [Theory]
    [InlineData("tradelocker", "TRADELOCKER_B_Trades", true)]
    [InlineData("tradelocker", " TRADELOCKER_B_Trades ", true)]
    [InlineData("tradelocker", "tradelocker_b_trades", false)]
    [InlineData("tradelocker", "TRADELOCKER_B_History", false)]
    [InlineData("tradelocker", "PRIMEXBT_B_Trades", false)]
    [InlineData("primexbt", "TRADELOCKER_B_Trades", false)]
    [InlineData("mt5", "TRADELOCKER_B_Trades", false)]
    [InlineData("tradelocker", null, false)]
    [InlineData("tradelocker", "", false)]
    public void IsTradeLockerTradeMap_RequiresPlatformAndExactMapName(string? platformB, string? mapName, bool expected)
    {
        Assert.Equal(expected, TradeLockerRoutingRules.IsTradeLockerTradeMap(platformB, mapName));
    }

    [Theory]
    [InlineData("tradelocker", "TRADELOCKER_B_History", true)]
    [InlineData("tradelocker", "TRADELOCKER_B_Trades", false)]
    [InlineData("ctrader", "TRADELOCKER_B_History", false)]
    [InlineData("tradelocker", "  ", false)]
    public void IsTradeLockerHistoryMap_RequiresPlatformAndExactMapName(string? platformB, string? mapName, bool expected)
    {
        Assert.Equal(expected, TradeLockerRoutingRules.IsTradeLockerHistoryMap(platformB, mapName));
    }
}
