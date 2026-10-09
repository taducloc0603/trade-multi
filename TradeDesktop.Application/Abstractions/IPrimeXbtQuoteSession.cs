using System.Text.RegularExpressions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public enum PrimeXbtSessionEventKind
{
    Connecting = 0,
    Connected = 1,
    Disconnected = 2,
    SymbolResolved = 3,
    DigitsMismatch = 4,
    Stopped = 5,
    Error = 6,
    ReconnectStorm = 7,
    AuthRequired = 8,
    // Phase 5 (P8): snapshot `positions` báo positionMode ≠ HEDGE ⇒ Trades map B fail-closed.
    PositionModeInvalid = 9,
    // Phase 7: lệnh gửi đi không có ack (timeout / rớt socket) và kết luận đối soát sau đó (chỉ báo cáo).
    OrderUncertain = 10,
    OrderReconciled = 11
}

public sealed record PrimeXbtSessionEvent(PrimeXbtSessionEventKind Kind, string Message);

// Nguồn giá sàn B khi platform_b = primexbt (docs/plans/primexbt Phase 4). Hợp đồng soi gương ICTraderQuoteSession:
// EnsureState/Read chạy trên luồng poll 50 ms của SharedMemoryMarketDataReader và KHÔNG BAO GIỜ chặn hay throw.
// Read trả IsConnected=false (fail-closed) khi chưa sẵn sàng. LatencyMs = TUỔI TICK (tick PrimeXBT không có timestamp).
public interface IPrimeXbtQuoteSession
{
    // Bắn từ luồng nền.
    event Action<PrimeXbtSessionEvent>? EventRaised;

    // Đã che bí mật (JWT/cookie), tiền tố "[PRIMEXBT][LEVEL] ".
    event Action<string>? LogLine;

    string StatusText { get; }

    bool IsLoggedOn { get; }

    void EnsureState(string platformB, PrimeXbtConfig config);

    ExchangeMetrics Read(ExchangeMetrics exchangeA, int point, int confirmLatencyMs);
}

public static class PrimeXbtLogMasker
{
    private static readonly Regex JwtRx = new(@"eyJ[\w-]+\.[\w-]+\.[\w-]+", RegexOptions.Compiled);
    private static readonly Regex QueryJwtRx = new(@"(jwt=)[^&\s""]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CookieRx = new(@"((?:fws_token|ws_token|bws_token|refresh_token)\s*[=:]\s*""?)[^;,""\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GuardRx = new(@"(auth-guard=)[^&\s""]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var masked = JwtRx.Replace(text, "<JWT>");
        masked = QueryJwtRx.Replace(masked, "$1<JWT>");
        masked = CookieRx.Replace(masked, "$1<COOKIE>");
        return GuardRx.Replace(masked, "$1<AG>");
    }
}
