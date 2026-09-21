using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Infrastructure.CTrader;

// Position đóng hẳn → HistorySharedRecord. Nguồn sự thật "đã đóng" là position BIẾN MẤT khỏi cache
// (CTraderPositionCache.PositionClosed), không suy theo chiều lệnh.
// R10: FIX không trả P&L/commission → Commission = 0, Profit là số TÍNH LẠI theo điểm giá, không phải số broker.
public sealed class CTraderHistoryProjector
{
    // Tương đương MMF history map: HISTORY_MEMORY_SIZE 65536 / HISTORY_RECORD_SIZE 124 ≈ 528 record, FIFO.
    public const int DefaultCapacity = 528;

    private readonly int _capacity;
    private readonly LinkedList<CTraderClosedPosition> _closed = new();

    public CTraderHistoryProjector(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    // Content-version RIÊNG cho history — độc lập với trades (ShouldApplyHistoryResult theo dõi riêng).
    public ulong Version { get; private set; }

    public int Count => _closed.Count;

    public void OnPositionClosed(CTraderClosedPosition closed)
    {
        _closed.AddLast(closed);
        while (_closed.Count > _capacity)
        {
            _closed.RemoveFirst();
        }

        Version++;
    }

    public IReadOnlyList<HistorySharedRecord> ToHistoryRecords(string symbolName, decimal contractSizeB, int point)
        => _closed
            .Select(c =>
            {
                var p = c.Position;
                var move = p.IsBuy ? c.ClosePrice - p.EntryPrice : p.EntryPrice - c.ClosePrice;
                return new HistorySharedRecord(
                    Ticket: CTraderTicketCodec.Encode(p.PositionId),
                    TradeType: p.IsBuy ? 0 : 1,
                    Volume: (double)(contractSizeB > 0m ? p.VolumeUnits / contractSizeB : p.VolumeUnits),
                    OpenPrice: (double)p.EntryPrice,
                    ClosePrice: (double)c.ClosePrice,
                    Sl: 0,
                    Tp: 0,
                    Commission: 0,
                    Profit: (double)(move * Math.Max(1, point)),
                    OpenTimeMsc: p.OpenTimeMsc,
                    CloseTimeMsc: c.CloseTimeMsc,
                    CloseEaTimeLocal: c.CloseEaTimeLocal,
                    Symbol: symbolName);
            })
            .ToList();

    public SharedMapReadResult<HistorySharedRecord> ReadAsMapResult(
        CTraderSessionHealth health,
        string mapName,
        string symbolName,
        decimal contractSizeB,
        int point)
    {
        if (!health.IsMapAvailable)
        {
            return SharedMapReadResult<HistorySharedRecord>.MapNotFound(mapName);
        }

        var records = ToHistoryRecords(symbolName, contractSizeB, point);
        return SharedMapReadResult<HistorySharedRecord>.Success(Version, records, records.Count, health.ConnectedFlag);
    }
}
