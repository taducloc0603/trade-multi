using QuickFix;

namespace TradeDesktop.Infrastructure.CTrader;

// Top-of-book cho symbol sàn B ở chế độ SPOT (264=1).
// Phase 0 câu 2 (live 2026-09-16): spot KHÔNG gửi X incremental, không có tag 278 — mỗi tick là một W đầy đủ
// 2 entry (269=0 bid, 269=1 offer). Vì vậy mỗi W thay TOÀN BỘ book. Nhận X khi đang spot = bất thường
// → xoá book (fail-closed, IsConnected=false) thay vì đoán ý nghĩa (quyết định Phase 3 câu 3).
public sealed class CTraderQuoteBook
{
    public const string AnomalyIncrementalInSpot = "INCREMENTAL_REFRESH_IN_SPOT_MODE";

    private readonly int _symbolId;
    private readonly Func<long> _tickCount;

    public CTraderQuoteBook(int symbolId, Func<long>? tickCount = null)
    {
        _symbolId = symbolId;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
    }

    public decimal? Bid { get; private set; }
    public decimal? Ask { get; private set; }

    // R9: không bao giờ phục vụ giá cũ khi book rỗng — cả hai phía phải có từ cùng một W.
    public bool HasTopOfBook => Bid is > 0m && Ask is > 0m;

    // Tuổi tick = Environment.TickCount64 - LastQuoteTickCount (README §4.3), miễn nhiễm clock skew broker.
    public long LastQuoteTickCount { get; private set; }

    // Stamp đơn điệu theo từng quote cho MarkAndCheckNewTick/TPS (tăng mỗi W hợp lệ).
    public long QuoteSequence { get; private set; }

    // Tag 52 SendingTime — spot không có 273 MDEntryTime (Phase 0 câu 2). Chỉ để hiển thị.
    public ulong LastSendingTimeMs { get; private set; }

    public string? LastAnomaly { get; private set; }

    // Trả true khi book đổi (W hợp lệ). X hoặc W symbol khác → false.
    public bool Apply(Message message)
    {
        switch (FixFieldReader.MsgType(message))
        {
            case "W":
                return ApplySnapshot(message);
            case "X":
                LastAnomaly = AnomalyIncrementalInSpot;
                Clear();
                return false;
            default:
                return false;
        }
    }

    // Mất session → xoá book ngay (R9): IsConnected=false ở tick 50 ms kế tiếp, không chờ price_freeze_ms.
    public void Clear()
    {
        Bid = null;
        Ask = null;
    }

    private bool ApplySnapshot(Message message)
    {
        if (FixFieldReader.Int(message, 55) != _symbolId)
        {
            return false;
        }

        decimal? bid = null;
        decimal? ask = null;
        foreach (var entry in FixFieldReader.Groups(message, 268))
        {
            var type = FixFieldReader.Char(entry, 269);
            var price = FixFieldReader.Decimal(entry, 270);
            if (type == '0')
            {
                bid = price;
            }
            else if (type == '1')
            {
                ask = price;
            }
        }

        Bid = bid;
        Ask = ask;
        LastAnomaly = null;
        LastQuoteTickCount = _tickCount();
        QuoteSequence++;
        LastSendingTimeMs = FixFieldReader.UtcTimestampMs(message.Header, 52);
        return true;
    }
}
