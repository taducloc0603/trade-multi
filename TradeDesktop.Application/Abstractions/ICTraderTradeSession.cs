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
    Error = 6,
    // Ngắt mạch: quá nhiều lần mất phiên trong một phút → dừng hẳn, KHÔNG tự nối lại.
    ReconnectStorm = 7
}

public sealed record CTraderTradeSessionEvent(CTraderTradeEventKind Kind, string Message);

// Phase 7 Bước C — lệnh market gửi qua FIX. `PositionId` null = mở position mới; có giá trị = tác động vào
// position đó (đóng = side ngược + đủ volume, R1 đã chứng minh live 2026-09-23).
public sealed record CTraderOrderRequest(string ClOrdId, bool IsBuy, decimal QuantityUnits, long? PositionId);

// `Success` chỉ đúng khi sàn trả `150=F` VÀ `39=2`. Reject mang nguyên văn tag 58 (tag 103 luôn 0 — R11).
// Timeout cũng là `Success=false`: executor phải để router đi đường partial-open/rollback sẵn có.
public sealed record CTraderOrderOutcome(bool Success, string Detail, long? PositionId = null);

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

    // Phase 7 Bước C: ĐƯỜNG DUY NHẤT gửi lệnh ra sàn B, chỉ được gọi từ CTraderTradeExecutor khi router ra lệnh.
    // Rule E: session KHÔNG tự gọi hàm này ở bất kỳ nhánh nào — không flatten, không retry, không reconcile bằng lệnh.
    // Mặc định trả Success=false để fake/test cũ không vô tình "đặt lệnh được".
    Task<CTraderOrderOutcome> SendMarketOrderAsync(CTraderOrderRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CTraderOrderOutcome(false, "SendMarketOrderAsync chưa được cài đặt"));

    // Volume của position đang mở (đơn vị cơ sở) + chiều, để executor dựng lệnh đóng ngược chiều đúng khối lượng.
    // null = không có position đó trong cache → executor PHẢI fail closed, không đoán.
    (bool IsBuy, decimal QuantityUnits)? TryGetOpenPosition(long positionId) => null;
}
