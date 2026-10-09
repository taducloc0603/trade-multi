using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Infrastructure.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

// Phase 2: phiên PrimeXBT lưu cục bộ bằng DPAPI; mọi lỗi đọc ⇒ null, không throw; không lộ bí mật.
public sealed class PrimeXbtTokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "primexbt-tokenstore-tests-" + Guid.NewGuid().ToString("N"));

    internal static string MakeJwt(DateTime expiresUtc)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = new DateTimeOffset(expiresUtc).ToUnixTimeSeconds();
        return $"{B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")}.{B64($"{{\"sub\":1,\"exp\":{exp}}}")}.c2lnbmF0dXJl";
    }

    internal static PrimeXbtSession Session(DateTime expiresUtc, bool withCookies = true) => new(
        MakeJwt(expiresUtc),
        withCookies
            ? new Dictionary<string, string> { ["fws_token"] = "fws-secret-value", ["refresh_token"] = "refresh-secret-value" }
            : new Dictionary<string, string>(),
        new DateTime(2026, 10, 9, 7, 0, 0, DateTimeKind.Utc));

    private string FilePath => Path.Combine(_dir, "test-host.bin");

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new PrimeXbtTokenStore(FilePath);
        var session = Session(new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc));

        store.Save(session);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(session.Jwt, loaded!.Jwt);
        Assert.Equal("fws-secret-value", loaded.ApiCookies["fws_token"]);
        Assert.Equal(session.SavedUtc, loaded.SavedUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.SavedUtc.Kind);
    }

    [Fact]
    public void Load_MissingFile_IsNull()
    {
        Assert.Null(new PrimeXbtTokenStore(FilePath).Load());
    }

    [Fact]
    public void Load_CorruptFile_IsNullAndDoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, [1, 2, 3, 4, 5]);

        Assert.Null(new PrimeXbtTokenStore(FilePath).Load());
    }

    [Fact]
    public void File_IsEncrypted_NoPlaintextSecrets()
    {
        var store = new PrimeXbtTokenStore(FilePath);
        var session = Session(new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc));

        store.Save(session);
        var bytes = File.ReadAllText(FilePath, Encoding.Latin1);

        Assert.DoesNotContain("eyJ", bytes);
        Assert.DoesNotContain("fws-secret-value", bytes);
    }

    [Fact]
    public void Clear_RemovesSession_AndIsIdempotent()
    {
        var store = new PrimeXbtTokenStore(FilePath);
        store.Save(Session(new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc)));

        store.Clear();
        store.Clear();

        Assert.Null(store.Load());
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Save_OverwritesPreviousSession()
    {
        var store = new PrimeXbtTokenStore(FilePath);
        store.Save(Session(new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc)));
        var newer = Session(new DateTime(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc));

        store.Save(newer);

        Assert.Equal(newer.Jwt, store.Load()!.Jwt);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }
}

public sealed class PrimeXbtSessionTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void JwtExpiry_IsReadFromExpClaim()
    {
        var expires = new DateTime(2026, 10, 16, 3, 27, 0, DateTimeKind.Utc);

        Assert.Equal(expires, PrimeXbtTokenStoreTests.Session(expires).JwtExpiresUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.sig")] // payload {} — không có exp
    public void ReadExpiry_InvalidInput_IsNull(string? jwt)
    {
        Assert.Null(PrimeXbtJwt.ReadExpiryUtc(jwt));
        Assert.False(PrimeXbtJwt.LooksLikeJwt(jwt));
    }

    [Fact]
    public void IsUsableAt_RequiresCookiesAndUnexpiredJwt()
    {
        Assert.True(PrimeXbtTokenStoreTests.Session(Now.AddDays(1)).IsUsableAt(Now));
        Assert.False(PrimeXbtTokenStoreTests.Session(Now.AddDays(-1)).IsUsableAt(Now));
        Assert.False(PrimeXbtTokenStoreTests.Session(Now.AddDays(1), withCookies: false).IsUsableAt(Now));
    }

    [Fact]
    public void ToString_NeverContainsSecrets()
    {
        var session = PrimeXbtTokenStoreTests.Session(Now.AddDays(1));

        var text = session.ToString();

        Assert.DoesNotContain(session.Jwt, text);
        Assert.DoesNotContain("fws-secret-value", text);
        Assert.Contains("fws_token", text);
    }
}
