using TradeDesktop.Application.Services.PrimeXbt;
using TradeDesktop.Infrastructure.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

public sealed class PrimeXbtPositionCacheTests
{
    private const string Symbol = "XAU/USD";

    private static PrimeXbtPositionsSnapshot Snap(string key)
        => PrimeXbtPositionsParser.Parse(PrimeXbtFixtures.Body("account-positions.json", key), Symbol);

    [Fact]
    public void NewCache_IsNotSynced()
    {
        var cache = new PrimeXbtPositionCache(() => 1);

        Assert.False(cache.Synced);
        Assert.Equal(0UL, cache.Version);
    }

    [Fact]
    public void EmptySnapshot_SyncsWithZeroRecords()
    {
        var cache = new PrimeXbtPositionCache(() => 1);

        cache.Apply(Snap("positions_response_empty"));

        Assert.True(cache.Synced);
        Assert.Equal("HEDGE", cache.PositionMode);
        Assert.Empty(cache.ToTradeRecords(Symbol, 100m));
    }

    [Fact]
    public void HedgeSnapshot_GivesTwoRecords_FromSubPositions_NeverTheAggregate()
    {
        var cache = new PrimeXbtPositionCache(() => 5_000);

        Assert.True(cache.Apply(Snap("positions_event_hedge_two_subs")));
        var records = cache.ToTradeRecords(Symbol, 100m);

        Assert.Equal(2, records.Count);
        var buy = Assert.Single(records, r => r.TradeType == 0);
        var sell = Assert.Single(records, r => r.TradeType == 1);
        Assert.True(PrimeXbtTicketCodec.TryDecode(buy.Ticket, out var buyId));
        Assert.Equal(10680072L, buyId);
        Assert.True(PrimeXbtTicketCodec.TryDecode(sell.Ticket, out var sellId));
        Assert.Equal(10680080L, sellId);
        Assert.Equal(0.0001, buy.Lot, 10); // 0.01 oz / 100
        Assert.Equal(4179.61, buy.Price, 6);
        Assert.Equal(Symbol, buy.Symbol);
        Assert.Equal(0, buy.Profit);
        Assert.Equal((ulong)DateTimeOffset.Parse("2026-10-09T02:57:39.202Z").ToUnixTimeMilliseconds(), buy.TimeMsc);
        Assert.Equal(5_000UL, buy.OpenEaTimeLocal);
        Assert.DoesNotContain(records, r => PrimeXbtTicketCodec.TryDecode(r.Ticket, out var id) && id == 0);
    }

    [Fact]
    public void SameSnapshotAgain_DoesNotBumpVersion()
    {
        var cache = new PrimeXbtPositionCache(() => 1);
        cache.Apply(Snap("positions_event_one_buy"));
        var version = cache.Version;

        Assert.False(cache.Apply(Snap("positions_event_one_buy")));
        Assert.Equal(version, cache.Version);
    }

    [Fact]
    public void UplOnlyChange_DoesNotBumpVersion_RecordsCarryNoProfit()
    {
        var cache = new PrimeXbtPositionCache(() => 1);
        var original = Snap("positions_event_one_buy");
        cache.Apply(original);
        var version = cache.Version;

        var moved = original with { Positions = original.Positions.Select(p => p with { Upl = 12.34m }).ToList() };

        Assert.False(cache.Apply(moved));
        Assert.Equal(version, cache.Version);
    }

    [Fact]
    public void OpenThenHedgeThenCloseBuy_VersionsAdvance_StampKeptForSurvivor()
    {
        var tick = 1_000L;
        var cache = new PrimeXbtPositionCache(() => tick);

        cache.Apply(Snap("positions_event_one_buy"));
        var v1 = cache.Version;
        tick = 2_000;
        cache.Apply(Snap("positions_event_hedge_two_subs"));
        var v2 = cache.Version;
        var hedge = cache.ToTradeRecords(Symbol, 100m);
        Assert.Equal(1_000UL, hedge.Single(r => r.TradeType == 0).OpenEaTimeLocal); // Buy giữ stamp lần đầu
        Assert.Equal(2_000UL, hedge.Single(r => r.TradeType == 1).OpenEaTimeLocal);

        var onlySell = Snap("positions_event_hedge_two_subs");
        onlySell = onlySell with { Positions = onlySell.Positions.Where(p => p.Side == PrimeXbtSide.Sell).ToList() };
        tick = 3_000;
        cache.Apply(onlySell);

        Assert.True(v2 > v1);
        Assert.True(cache.Version > v2);
        var survivor = Assert.Single(cache.ToTradeRecords(Symbol, 100m));
        Assert.Equal(1, survivor.TradeType);
        Assert.Equal(2_000UL, survivor.OpenEaTimeLocal);
    }

    [Fact]
    public void InvalidSnapshot_Unsyncs_KeepsContent()
    {
        var cache = new PrimeXbtPositionCache(() => 1);
        cache.Apply(Snap("positions_event_one_buy"));
        var version = cache.Version;

        var bad = PrimeXbtPositionsParser.Parse(PrimeXbtFixtures.Json("""{"data":[{"id":0,"symbol":"XAU/USD","qty":0.01}]}"""), Symbol);
        cache.Apply(bad);

        Assert.False(bad.IsValid);
        Assert.False(cache.Synced);
        Assert.Equal(1, cache.Count);
        Assert.Equal(version, cache.Version);
    }

    [Fact]
    public void MarkUnsynced_ThenValidSnapshot_Resyncs()
    {
        var cache = new PrimeXbtPositionCache(() => 1);
        cache.Apply(Snap("positions_event_one_buy"));

        cache.MarkUnsynced();
        Assert.False(cache.Synced);
        Assert.False(cache.Apply(Snap("positions_event_one_buy")));
        Assert.True(cache.Synced);
    }

    [Fact]
    public void NettingSnapshot_IsRecordedAsMode()
    {
        var cache = new PrimeXbtPositionCache(() => 1);
        var netting = Snap("positions_response_empty") with { PositionMode = "NETTING" };

        cache.Apply(netting);

        Assert.Equal("NETTING", cache.PositionMode);
    }

    [Fact]
    public void TryGet_ReturnsOpenSubPosition()
    {
        var cache = new PrimeXbtPositionCache(() => 1);
        cache.Apply(Snap("positions_event_hedge_two_subs"));

        Assert.Equal(PrimeXbtSide.Sell, cache.TryGet(10680080)!.Side);
        Assert.Null(cache.TryGet(1));
    }
}
