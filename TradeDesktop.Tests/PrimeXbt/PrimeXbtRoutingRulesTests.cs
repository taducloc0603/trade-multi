using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

// Phase 1 (docs/plans/primexbt): luật nhận diện platform/kênh map của PrimeXBT, tách bạch với cTrader.
public sealed class PrimeXbtRoutingRulesTests
{
    [Fact]
    public void ChannelAndMapNames_AreFixedAndDistinctFromCTrader()
    {
        Assert.Equal("PRIMEXBT_B", PrimeXbtRoutingRules.ChannelMapName);
        Assert.Equal("PRIMEXBT_B_Trades", PrimeXbtRoutingRules.TradeMapName);
        Assert.Equal("PRIMEXBT_B_History", PrimeXbtRoutingRules.HistoryMapName);
        Assert.NotEqual(CTraderRoutingRules.TradeMapName, PrimeXbtRoutingRules.TradeMapName);
        Assert.NotEqual(CTraderRoutingRules.HistoryMapName, PrimeXbtRoutingRules.HistoryMapName);
    }

    [Theory]
    [InlineData("primexbt", true)]
    [InlineData("PRIMEXBT", true)]
    [InlineData(" PrimeXbt ", true)]
    [InlineData("ctrader", false)]
    [InlineData("mt5", false)]
    [InlineData("prime-xbt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPrimeXbtPlatform_MatchesOnlyPrimeXbt(string? platform, bool expected)
    {
        Assert.Equal(expected, PrimeXbtRoutingRules.IsPrimeXbtPlatform(platform));
    }

    [Theory]
    [InlineData("ctrader")]
    [InlineData("mt4")]
    [InlineData("mt5")]
    public void OtherPlatforms_AreNotRecognisedAsPrimeXbt_AndPrimeXbtIsNotCTrader(string other)
    {
        Assert.False(PrimeXbtRoutingRules.IsPrimeXbtPlatform(other));
        Assert.False(CTraderRoutingRules.IsCTraderPlatform("primexbt"));
    }

    [Theory]
    [InlineData("primexbt", "PRIMEXBT_B_Trades", true)]
    [InlineData("primexbt", " PRIMEXBT_B_Trades ", true)]
    [InlineData("primexbt", "primexbt_b_trades", false)]
    [InlineData("primexbt", "PRIMEXBT_B_History", false)]
    [InlineData("primexbt", "CTRADER_B_Trades", false)]
    [InlineData("primexbt", "Local\\MT_B_Trades", false)]
    [InlineData("ctrader", "PRIMEXBT_B_Trades", false)]
    [InlineData("mt5", "PRIMEXBT_B_Trades", false)]
    [InlineData("primexbt", null, false)]
    [InlineData("primexbt", "", false)]
    public void IsPrimeXbtTradeMap_RequiresPlatformAndExactMapName(string? platformB, string? mapName, bool expected)
    {
        Assert.Equal(expected, PrimeXbtRoutingRules.IsPrimeXbtTradeMap(platformB, mapName));
    }

    [Theory]
    [InlineData("primexbt", "PRIMEXBT_B_History", true)]
    [InlineData("primexbt", "PRIMEXBT_B_Trades", false)]
    [InlineData("primexbt", "CTRADER_B_History", false)]
    [InlineData("ctrader", "PRIMEXBT_B_History", false)]
    [InlineData(null, "PRIMEXBT_B_History", false)]
    public void IsPrimeXbtHistoryMap_RequiresPlatformAndExactMapName(string? platformB, string? mapName, bool expected)
    {
        Assert.Equal(expected, PrimeXbtRoutingRules.IsPrimeXbtHistoryMap(platformB, mapName));
    }

    [Fact]
    public void CTraderRules_NeverClaimPrimeXbtMaps_AndViceVersa()
    {
        // B = primexbt: decorator cTrader không được nhận bất kỳ map nào, kể cả map của chính nó.
        Assert.False(CTraderRoutingRules.IsCTraderTradeMap("primexbt", CTraderRoutingRules.TradeMapName));
        Assert.False(CTraderRoutingRules.IsCTraderHistoryMap("primexbt", CTraderRoutingRules.HistoryMapName));
        Assert.False(CTraderRoutingRules.IsCTraderTradeMap("primexbt", PrimeXbtRoutingRules.TradeMapName));
        // B = ctrader: luật PrimeXBT không được nhận bất kỳ map nào.
        Assert.False(PrimeXbtRoutingRules.IsPrimeXbtTradeMap("ctrader", PrimeXbtRoutingRules.TradeMapName));
        Assert.False(PrimeXbtRoutingRules.IsPrimeXbtHistoryMap("ctrader", PrimeXbtRoutingRules.HistoryMapName));
        Assert.False(PrimeXbtRoutingRules.IsPrimeXbtTradeMap("ctrader", CTraderRoutingRules.TradeMapName));
    }
}
