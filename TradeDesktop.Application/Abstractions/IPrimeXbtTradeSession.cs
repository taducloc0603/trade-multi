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

    // Phase 7: chiều + qty (oz) của sub-position đang mở để executor dựng lệnh đóng. null ⇒ executor PHẢI fail closed.
    (PrimeXbtSide Side, decimal Qty)? TryGetOpenPosition(long positionId) => null;
}
