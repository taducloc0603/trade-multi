using System.Text.Json;
using TradeDesktop.Infrastructure.PrimeXbt.Browser;

namespace TradeDesktop.Tests.PrimeXbt.Browser;

// Phần parse thuần của PrimeXbtCdpSessionReader (fixture theo hình dạng JSON của Chrome /json/list và CDP).
public sealed class PrimeXbtCdpSessionReaderParsingTests
{
    private const string Targets = """
        [
          { "id": "SW1", "type": "service_worker", "url": "https://primexbt.com/sw.js", "webSocketDebuggerUrl": "ws://127.0.0.1:1/devtools/page/SW1" },
          { "id": "T1", "type": "page", "url": "https://www.google.com/", "webSocketDebuggerUrl": "ws://127.0.0.1:1/devtools/page/T1" },
          { "id": "T2", "type": "page", "url": "https://primexbt.com.evil.example/", "webSocketDebuggerUrl": "ws://127.0.0.1:1/devtools/page/T2" },
          { "id": "T3", "type": "page", "url": "https://primexbt.com/my/trade", "webSocketDebuggerUrl": "ws://127.0.0.1:1/devtools/page/T3" },
          { "id": "T4", "type": "page", "url": "https://primexbt.com/my/id/sign-in", "webSocketDebuggerUrl": "ws://127.0.0.1:1/devtools/page/T4" }
        ]
        """;

    [Fact]
    public void SelectPrimeXbtTab_PicksFirstPrimeXbtPage()
    {
        var tab = PrimeXbtCdpSessionReader.SelectPrimeXbtTab(Targets);
        Assert.NotNull(tab);
        Assert.Equal("T3", tab!.Id);
        Assert.Equal("ws://127.0.0.1:1/devtools/page/T3", tab.WebSocketUrl);
        Assert.Equal("T3", PrimeXbtCdpSessionReader.FindPrimeXbtTabId(Targets));
    }

    [Fact]
    public void SelectPrimeXbtTab_NoPrimeXbtPage_ReturnsNull()
    {
        const string json = """[ { "id": "T1", "type": "page", "url": "https://www.google.com/", "webSocketDebuggerUrl": "ws://x" } ]""";
        Assert.Null(PrimeXbtCdpSessionReader.SelectPrimeXbtTab(json));
        Assert.Null(PrimeXbtCdpSessionReader.FindPrimeXbtTabId(json));
    }

    [Fact]
    public void SelectPrimeXbtTab_MalformedJson_ReturnsNull()
    {
        Assert.Null(PrimeXbtCdpSessionReader.SelectPrimeXbtTab("{not json"));
        Assert.Null(PrimeXbtCdpSessionReader.FindPrimeXbtTabId("{}"));
    }

    [Fact]
    public void FindPrimeXbtTabId_DoesNotNeedWebSocketUrl()
    {
        const string json = """[ { "id": "T9", "type": "page", "url": "https://primexbt.com/" } ]""";
        Assert.Equal("T9", PrimeXbtCdpSessionReader.FindPrimeXbtTabId(json));
        Assert.Null(PrimeXbtCdpSessionReader.SelectPrimeXbtTab(json));
    }

    [Theory]
    [InlineData("https://primexbt.com", true)]
    [InlineData("https://primexbt.com/my", true)]
    [InlineData("https://PRIMEXBT.com/my", true)]
    [InlineData("https://primexbt.com.evil.example/", false)]
    [InlineData("http://primexbt.com/", false)]
    [InlineData(null, false)]
    public void IsPrimeXbtUrl(string? url, bool expected)
        => Assert.Equal(expected, PrimeXbtCdpSessionReader.IsPrimeXbtUrl(url));

    [Fact]
    public void ExtractApiCookies_FiltersByDomainAndName_PrefersExpectedPath()
    {
        using var doc = JsonDocument.Parse("""
            { "cookies": [
              { "name": "fws_token", "value": "fws-good", "domain": "api.primexbt.com", "path": "/v2/fws" },
              { "name": "fws_token", "value": "fws-other-domain", "domain": ".evil.example", "path": "/v2/fws" },
              { "name": "refresh_token", "value": "refresh-check", "domain": "api.primexbt.com", "path": "/v2/auth/check" },
              { "name": "refresh_token", "value": "refresh-good", "domain": "api.primexbt.com", "path": "/v2/auth/refresh" },
              { "name": "ws_token", "value": "ws", "domain": ".api.primexbt.com", "path": "/v2/pws/" },
              { "name": "session", "value": "ignored", "domain": "api.primexbt.com", "path": "/" },
              { "name": "bws_token", "value": "", "domain": "api.primexbt.com", "path": "/v2/bws" }
            ] }
            """);

        var map = PrimeXbtCdpSessionReader.ExtractApiCookies(doc.RootElement);

        Assert.Equal("fws-good", map["fws_token"]);
        Assert.Equal("refresh-good", map["refresh_token"]);
        Assert.Equal("ws", map["ws_token"]);
        Assert.False(map.ContainsKey("session"));
        Assert.False(map.ContainsKey("bws_token"));
    }

    [Fact]
    public void ExtractApiCookies_PreferredPathSeenFirst_NotOverwrittenByOtherPath()
    {
        using var doc = JsonDocument.Parse("""
            { "cookies": [
              { "name": "refresh_token", "value": "refresh-good", "domain": "api.primexbt.com", "path": "/v2/auth/refresh" },
              { "name": "refresh_token", "value": "refresh-check", "domain": "api.primexbt.com", "path": "/v2/auth/check" }
            ] }
            """);

        Assert.Equal("refresh-good", PrimeXbtCdpSessionReader.ExtractApiCookies(doc.RootElement)["refresh_token"]);
    }

    [Fact]
    public void ExtractApiCookies_UnexpectedShape_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{ "nope": 1 }""");
        Assert.Empty(PrimeXbtCdpSessionReader.ExtractApiCookies(doc.RootElement));
    }

    [Fact]
    public void ReadEvaluateString_StringValue()
    {
        using var doc = JsonDocument.Parse("""{ "result": { "type": "string", "value": "a.b.c" } }""");
        Assert.Equal("a.b.c", PrimeXbtCdpSessionReader.ReadEvaluateString(doc.RootElement));
    }

    [Fact]
    public void ReadEvaluateString_NullValue_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{ "result": { "type": "object", "subtype": "null", "value": null } }""");
        Assert.Null(PrimeXbtCdpSessionReader.ReadEvaluateString(doc.RootElement));
    }
}
