using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

// Chrome + CDP sync: chỉ ghi kho DPAPI khi phiên đọc được tốt hơn bản đang lưu; không bao giờ lùi JWT.
public sealed class PrimeXbtSessionSyncDecisionTests
{
    private static readonly DateTime Expiry = new(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc);

    private static PrimeXbtSession Session(DateTime expiresUtc, string fws = "fws-a", string refresh = "refresh-a", bool withCookies = true)
        => new(
            PrimeXbtTokenStoreTests.MakeJwt(expiresUtc),
            withCookies
                ? new Dictionary<string, string> { ["fws_token"] = fws, ["refresh_token"] = refresh }
                : new Dictionary<string, string> { ["fws_token"] = fws },
            DateTime.UtcNow);

    [Fact]
    public void EmptyStore_Saves()
        => Assert.True(PrimeXbtSessionSyncDecision.ShouldSave(null, Session(Expiry)));

    [Fact]
    public void NewerJwt_Saves()
        => Assert.True(PrimeXbtSessionSyncDecision.ShouldSave(Session(Expiry), Session(Expiry.AddDays(1))));

    [Fact]
    public void OlderJwt_NeverOverwritesNewer()
        => Assert.False(PrimeXbtSessionSyncDecision.ShouldSave(Session(Expiry), Session(Expiry.AddHours(-1), fws: "fws-b")));

    [Fact]
    public void SameJwtSameCookies_DoesNotSave()
        => Assert.False(PrimeXbtSessionSyncDecision.ShouldSave(Session(Expiry), Session(Expiry)));

    [Fact]
    public void SameExpiry_FwsCookieChanged_Saves()
        => Assert.True(PrimeXbtSessionSyncDecision.ShouldSave(Session(Expiry), Session(Expiry, fws: "fws-b")));

    [Fact]
    public void SameExpiry_RefreshCookieChanged_Saves()
        => Assert.True(PrimeXbtSessionSyncDecision.ShouldSave(Session(Expiry), Session(Expiry, refresh: "refresh-b")));

    [Fact]
    public void MissingRequiredCookie_DoesNotSave_EvenIfStoreEmpty()
        => Assert.False(PrimeXbtSessionSyncDecision.ShouldSave(null, Session(Expiry.AddDays(1), withCookies: false)));

    [Fact]
    public void StoredJwtUnreadable_Saves()
    {
        var broken = new PrimeXbtSession("not-a-jwt", new Dictionary<string, string>(), DateTime.UtcNow);
        Assert.True(PrimeXbtSessionSyncDecision.ShouldSave(broken, Session(Expiry)));
    }

    [Fact]
    public void CapturedJwtUnreadable_DoesNotSave()
    {
        var captured = new PrimeXbtSession(
            "x.y.z",
            new Dictionary<string, string> { ["fws_token"] = "a", ["refresh_token"] = "b" },
            DateTime.UtcNow);
        Assert.False(PrimeXbtSessionSyncDecision.ShouldSave(null, captured));
    }
}
