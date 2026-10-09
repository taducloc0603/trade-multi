using System.Text;
using System.Text.Json;

namespace TradeDesktop.Application.Abstractions;

// Phiên đăng nhập PrimeXBT (docs/plans/primexbt Phase 0 Q1/Q2): socket `fws` cần JWT + cookie `fws_token`;
// refresh cần cookie `refresh_token`. BÍ MẬT — chỉ lưu cục bộ (DPAPI), không log, không lên Supabase.
public sealed record PrimeXbtSession(
    string Jwt,
    IReadOnlyDictionary<string, string> ApiCookies,
    DateTime SavedUtc)
{
    public const string FwsCookie = "fws_token";
    public const string RefreshCookie = "refresh_token";

    public DateTime? JwtExpiresUtc => PrimeXbtJwt.ReadExpiryUtc(Jwt);

    public bool HasRequiredCookies =>
        ApiCookies.ContainsKey(FwsCookie) && ApiCookies.ContainsKey(RefreshCookie);

    public bool IsUsableAt(DateTime utcNow) =>
        HasRequiredCookies && JwtExpiresUtc is { } expires && expires > utcNow;

    // Không bao giờ in JWT/cookie: chỉ hạn và tên cookie.
    public override string ToString() =>
        $"PrimeXbtSession {{ JwtExpiresUtc = {JwtExpiresUtc:yyyy-MM-dd HH:mm}Z, Cookies = [{string.Join(",", ApiCookies.Keys.OrderBy(k => k, StringComparer.Ordinal))}], SavedUtc = {SavedUtc:yyyy-MM-dd HH:mm}Z }}";
}

public interface IPrimeXbtTokenStore
{
    // null khi chưa đăng nhập, file hỏng hoặc không giải mã được (khác máy/khác user Windows). Không throw.
    PrimeXbtSession? Load();

    void Save(PrimeXbtSession session);

    void Clear();
}

public static class PrimeXbtJwt
{
    // Đọc claim `exp` (giây Unix) từ payload JWT; null nếu không phải JWT hợp lệ. Không kiểm chữ ký.
    public static DateTime? ReadExpiryUtc(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool LooksLikeJwt(string? value) => ReadExpiryUtc(value) is not null;
}
