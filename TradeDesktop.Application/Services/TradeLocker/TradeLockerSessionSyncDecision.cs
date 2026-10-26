using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Application.Services.TradeLocker;

// Quyết định thuần: phiên vừa đọc từ trình duyệt (Chrome + CDP, sessionStorage của web) có đáng ghi đè kho DPAPI không.
// - Refresh token không đọc được hạn ⇒ không ghi.
// - KHÔNG BAO GIỜ ghi đè refresh token hạn xa hơn bằng hạn gần hơn (vd app vừa tự refresh, web còn giữ token cũ).
// - Cùng hạn nhưng token khác (đăng nhập lại cùng giây) ⇒ ghi.
// Web mất token (đã logout) KHÔNG xử lý ở đây: caller không có phiên để truyền vào nên store giữ nguyên.
public static class TradeLockerSessionSyncDecision
{
    public static bool ShouldSave(TradeLockerSession? stored, TradeLockerSession captured)
    {
        ArgumentNullException.ThrowIfNull(captured);

        if (captured.RefreshExpiresUtc is not { } capturedExpiry)
        {
            return false;
        }

        if (stored is null || stored.RefreshExpiresUtc is not { } storedExpiry)
        {
            return true;
        }

        if (capturedExpiry != storedExpiry)
        {
            return capturedExpiry > storedExpiry;
        }

        return !string.Equals(stored.RefreshToken, captured.RefreshToken, StringComparison.Ordinal);
    }
}
