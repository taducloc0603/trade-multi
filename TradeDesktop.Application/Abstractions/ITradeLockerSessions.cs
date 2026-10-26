using System.Text.RegularExpressions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.TradeLocker;

namespace TradeDesktop.Application.Abstractions;

public enum TradeLockerSessionEventKind
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
    // Account kerber không phải demo (CLAUDE.md §0.0): chỉ CẢNH BÁO — quyết định giao dịch live thuộc chủ dự án.
    AccountNotDemo = 9,
    // Frame lạ / message không decode được (drift giao thức, R1). Phần bắt buộc đã fail-closed ở decoder.
    ProtocolDrift = 10,
    // Lệnh gửi đi không có kết quả rõ (rớt socket / timeout) — KHÔNG gửi lại (Phase 0 Q6b).
    OrderUncertain = 11
}

public sealed record TradeLockerSessionEvent(TradeLockerSessionEventKind Kind, string Message);

// Nguồn giá sàn B khi platform_b = tradelocker (docs/plans/tradelocker Phase 4). Cùng hợp đồng IPrimeXbtQuoteSession:
// EnsureState/Read chạy trên luồng poll 50 ms của SharedMemoryMarketDataReader và KHÔNG BAO GIỜ chặn hay throw.
// Read trả IsConnected=false (fail-closed) khi chưa sẵn sàng. LatencyMs = TUỔI TICK (ProtoQuote không có timestamp sàn).
public interface ITradeLockerQuoteSession
{
    // Bắn từ luồng nền.
    event Action<TradeLockerSessionEvent>? EventRaised;

    // Đã che token, tiền tố "[TRADELOCKER][LEVEL] ".
    event Action<string>? LogLine;

    string StatusText { get; }

    bool IsConnected { get; }

    void EnsureState(string platformB, TradeLockerConfig config);

    ExchangeMetrics Read(ExchangeMetrics exchangeA, int point, int confirmLatencyMs);
}

public sealed record TradeLockerOrderOutcome(
    bool Success,
    string Detail,
    string? CorrelationId = null,
    bool IsUncertain = false,
    TradeLockerError? Error = null)
{
    public const string NotConnected = "NOT_CONNECTED";
    public const string TimeoutUncertain = "TIMEOUT_UNCERTAIN";
}

// Vị thế, lịch sử và gửi lệnh sàn B TradeLocker — CÙNG socket hermes với ITradeLockerQuoteSession (một instance cài cả
// hai). Vòng đời do ITradeLockerQuoteSession.EnsureState điều khiển. Rule E: chỉ SendOrderAsync gửi lệnh, và chỉ
// TradeLockerTradeExecutor gọi.
public interface ITradeLockerTradeSession
{
    // MapNotFound cho tới khi đã kết nối ∧ resolve symbol ∧ nhận event positions đầu tiên của KẾT NỐI HIỆN TẠI; về
    // MapNotFound NGAY khi rớt. Không bao giờ trả "map rỗng" khi chưa đồng bộ (map rỗng giả ⇒ recovery đóng nhầm chân A).
    // Timestamp = content-version. Ticket = TradeLockerTicketCodec (bit 63).
    SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName);

    // Phase 6: History map B (REST hera). Mặc định MapNotFound.
    SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
        => SharedMapReadResult<HistorySharedRecord>.MapNotFound(mapName);

    // Phase 7: position đang mở (đã khớp) theo id wire. null ⇒ executor PHẢI fail closed.
    TradeLockerPositionView? TryGetOpenPosition(string positionId) => null;

    // Khác null khi KHÔNG nên mở cặp mới (chưa đăng nhập / refresh lỗi / socket rớt / symbol không ACTIVE).
    // Chỉ chặn Open, Close vẫn đi.
    string? OpenBlockReason => null;

    // Symbol đã resolve của kết nối hiện tại (lot_step/min/max/trading_status cho planner). null ⇒ planner fail closed.
    TradeLockerSymbol? CurrentSymbol => null;

    // Phase 7: ĐƯỜNG DUY NHẤT gửi lệnh TradeLocker. Chỉ nhận plan của TradeLockerOrderPlanner. Mặc định fail.
    Task<TradeLockerOrderOutcome> SendOrderAsync(TradeLockerOrderPlan plan, CancellationToken cancellationToken = default)
        => Task.FromResult(new TradeLockerOrderOutcome(false, "SendOrderAsync chưa được cài đặt"));
}

public static class TradeLockerLogMasker
{
    private static readonly Regex JwtRx = new(@"eyJ[\w-]+\.[\w-]+\.[\w-]+", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"((?:access_token|refresh_token)""?\s*[=:]\s*""?)[^&;,""\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return TokenRx.Replace(JwtRx.Replace(text, "<JWT>"), "$1<TOKEN>");
    }
}
