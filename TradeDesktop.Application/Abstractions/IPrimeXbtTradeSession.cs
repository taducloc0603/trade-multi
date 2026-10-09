using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Application.Abstractions;

// docs/plans/primexbt Phase 5 — vị thế mở sàn B (CHỈ ĐỌC). Cùng socket `fws` với IPrimeXbtQuoteSession (một instance
// PrimeXbtFwsSession cài cả hai): snapshot `positions` đi chung kênh với giá, mở socket thứ hai là thừa. Vòng đời do
// IPrimeXbtQuoteSession.EnsureState điều khiển. KHÔNG có đường gửi lệnh (Rule E) — executor vẫn là Null tới Phase 7.
public interface IPrimeXbtTradeSession
{
    // P4/R2: MapNotFound cho tới khi đã kết nối ∧ resolve symbol ∧ nhận snapshot `positions` hợp lệ của KẾT NỐI HIỆN TẠI
    // ∧ positionMode = HEDGE (P8); về MapNotFound NGAY khi rớt. Không bao giờ trả "map rỗng" khi chưa đồng bộ — map rỗng
    // giả sẽ khiến recovery đóng nhầm chân A. R3: Timestamp = content-version. R4: Ticket = PrimeXbtTicketCodec.Encode(subId).
    SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName);

    // Phase 6: History map B — cùng cổng sức khoẻ với trades + đã nhận ít nhất một `report/orders2` của kết nối hiện tại.
    // Timestamp = version RIÊNG của history. Profit = tiền (USD), tự tính; Commission = −fee.
    SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
        => SharedMapReadResult<HistorySharedRecord>.MapNotFound(mapName);

    // Phase 7: chiều + qty (oz) của sub-position đang mở để executor dựng lệnh đóng. null ⇒ executor PHẢI fail closed.
    (PrimeXbtSide Side, decimal Qty)? TryGetOpenPosition(long positionId) => null;

    // Phase 7: min/step/max của symbol (route `trade-settings`) cho PrimeXbtOrderPlanner. null ⇒ planner fail closed.
    PrimeXbtTradeSettings? CurrentTradeSettings => null;

    // Phase 7: ĐƯỜNG DUY NHẤT gửi lệnh PrimeXBT, chỉ PrimeXbtTradeExecutor gọi khi router ra lệnh. Chỉ nhận plan của
    // PrimeXbtOrderPlanner (mở market / đóng ĐÚNG một sub-position). Success = RESPONSE không lỗi — KHÔNG có nghĩa đã có
    // vị thế (xác nhận qua Trades map, D2). Timeout/rớt socket sau khi gửi ⇒ IsUncertain, KHÔNG BAO GIỜ gửi lại (Q5: lệnh
    // vẫn có thể khớp); session đối soát và chỉ báo cáo (Rule E). Mặc định fail để fake/test cũ không "đặt lệnh được".
    Task<PrimeXbtOrderOutcome> SendOrderAsync(PrimeXbtOrderPlan plan, CancellationToken cancellationToken = default)
        => Task.FromResult(new PrimeXbtOrderOutcome(false, "SendOrderAsync chưa được cài đặt"));
}

public sealed record PrimeXbtOrderOutcome(bool Success, string Detail, long? OrderId = null, bool IsUncertain = false)
{
    public const string NotConnected = "NOT_CONNECTED";
    public const string TimeoutUncertain = "TIMEOUT_UNCERTAIN";
}
