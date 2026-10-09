using System.Text.Json;
using System.Text.Json.Nodes;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

public sealed class PrimeXbtEnvelopeTests
{
    [Fact]
    public void Build_ProducesWebAppShape()
    {
        var json = PrimeXbtEnvelope.Build(PrimeXbtEnvelope.TypeSubscription, 8, "fx/market", new JsonObject { ["symbolId"] = 1019 });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("SUBSCRIPTION", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(8, doc.RootElement.GetProperty("rid").GetInt32());
        Assert.Equal("fx/market", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(1019, doc.RootElement.GetProperty("body").GetProperty("symbolId").GetInt32());
    }

    [Fact]
    public void Build_NullBody_SendsEmptyObject()
    {
        using var doc = JsonDocument.Parse(PrimeXbtEnvelope.Build(PrimeXbtEnvelope.TypeRequest, 1, "time", null));

        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("body").ValueKind);
    }

    [Theory]
    [InlineData("EVENT")]
    [InlineData("")]
    public void Build_InvalidType_Throws(string type)
    {
        Assert.Throws<ArgumentException>(() => PrimeXbtEnvelope.Build(type, 1, "time", null));
    }

    [Fact]
    public void TryParse_Response_ReadsRidSidAndBody()
    {
        Assert.True(PrimeXbtEnvelope.TryParse(PrimeXbtFixtures.Raw("quotes.json", "fx_market_response"), out var frame));

        Assert.Equal(PrimeXbtFrameType.Response, frame.Type);
        Assert.Equal("fx/market", frame.Action);
        Assert.Equal(8, frame.Rid);
        Assert.Equal(6, frame.Sid);
        Assert.NotNull(frame.Body);
        Assert.Null(frame.Error);
    }

    [Fact]
    public void TryParse_Event_ReadsAid()
    {
        Assert.True(PrimeXbtEnvelope.TryParse(PrimeXbtFixtures.Raw("quotes.json", "fx_market_event"), out var frame));

        Assert.Equal(PrimeXbtFrameType.Event, frame.Type);
        Assert.Null(frame.Rid);
        Assert.Equal(1, frame.Aid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"type":"PING","action":"x"}""")]
    [InlineData("""{"type":"RESPONSE"}""")]
    public void TryParse_Garbage_ReturnsFalseWithoutThrowing(string? text)
    {
        Assert.False(PrimeXbtEnvelope.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_BusinessError_InBody()
    {
        Assert.True(PrimeXbtEnvelope.TryParse(PrimeXbtFixtures.Raw("trading.json", "rejections_phase0", "qty_below_min"), out var frame));

        Assert.NotNull(frame.Error);
        Assert.Equal("TOO_LOW_AMOUNT", frame.Error!.Code);
        Assert.False(frame.Error.IsArgumentError);
    }

    [Fact]
    public void TryParse_ArgumentError_TopLevelWithoutBody()
    {
        Assert.True(PrimeXbtEnvelope.TryParse(PrimeXbtFixtures.Raw("trading.json", "rejections_phase0", "unknown_symbol"), out var frame));

        Assert.Null(frame.Body);
        Assert.Equal("WRONG_ARGS", frame.Error!.Code);
        Assert.Equal("Unknown symbol", frame.Error.Description);
        Assert.True(frame.Error.IsArgumentError);
    }

    [Fact]
    public void TryParse_SuccessfulPlace_HasNoError()
    {
        Assert.True(PrimeXbtEnvelope.TryParse(PrimeXbtFixtures.Raw("trading.json", "open_buy", "response", "frame"), out var frame));

        Assert.Null(frame.Error);
        Assert.Equal(2095468976, frame.Body!.Value.GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData("TOO_LOW_AMOUNT", "Khối lượng nhỏ hơn mức tối thiểu của symbol")]
    [InlineData("POSITION_NOT_FOUND", "Không tìm thấy vị thế")]
    [InlineData("SOMETHING_NEW", "SOMETHING_NEW")]
    [InlineData(null, "Lỗi không rõ")]
    public void ErrorMapper_KnownCodesTranslated_UnknownPassThrough(string? code, string expected)
    {
        Assert.Equal(expected, PrimeXbtErrorMapper.Describe(code));
    }
}

public sealed class PrimeXbtMarketParserTests
{
    [Fact]
    public void SymbolResolver_RealMarkets2_ResolvesXauUsd()
    {
        var result = PrimeXbtSymbolResolver.Resolve(PrimeXbtFixtures.Body("quotes.json", "markets2_response_xau_only"), "XAU/USD");

        Assert.Equal(PrimeXbtSymbolResolveStatus.Resolved, result.Status);
        Assert.Equal(1019, result.Info!.SymbolId);
        Assert.Equal(2, result.Info.Digits);
        Assert.True(result.Info.IsMarketOpen);
    }

    [Fact]
    public void SymbolResolver_DoesNotConfuse24hVariant()
    {
        var body = PrimeXbtFixtures.Json("""
            {"data":[{"symbolId":2000,"symbol":"XAU/USD.24","priceScale":2},{"symbolId":1019,"symbol":"XAU/USD","priceScale":2}]}
            """);

        Assert.Equal(1019, PrimeXbtSymbolResolver.Resolve(body, "XAU/USD").Info!.SymbolId);
        Assert.Equal(2000, PrimeXbtSymbolResolver.Resolve(body, "XAU/USD.24").Info!.SymbolId);
    }

    [Fact]
    public void SymbolResolver_DuplicateExactMatches_IsAmbiguous()
    {
        var body = PrimeXbtFixtures.Json("""{"data":[{"symbolId":1,"symbol":"XAU/USD","priceScale":2},{"symbolId":2,"symbol":"XAU/USD","priceScale":2}]}""");

        Assert.Equal(PrimeXbtSymbolResolveStatus.Ambiguous, PrimeXbtSymbolResolver.Resolve(body, "XAU/USD").Status);
    }

    [Theory]
    [InlineData("""{"data":[{"symbolId":1,"symbol":"BTC/USD","priceScale":1}]}""", "XAU/USD", PrimeXbtSymbolResolveStatus.NotFound)]
    [InlineData("""{"data":[{"symbol":"XAU/USD","priceScale":2}]}""", "XAU/USD", PrimeXbtSymbolResolveStatus.NotFound)]
    [InlineData("""{"data":[{"symbolId":1,"symbol":"XAU/USD"}]}""", "XAU/USD", PrimeXbtSymbolResolveStatus.Invalid)]
    [InlineData("""{"nodata":true}""", "XAU/USD", PrimeXbtSymbolResolveStatus.Invalid)]
    [InlineData("""{"data":[]}""", "", PrimeXbtSymbolResolveStatus.Invalid)]
    public void SymbolResolver_FailsClosed(string json, string symbol, PrimeXbtSymbolResolveStatus expected)
    {
        Assert.Equal(expected, PrimeXbtSymbolResolver.Resolve(PrimeXbtFixtures.Json(json), symbol).Status);
    }

    [Fact]
    public void QuoteParser_RealEvent()
    {
        Assert.True(PrimeXbtQuoteParser.TryParse(PrimeXbtFixtures.Body("quotes.json", "fx_market_event"), 1019, out var quote));

        Assert.Equal(4177.04m, quote.Bid);
        Assert.Equal(4177.23m, quote.Ask);
        Assert.Equal(4177.04m, quote.Last);
    }

    [Theory]
    [InlineData("""{"symId":1019,"a":4177.23,"b":4177.04}""", 2000)]       // sai symbol
    [InlineData("""{"symId":1019,"a":4177.00,"b":4177.04}""", 1019)]       // ask < bid
    [InlineData("""{"symId":1019,"a":4177.23}""", 1019)]                   // thiếu bid
    [InlineData("""{"symId":1019,"a":0,"b":0}""", 1019)]                   // giá 0
    [InlineData("""{"symId":1019,"a":"4177.23","b":"4177.04"}""", 1019)]   // chuỗi thay vì số
    public void QuoteParser_RejectsBadTicks(string json, int expectedSymbolId)
    {
        Assert.False(PrimeXbtQuoteParser.TryParse(PrimeXbtFixtures.Json(json), expectedSymbolId, out _));
    }
}
