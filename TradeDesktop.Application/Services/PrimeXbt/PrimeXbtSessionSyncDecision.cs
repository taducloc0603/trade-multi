using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Application.Services.PrimeXbt;

// Quyết định thuần: phiên vừa đọc từ trình duyệt (Chrome + CDP) có đáng ghi đè kho DPAPI không.
// - Thiếu cookie bắt buộc ⇒ không ghi (socket/refresh không dùng được).
// - KHÔNG BAO GIỜ ghi đè JWT mới hơn bằng JWT cũ hơn (vd app vừa tự refresh, web còn giữ JWT cũ).
// - Cùng hạn JWT nhưng cookie fws_token/refresh_token đổi (đăng nhập lại) ⇒ ghi.
// Việc web mất token (đã logout) KHÔNG được xử lý ở đây: caller không có phiên để truyền vào nên store giữ nguyên.
public static class PrimeXbtSessionSyncDecision
{
    public static bool ShouldSave(PrimeXbtSession? stored, PrimeXbtSession captured)
    {
        ArgumentNullException.ThrowIfNull(captured);

        if (!captured.HasRequiredCookies || captured.JwtExpiresUtc is not { } capturedExpiry)
        {
            return false;
        }

        if (stored is null || stored.JwtExpiresUtc is not { } storedExpiry)
        {
            return true;
        }

        if (capturedExpiry > storedExpiry)
        {
            return true;
        }

        if (capturedExpiry < storedExpiry)
        {
            return false;
        }

        return CookieDiffers(stored, captured, PrimeXbtSession.FwsCookie)
            || CookieDiffers(stored, captured, PrimeXbtSession.RefreshCookie);
    }

    private static bool CookieDiffers(PrimeXbtSession stored, PrimeXbtSession captured, string name)
    {
        stored.ApiCookies.TryGetValue(name, out var oldValue);
        captured.ApiCookies.TryGetValue(name, out var newValue);
        return !string.Equals(oldValue, newValue, StringComparison.Ordinal);
    }
}
