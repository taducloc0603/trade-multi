using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public enum CTraderTradeEventKind
{
    Connecting = 0,
    LoggedOn = 1,
    LoggedOut = 2,
    PositionsSynced = 3,
    PositionsNotSynced = 4,
    Stopped = 5,
    Error = 6
}

public sealed record CTraderTradeSessionEvent(CTraderTradeEventKind Kind, string Message);

// Phase 5 — luồng LỆNH ĐANG MỞ sàn B qua FIX TRADE session. CHỈ nhận: SecurityList, RequestForPositions/PositionReport,
// ExecutionReport. KHÔNG có đường gửi order (Rule E) — NullCTraderTradeExecutor vẫn giữ chỗ tới Phase 7.
public interface ICTraderTradeSession
{
    event Action<CTraderTradeSessionEvent>? EventRaised;

    // Dòng log đã che 554, prefix [CTRADER][TRADE][LEVEL].
    event Action<string>? LogLine;

    string StatusText { get; }

    // Giống QUOTE (G4): platform_b != ctrader → không mở socket; đổi khỏi ctrader → logout + giải phóng. Không throw.
    void EnsureState(string platformB, CTraderFixConfig config);

    // R2: MapNotFound cho tới TradeLoggedOn && SymbolResolved && PositionsSynced; quay lại MapNotFound NGAY khi đứt.
    // R3: Timestamp = content-version. R4: Ticket = CTraderTicketCodec.Encode(positionId). Connected ∈ {0,1}.
    SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName, bool quoteLoggedOn);

    // Phase 6: history B. R2 cùng điều kiện với trades; Timestamp = version RIÊNG của history (R3); Profit tính lại từ
    // (Close−Open)×point, Commission = 0 (R10). Record chỉ sinh khi position đóng hẳn qua fill.
    SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName, bool quoteLoggedOn, int point);
}
