using System.Net;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.TradeLocker;
using TradeDesktop.Infrastructure.TradeLocker;
using TradeDesktop.Infrastructure.TradeLocker.Browser;
using TradeDesktop.Tests.PrimeXbt;

namespace TradeDesktop.Tests.TradeLocker;

// Phase 2: phiên TradeLocker (refresh token Keycloak) lưu DPAPI, đồng bộ từ Chrome không lùi token,
// Keycloak/kerber đúng như Phase 0 đã kiểm chứng (X-ODA-Brand, accountId dạng số, JWT 5').
public sealed class TradeLockerAuthTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 26, 5, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tradelocker-auth-tests-" + Guid.NewGuid().ToString("N"));

    private static string Jwt(DateTime expiresUtc) => PrimeXbtTokenStoreTests.MakeJwt(expiresUtc);

    private static TradeLockerSession Session(DateTime refreshExpires, DateTime accessExpires, string tag = "")
        => new(Jwt(refreshExpires) + tag, Jwt(accessExpires), Now);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    // ---------- TradeLockerSession / token store ----------

    [Fact]
    public void Session_ToString_ContainsNoToken()
    {
        var s = Session(Now.AddDays(30), Now.AddMinutes(30));

        Assert.DoesNotContain(s.RefreshToken, s.ToString());
        Assert.DoesNotContain(s.AccessToken, s.ToString());
        Assert.True(s.IsUsableAt(Now));
        Assert.False(s.IsUsableAt(Now.AddDays(31)));
    }

    [Fact]
    public void TokenStore_SaveLoadClear_RoundTrips()
    {
        var store = new TradeLockerTokenStore(Path.Combine(_dir, "host.bin"));
        var s = Session(Now.AddDays(30), Now.AddMinutes(30));

        store.Save(s);
        Assert.Equal(s, store.Load());
        Assert.DoesNotContain(s.RefreshToken, Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath))); // đã mã hoá

        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void TokenStore_CorruptOrMissingFile_LoadsNull()
    {
        var path = Path.Combine(_dir, "bad.bin");
        Assert.Null(new TradeLockerTokenStore(path).Load());

        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        Assert.Null(new TradeLockerTokenStore(path).Load());
    }

    // ---------- sync decision ----------

    [Fact]
    public void SyncDecision_NoStored_SavesWhenRefreshReadable()
    {
        Assert.True(TradeLockerSessionSyncDecision.ShouldSave(null, Session(Now.AddDays(30), Now)));
        Assert.False(TradeLockerSessionSyncDecision.ShouldSave(null, new TradeLockerSession("not-a-jwt", "", Now)));
    }

    [Fact]
    public void SyncDecision_NeverMovesBackToOlderRefreshToken()
    {
        var newer = Session(Now.AddDays(30), Now);
        var older = Session(Now.AddDays(29), Now);

        Assert.False(TradeLockerSessionSyncDecision.ShouldSave(newer, older));
        Assert.True(TradeLockerSessionSyncDecision.ShouldSave(older, newer));
    }

    [Fact]
    public void SyncDecision_SameExpiry_SavesOnlyWhenTokenDiffers()
    {
        var a = Session(Now.AddDays(30), Now, "x");
        var b = Session(Now.AddDays(30), Now, "y");

        Assert.False(TradeLockerSessionSyncDecision.ShouldSave(a, a));
        Assert.True(TradeLockerSessionSyncDecision.ShouldSave(a, b));
    }

    // ---------- auth client (HTTP giả) ----------

    [Fact]
    public async Task Refresh_ReturnsAccessAndRotatedRefresh_PostsRefreshGrant()
    {
        var access = Jwt(Now.AddMinutes(30));
        var rotated = Jwt(Now.AddDays(30));
        var handler = new FakeHandler(_ => Json($$"""{"access_token":"{{access}}","refresh_token":"{{rotated}}","expires_in":1800}"""));

        var tokens = await new TradeLockerAuthClient(new HttpClient(handler)).RefreshAsync("old-refresh", CancellationToken.None);

        Assert.NotNull(tokens);
        Assert.Equal(access, tokens!.AccessToken);
        Assert.Equal(rotated, tokens.RefreshToken);
        Assert.Equal(TradeLockerAuthClient.KeycloakTokenUrl, handler.Requests[0].Url);
        Assert.Contains("grant_type=refresh_token", handler.Requests[0].Body);
        Assert.Contains("client_id=frontend-app", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Refresh_NoRotation_KeepsOldRefresh_HttpErrorGivesNull()
    {
        var access = Jwt(Now.AddMinutes(30));
        var ok = new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => Json($$"""{"access_token":"{{access}}"}"""))));
        Assert.Equal("old", (await ok.RefreshAsync("old", CancellationToken.None))!.RefreshToken);

        var bad = new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest))));
        Assert.Null(await bad.RefreshAsync("old", CancellationToken.None));
        Assert.Equal("keycloak refresh: HTTP 400", bad.LastError);
    }

    [Theory]
    [InlineData(401, false)]
    [InlineData(400, false)]
    [InlineData(503, true)]
    [InlineData(429, true)]
    public async Task HttpStatus_ClassifiedTransientOrNot(int status, bool transient)
    {
        var client = new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)status))));

        Assert.Null(await client.ListAccountsAsync("acc", "PRDTL", CancellationToken.None));
        Assert.Equal(transient, client.LastErrorIsTransient);
    }

    [Fact]
    public async Task NetworkException_IsTransient()
    {
        var client = new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("no route"))));

        Assert.Null(await client.ListAccountsAsync("acc", "PRDTL", CancellationToken.None));
        Assert.True(client.LastErrorIsTransient);
        Assert.Equal("kerber accounts: HttpRequestException", client.LastError);
    }

    [Fact]
    public async Task ListAccounts_SendsBrandHeader_ParsesAccounts()
    {
        var handler = new FakeHandler(_ => Json("""
            {"data":[{"id":"PRDTL#O#1791342294192995800","accId":"1791342294192995800","accName":"My open demo account",
              "type":"OPEN_DEMO","engine":"APOLLO","status":"ACTIVE","environmentInfo":{"cluster":"apl0"}}]}
            """));

        var accounts = await new TradeLockerAuthClient(new HttpClient(handler)).ListAccountsAsync("acc", "PRDTL", CancellationToken.None);

        var a = Assert.Single(accounts!);
        Assert.Equal("1791342294192995800", a.AccId);
        Assert.Equal("apl0", a.Cluster);
        Assert.True(a.IsDemo);
        Assert.True(a.IsApollo);
        Assert.Equal("PRDTL", handler.Requests[0].Headers["X-ODA-Brand"]);
        Assert.Equal("Bearer acc", handler.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task MintJwt_SendsNumericAccountId_ReadsExpireDateOrFallsBackToExp()
    {
        var jwt = Jwt(Now.AddMinutes(5));
        var handler = new FakeHandler(_ => Json($$$"""{"data":{"token":"{{{jwt}}}","expireDate":"2026-10-26T05:05:00Z"}}"""));

        var minted = await new TradeLockerAuthClient(new HttpClient(handler)).MintAccountJwtAsync("acc", "1791342294192995800", CancellationToken.None);

        Assert.Equal(jwt, minted!.Jwt);
        Assert.Equal(Now.AddMinutes(5), minted.ExpiresUtc);
        Assert.Contains("\"accountId\":\"1791342294192995800\"", handler.Requests[0].Body);

        var noDate = new FakeHandler(_ => Json($$"""{"accessToken":"{{jwt}}"}"""));
        var fallback = await new TradeLockerAuthClient(new HttpClient(noDate)).MintAccountJwtAsync("acc", "1", CancellationToken.None);
        Assert.Equal(Now.AddMinutes(5), fallback!.ExpiresUtc);
    }

    [Fact]
    public async Task MintJwt_NoTokenInResponse_GivesNull()
    {
        var client = new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => Json("""{"data":{"foo":"bar"}}"""))));

        Assert.Null(await client.MintAccountJwtAsync("acc", "1", CancellationToken.None));
        Assert.Contains("không có token", client.LastError);
    }

    // ---------- auth session ----------

    [Fact]
    public async Task AuthSession_NotLoggedIn_FailsClosed()
    {
        var session = new TradeLockerAuthSession(new MemoryStore(null), new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("no http")))), () => Now);

        Assert.Null(await session.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("chưa đăng nhập TradeLocker", session.LastError);
    }

    [Fact]
    public async Task AuthSession_ValidAccess_UsedWithoutHttp_ExpiredAccess_RefreshedAndSaved()
    {
        var valid = Session(Now.AddDays(30), Now.AddMinutes(20));
        var noHttp = new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("no http"))));
        Assert.Equal(valid.AccessToken, await new TradeLockerAuthSession(new MemoryStore(valid), noHttp, () => Now).GetAccessTokenAsync(CancellationToken.None));

        var expired = Session(Now.AddDays(30), Now.AddSeconds(30)); // trong biên 60 s ⇒ refresh
        var newAccess = Jwt(Now.AddMinutes(30));
        var store = new MemoryStore(expired);
        var client = new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => Json($$"""{"access_token":"{{newAccess}}"}"""))));

        Assert.Equal(newAccess, await new TradeLockerAuthSession(store, client, () => Now).GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(newAccess, store.Current!.AccessToken); // không xoay refresh ⇒ vẫn lưu access mới
        Assert.Equal(expired.RefreshToken, store.Current.RefreshToken);
    }

    [Fact]
    public async Task AuthSession_RefreshTokenExpired_FailsClosed()
    {
        var store = new MemoryStore(Session(Now.AddMinutes(-1), Now.AddMinutes(-1)));
        var session = new TradeLockerAuthSession(store, new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException()))), () => Now);

        Assert.Null(await session.GetAccessTokenAsync(CancellationToken.None));
        Assert.Contains("hết hạn", session.LastError);
    }

    [Theory]
    [InlineData("", "111")]
    [InlineData("222", "222")]
    public async Task AuthSession_ResolveAccount_ByAccIdOrFirst(string configured, string expected)
    {
        var session = AccountsSession("""
            {"data":[{"id":"P#O#111","accId":"111","type":"OPEN_DEMO","engine":"APOLLO","environmentInfo":{"cluster":"apl0"}},
                     {"id":"P#O#222","accId":"222","type":"OPEN_DEMO","engine":"APOLLO","environmentInfo":{"cluster":"apl0"}}]}
            """);

        var account = await session.ResolveAccountAsync(TradeLockerConfigTests.Demo() with { AccountId = configured }, CancellationToken.None);

        Assert.Equal(expected, account!.AccId);
    }

    [Fact]
    public async Task AuthSession_ResolveAccount_MissingOrNonApollo_FailsClosed()
    {
        var missing = AccountsSession("""{"data":[{"id":"x","accId":"111","engine":"APOLLO"}]}""");
        Assert.Null(await missing.ResolveAccountAsync(TradeLockerConfigTests.Demo() with { AccountId = "999" }, CancellationToken.None));
        Assert.Contains("không thấy account 999", missing.LastError);

        var legacy = AccountsSession("""{"data":[{"id":"x","accId":"111","engine":"CLASSIC"}]}""");
        Assert.Null(await legacy.ResolveAccountAsync(TradeLockerConfigTests.Demo() with { AccountId = "" }, CancellationToken.None));
        Assert.Contains("chỉ hỗ trợ APOLLO", legacy.LastError);
    }

    [Fact]
    public async Task AuthSession_AccountJwt_CachedUntilMarginThenReminted()
    {
        var now = Now;
        var mintCount = 0;
        var handler = new FakeHandler(_ =>
        {
            mintCount++;
            return Json($$"""{"token":"{{Jwt(now.AddMinutes(5))}}"}""");
        });
        var session = new TradeLockerAuthSession(new MemoryStore(Session(Now.AddDays(30), Now.AddMinutes(30))), new TradeLockerAuthClient(new HttpClient(handler)), () => now);

        await session.GetAccountJwtAsync("111", CancellationToken.None);
        now = Now.AddMinutes(3);
        await session.GetAccountJwtAsync("111", CancellationToken.None);
        Assert.Equal(1, mintCount);

        now = Now.AddMinutes(4).AddSeconds(30); // còn < 60 s
        await session.GetAccountJwtAsync("111", CancellationToken.None);
        Assert.Equal(2, mintCount);
    }

    // ---------- CDP reader (phần thuần) ----------

    [Fact]
    public void Cdp_SelectTabs_OnlyAppTradeLockerPagesWithSocket()
    {
        const string targets = """
            [{"id":"1","type":"page","url":"https://app.tradelocker.com/en/trade","webSocketDebuggerUrl":"ws://a"},
             {"id":"2","type":"page","url":"https://auth.tradelocker.com/login","webSocketDebuggerUrl":"ws://b"},
             {"id":"3","type":"service_worker","url":"https://app.tradelocker.com/sw.js","webSocketDebuggerUrl":"ws://c"},
             {"id":"4","type":"page","url":"https://app.tradelocker.com.evil.test/","webSocketDebuggerUrl":"ws://d"}]
            """;

        var tabs = TradeLockerCdpSessionReader.SelectTradeLockerTabs(targets);

        Assert.Equal("1", Assert.Single(tabs).Id);
        Assert.Empty(TradeLockerCdpSessionReader.SelectTradeLockerTabs("not json"));
    }

    [Fact]
    public void Cdp_ParseOidcUser_RequiresReadableRefreshToken()
    {
        var refresh = Jwt(Now.AddDays(30));
        var access = Jwt(Now.AddMinutes(30));

        var session = TradeLockerCdpSessionReader.ParseOidcUser($$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_at":1}""", Now);
        Assert.Equal(refresh, session!.RefreshToken);
        Assert.Equal(access, session.AccessToken);

        Assert.Null(TradeLockerCdpSessionReader.ParseOidcUser(null, Now));
        Assert.Null(TradeLockerCdpSessionReader.ParseOidcUser("""{"access_token":"x"}""", Now));
        Assert.Null(TradeLockerCdpSessionReader.ParseOidcUser("{broken", Now));
    }

    private static TradeLockerAuthSession AccountsSession(string accountsJson)
        => new(new MemoryStore(Session(Now.AddDays(30), Now.AddMinutes(30))),
            new TradeLockerAuthClient(new HttpClient(new FakeHandler(_ => Json(accountsJson)))),
            () => Now);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class MemoryStore(TradeLockerSession? initial) : ITradeLockerTokenStore
    {
        public TradeLockerSession? Current { get; private set; } = initial;
        public TradeLockerSession? Load() => Current;
        public void Save(TradeLockerSession session) => Current = session;
        public void Clear() => Current = null;
    }

    private sealed record CapturedRequest(string Url, string Body, Dictionary<string, string> Headers);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(), body, headers));
            return respond(request);
        }
    }
}
