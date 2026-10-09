using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

public sealed class PrimeXbtPositionsParserTests
{
    private const string Symbol = "XAU/USD";

    [Fact]
    public void HedgeBuyAndSell_UnderNettedAggregateRow_GivesTwoPositions()
    {
        // Dòng gộp: side SELL, qty 0 (UI hiện "Neutral"). Đọc nhầm dòng gộp ⇒ tưởng không còn vị thế ⇒ đóng nhầm chân A.
        var snapshot = PrimeXbtPositionsParser.Parse(PrimeXbtFixtures.Body("account-positions.json", "positions_event_hedge_two_subs"), Symbol);

        Assert.True(snapshot.IsValid);
        Assert.True(snapshot.IsHedgeMode);
        Assert.Equal(2, snapshot.Positions.Count);
        var sell = Assert.Single(snapshot.Positions, p => p.Id == 10680080);
        var buy = Assert.Single(snapshot.Positions, p => p.Id == 10680072);
        Assert.Equal(PrimeXbtSide.Sell, sell.Side);
        Assert.Equal(PrimeXbtSide.Buy, buy.Side);
        Assert.Equal(0.01m, buy.Qty);
        Assert.Equal(4179.61m, buy.OpenPrice);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 2, 57, 39, 202, TimeSpan.Zero), buy.OpenTime);
    }

    [Fact]
    public void SingleBuy_UsesSubPositionPriceNotAggregatePrice()
    {
        var snapshot = PrimeXbtPositionsParser.Parse(PrimeXbtFixtures.Body("account-positions.json", "positions_event_one_buy"), Symbol);

        var position = Assert.Single(snapshot.Positions);
        Assert.Equal(4179.61m, position.OpenPrice); // dòng gộp ghi 4179.42 — phải bỏ qua
    }

    [Theory]
    [InlineData("positions_event_flat")]
    [InlineData("positions_response_empty")]
    public void Flat_IsValidAndEmpty(string key)
    {
        var snapshot = PrimeXbtPositionsParser.Parse(PrimeXbtFixtures.Body("account-positions.json", key), Symbol);

        Assert.True(snapshot.IsValid);
        Assert.Empty(snapshot.Positions);
    }

    [Fact]
    public void AggregateWithQtyButNoSubPositions_IsInvalid()
    {
        var body = PrimeXbtFixtures.Json("""{"positionMode":"HEDGE","data":[{"id":0,"symbol":"XAU/USD","side":"BUY","qty":0.01,"subPositions":null}]}""");

        var snapshot = PrimeXbtPositionsParser.Parse(body, Symbol);

        Assert.False(snapshot.IsValid);
        Assert.Empty(snapshot.Positions);
    }

    [Fact]
    public void AggregateWithZeroQtyAndNoSubPositions_IsSkipped()
    {
        var body = PrimeXbtFixtures.Json("""{"positionMode":"HEDGE","data":[{"id":0,"symbol":"XAU/USD","qty":0,"subPositions":null}]}""");

        Assert.True(PrimeXbtPositionsParser.Parse(body, Symbol).IsValid);
    }

    [Fact]
    public void OtherSymbols_AreFiltered()
    {
        var body = PrimeXbtFixtures.Json("""
            {"positionMode":"HEDGE","data":[
              {"id":0,"symbol":"BTC/USD","qty":0.001,"subPositions":[{"id":5,"side":"BUY","qty":0.001,"openPrice":82000,"openTime":"2026-10-09T02:00:00Z","symbol":"BTC/USD"}]},
              {"id":0,"symbol":"XAU/USD","qty":0.01,"subPositions":[{"id":6,"side":"BUY","qty":0.01,"openPrice":4179,"openTime":"2026-10-09T02:00:00Z","symbol":"XAU/USD"}]}]}
            """);

        Assert.Equal(6, Assert.Single(PrimeXbtPositionsParser.Parse(body, Symbol).Positions).Id);
    }

    [Theory]
    [InlineData("""{"id":6,"side":"BUY","qty":0.01,"openPrice":4179,"symbol":"XAU/USD"}""")]                                  // thiếu openTime
    [InlineData("""{"id":6,"side":"HOLD","qty":0.01,"openPrice":4179,"openTime":"2026-10-09T02:00:00Z","symbol":"XAU/USD"}""")] // side lạ
    [InlineData("""{"id":0,"side":"BUY","qty":0.01,"openPrice":4179,"openTime":"2026-10-09T02:00:00Z","symbol":"XAU/USD"}""")]  // id 0
    public void SubPositionMissingRequiredField_InvalidatesSnapshot(string sub)
    {
        var body = PrimeXbtFixtures.Json($$"""{"positionMode":"HEDGE","data":[{"id":0,"symbol":"XAU/USD","qty":0.01,"subPositions":[{{sub}}]}]}""");

        Assert.False(PrimeXbtPositionsParser.Parse(body, Symbol).IsValid);
    }

    [Fact]
    public void NettingMode_IsReportedSoCallerCanFailClosed()
    {
        var body = PrimeXbtFixtures.Json("""{"positionMode":"NETTING","data":[]}""");

        Assert.False(PrimeXbtPositionsParser.Parse(body, Symbol).IsHedgeMode);
    }
}

public sealed class PrimeXbtTicketCodecTests
{
    [Theory]
    [InlineData(10680072L)]
    [InlineData(1L)]
    [InlineData(PrimeXbtTicketCodec.MaxPositionId)]
    public void RoundTrip(long positionId)
    {
        var ticket = PrimeXbtTicketCodec.Encode(positionId);

        Assert.True(PrimeXbtTicketCodec.TryDecode(ticket, out var decoded));
        Assert.Equal(positionId, decoded);
        Assert.True(ticket <= long.MaxValue); // bit 63 luôn trống
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(PrimeXbtTicketCodec.MaxPositionId + 1)]
    public void Encode_OutOfRange_Throws(long positionId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrimeXbtTicketCodec.Encode(positionId));
    }

    [Fact]
    public void CTraderAndPrimeXbtCodecs_RejectEachOther()
    {
        var primeXbtTicket = PrimeXbtTicketCodec.Encode(10680072);
        var ctraderTicket = CTraderTicketCodec.Encode(10680072);

        Assert.False(CTraderTicketCodec.TryDecode(primeXbtTicket, out _));
        Assert.False(PrimeXbtTicketCodec.TryDecode(ctraderTicket, out _));
        Assert.NotEqual(primeXbtTicket, ctraderTicket);
    }

    [Theory]
    [InlineData(77462099UL)]   // ticket MT5 thật ở smoke S4
    [InlineData(0UL)]
    [InlineData(0x2000_0000_0000_0000UL)] // namespace trần, id 0
    public void MtTicketsAndEmptyId_AreNotPrimeXbt(ulong ticket)
    {
        Assert.False(PrimeXbtTicketCodec.IsPrimeXbtTicket(ticket));
    }
}
