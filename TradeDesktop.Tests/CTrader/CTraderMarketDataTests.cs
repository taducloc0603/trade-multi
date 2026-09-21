using TradeDesktop.Infrastructure.CTrader;

namespace TradeDesktop.Tests.CTrader;

public sealed class CTraderMarketDataTests
{
    // W spot thật đo trên live 8220816 ngày 2026-09-16 (Phase 0 memo §5 câu 2).
    private const string LiveSpotW =
        "35=W|34=3|49=cServer|50=QUOTE|52=20260916-14:57:52.978|56=live.fxpro.8220816|57=QUOTE|55=41|262=MARKETDATAID|268=2|269=0|270=4350.77|269=1|270=4350.93";

    private const string LiveDepthX =
        "35=X|34=44|49=cServer|50=QUOTE|52=20260916-14:58:03.237|56=live.fxpro.8220816|57=QUOTE|262=MARKETDATAID|268=2|279=0|269=1|278=6906452065|55=41|270=4353.13|271=18750|279=2|269=1|278=6906452064|55=41";

    [Fact]
    public void QuoteBook_LiveSpotSnapshot_SetsTopOfBook()
    {
        long clock = 1000;
        var book = new CTraderQuoteBook(41, () => clock);

        Assert.True(book.Apply(FixTestSupport.Parse(LiveSpotW)));

        Assert.True(book.HasTopOfBook);
        Assert.Equal(4350.77m, book.Bid);
        Assert.Equal(4350.93m, book.Ask);
        Assert.Equal(1000, book.LastQuoteTickCount);
        Assert.Equal(1, book.QuoteSequence);
        Assert.True(book.LastSendingTimeMs > 0);
    }

    [Fact]
    public void QuoteBook_EachSnapshotReplacesBookAndAdvancesSequence()
    {
        long clock = 1000;
        var book = new CTraderQuoteBook(41, () => clock);
        book.Apply(FixTestSupport.Parse(LiveSpotW));

        clock = 1050;
        book.Apply(FixTestSupport.Parse(LiveSpotW.Replace("270=4350.77", "270=4351.00")));

        Assert.Equal(4351.00m, book.Bid);
        Assert.Equal(2, book.QuoteSequence);
        Assert.Equal(1050, book.LastQuoteTickCount);
    }

    [Fact]
    public void QuoteBook_IncrementalInSpotMode_FailsClosed()
    {
        var book = new CTraderQuoteBook(41);
        book.Apply(FixTestSupport.Parse(LiveSpotW));

        Assert.False(book.Apply(FixTestSupport.Parse(LiveDepthX)));

        Assert.False(book.HasTopOfBook);
        Assert.Null(book.Bid);
        Assert.Equal(CTraderQuoteBook.AnomalyIncrementalInSpot, book.LastAnomaly);
    }

    [Fact]
    public void QuoteBook_IncrementalBeforeAnySnapshot_DoesNotCrash()
    {
        var book = new CTraderQuoteBook(41);

        Assert.False(book.Apply(FixTestSupport.Parse(LiveDepthX)));
        Assert.False(book.HasTopOfBook);
    }

    [Fact]
    public void QuoteBook_SnapshotAfterAnomaly_Recovers()
    {
        var book = new CTraderQuoteBook(41);
        book.Apply(FixTestSupport.Parse(LiveDepthX));

        Assert.True(book.Apply(FixTestSupport.Parse(LiveSpotW)));
        Assert.True(book.HasTopOfBook);
        Assert.Null(book.LastAnomaly);
    }

    // Quyết định câu 3 (spot): X Delete-rồi-New cùng 278, hay Delete 278 không tồn tại, đều là bất thường ở chế độ
    // spot → xoá book, không throw, không đoán; W kế tiếp khôi phục.
    [Theory]
    [InlineData("262=MARKETDATAID|268=2|279=2|269=1|278=111|55=41|279=0|269=1|278=111|55=41|270=4353.13|271=18750")]
    [InlineData("262=MARKETDATAID|268=1|279=2|269=0|278=999999|55=41")]
    public void QuoteBook_DeleteNewOrUnknownDelete_FailClosedWithoutThrow(string body)
    {
        var book = new CTraderQuoteBook(41);
        book.Apply(FixTestSupport.Parse(LiveSpotW));

        var ex = Record.Exception(() => book.Apply(FixTestSupport.Parse(
            $"35=X|34=44|49=cServer|52=20260916-14:58:03.237|56=live.fxpro.8220816|{body}")));

        Assert.Null(ex);
        Assert.False(book.HasTopOfBook);
        Assert.True(book.Apply(FixTestSupport.Parse(LiveSpotW)));
        Assert.True(book.HasTopOfBook);
    }

    [Fact]
    public void QuoteBook_EmptyBookNeverServesStalePrices()
    {
        var book = new CTraderQuoteBook(41);
        book.Apply(FixTestSupport.Parse(LiveSpotW));

        book.Clear();

        Assert.False(book.HasTopOfBook);
        Assert.Null(book.Bid);
        Assert.Null(book.Ask);
    }

    [Fact]
    public void QuoteBook_OneSidedSnapshot_IsNotTopOfBook()
    {
        var book = new CTraderQuoteBook(41);

        book.Apply(FixTestSupport.Parse(
            "35=W|34=3|49=cServer|52=20260916-14:57:52.978|56=live.fxpro.8220816|55=41|262=MARKETDATAID|268=1|269=0|270=4350.77"));

        Assert.False(book.HasTopOfBook);
    }

    [Fact]
    public void QuoteBook_OtherSymbol_Ignored()
    {
        var book = new CTraderQuoteBook(41);

        Assert.False(book.Apply(FixTestSupport.Parse(LiveSpotW.Replace("|55=41|", "|55=1|"))));
        Assert.False(book.HasTopOfBook);
    }

    [Fact]
    public void SecurityCatalog_ReadsRawTags1007And1008()
    {
        var catalog = new CTraderSecurityCatalog();
        var y = FixTestSupport.Parse(
            "35=y|34=2|49=cServer|50=TRADE|52=20260916-14:52:24.901|56=live.fxpro.8220816|57=TRADE|320=spike-sec|322=responce:spike-sec|560=0|146=3|55=1|1007=EURUSD|1008=5|55=41|1007=XAUUSD|1008=2|55=1110|1007=XAUUSDgr|1008=3");

        Assert.Equal(3, catalog.Apply(y));

        Assert.True(catalog.TryGet(41, out var gold));
        Assert.Equal("XAUUSD", gold.SymbolName);
        Assert.Equal(2, gold.Digits);
        Assert.True(catalog.IsResolved(1110));
        Assert.False(catalog.IsResolved(999));
    }

    [Fact]
    public void SecurityCatalog_NewListReplacesOld_AndClearWorks()
    {
        var catalog = new CTraderSecurityCatalog();
        catalog.Apply(FixTestSupport.Parse("35=y|34=2|49=cServer|52=20260916-14:52:24.901|56=x|320=a|560=0|146=1|55=41|1007=XAUUSD|1008=2"));
        catalog.Apply(FixTestSupport.Parse("35=y|34=3|49=cServer|52=20260916-14:52:25.901|56=x|320=b|560=0|146=1|55=1|1007=EURUSD|1008=5"));

        Assert.False(catalog.IsResolved(41));
        Assert.True(catalog.IsResolved(1));

        catalog.Clear();
        Assert.Equal(0, catalog.Count);
    }
}
