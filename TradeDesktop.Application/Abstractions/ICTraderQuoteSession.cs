using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public enum CTraderQuoteEventKind
{
    Connecting = 0,
    LoggedOn = 1,
    LoggedOut = 2,
    SymbolResolved = 3,
    DigitsMismatch = 4,
    Stopped = 5,
    Error = 6,
    // Ngắt mạch: quá nhiều lần mất phiên trong một phút → dừng hẳn, KHÔNG tự nối lại.
    ReconnectStorm = 7
}

public sealed record CTraderQuoteSessionEvent(CTraderQuoteEventKind Kind, string Message);

// Luồng GIÁ sàn B qua FIX QUOTE session (Phase 4). CHỈ đọc giá — không có đường đặt lệnh.
// Hai method được gọi từ vòng poll 50 ms của reader: KHÔNG được throw, KHÔNG được chặn (connect/logout
// chạy nền).
public interface ICTraderQuoteSession
{
    // Sự kiện vòng đời (Telegram, UI). Phát từ thread nền.
    event Action<CTraderQuoteSessionEvent>? EventRaised;

    // Dòng log đã che 554, có prefix [CTRADER][LEVEL]. Phát từ thread nền.
    event Action<string>? LogLine;

    string StatusText { get; }

    // Phase 5: header Connected của trades map = QUOTE && TRADE logged on (§4.5).
    bool IsQuoteLoggedOn => false;

    // G4: platform_b != ctrader → không mở socket; đổi khỏi ctrader → unsubscribe + logout + giải phóng.
    void EnsureState(string platformB, CTraderFixConfig config);

    // G3: metrics sàn B từ top-of-book đang cache. Luôn trả về, kể cả Disconnected (fail-closed §4.7, R6, R9).
    ExchangeMetrics Read(ExchangeMetrics exchangeA, int point, int confirmLatencyMs);
}
