namespace TradeDesktop.Application.Models;

// Bảng sự thật sức khoẻ session cTrader — bề mặt an toàn của cả kế hoạch (README §4.7).
// Cả hai cờ đều fail-closed: thiếu BẤT KỲ điều kiện nào là coi như sàn B không dùng được.
public sealed record CTraderSessionHealth(
    bool QuoteLoggedOn,
    bool TradeLoggedOn,
    bool SymbolResolved,
    bool PositionsSynced,
    bool HasTopOfBook,
    long LastQuoteTickCount)
{
    public static CTraderSessionHealth Disconnected { get; } = new(false, false, false, false, false, 0);

    // ExchangeMetrics(B).IsConnected → guard !metrics.IsConnectedB ở router chặn mọi open/close.
    public bool IsQuoteConnected => QuoteLoggedOn && SymbolResolved && HasTopOfBook;

    // R2: trades/history map chỉ available khi đã sync xong positions. Trả true khi chưa sync =
    // GetLivePairTradeState → OnlyAOpen → đóng nhầm chân A của hedge đang mở thật.
    public bool IsMapAvailable => TradeLoggedOn && SymbolResolved && PositionsSynced;

    // Header MMF: 1 khi cả hai session logged on, 0 khi không, không bao giờ -1 (README §4.5).
    public int ConnectedFlag => QuoteLoggedOn && TradeLoggedOn ? 1 : 0;
}
