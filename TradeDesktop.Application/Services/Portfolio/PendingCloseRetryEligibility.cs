namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Quyết định thuần: một leg pending-close có đủ thông tin để retry đóng không. MT4/MT5 click qua cửa sổ terminal nên
/// BẮT BUỘC có trade HWND; cTrader (FIX) và PrimeXBT (WebSocket) đóng theo ticket, HWND B không bắt buộc ở cấu hình —
/// nếu vẫn đòi HWND thì leg B của hai sàn này không bao giờ được retry khi cột HWND B để trống (lỗi im lặng).
/// </summary>
public static class PendingCloseRetryEligibility
{
    public static bool CanRetry(bool hasTradeMap, bool hasPlatform, bool hasTradeHwnd, bool platformUsesHwnd)
        => hasTradeMap && hasPlatform && (hasTradeHwnd || !platformUsesHwnd);
}
