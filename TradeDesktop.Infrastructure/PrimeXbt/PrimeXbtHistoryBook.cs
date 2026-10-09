using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Infrastructure.PrimeXbt;

// docs/plans/primexbt Phase 6 — History map B dựng từ `report/orders2`. Mỗi lệnh ĐÓNG đã khớp (openReason=CLOSE_POSITION,
// có positionId) của symbol cấu hình = một record; dedupe theo order id; FIFO 528 như MMF history map (giống cTrader).
// Profit = (close − open) × qty × dấu (USD với XAU/USD — cùng đơn vị tiền với MT4/MT5, cột "$" cộng thẳng hai chân);
// giá mở lấy từ vị thế đã thấy lúc còn mở. Không biết giá mở (đóng trước khi app chạy) ⇒ profit = rpl (làm tròn 2 số)
// và giá mở SUY NGƯỢC từ rpl — đánh dấu Estimated. Commission = −fee (fee là phí thu; demo luôn 0).
// Không thread-safe — PrimeXbtFwsSession gọi trong _stateLock.
public sealed class PrimeXbtHistoryBook
{
    public const int DefaultCapacity = 528;
    private const int MaxRememberedPositions = 4096;

    private readonly int _capacity;
    private readonly Func<long> _tickCount;
    private readonly Dictionary<long, PrimeXbtSubPosition> _knownOpen = [];
    private readonly Queue<long> _knownOrder = new();
    private readonly List<Entry> _entries = [];
    private readonly HashSet<long> _orderIds = [];

    public PrimeXbtHistoryBook(Func<long>? tickCount = null, int capacity = DefaultCapacity)
    {
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _capacity = Math.Max(1, capacity);
    }

    // R3: version RIÊNG của history, chỉ tăng khi có record mới.
    public ulong Version { get; private set; }

    public int Count => _entries.Count;

    // Ghi nhớ vị thế đang mở để lúc nó đóng còn biết giá/thời điểm mở (cache vị thế xoá id khi đóng).
    public void RememberOpen(IEnumerable<PrimeXbtSubPosition> positions)
    {
        foreach (var p in positions)
        {
            if (_knownOpen.TryAdd(p.Id, p))
            {
                _knownOrder.Enqueue(p.Id);
                while (_knownOrder.Count > MaxRememberedPositions)
                {
                    _knownOpen.Remove(_knownOrder.Dequeue());
                }
            }
        }
    }

    // Trả (số record mới, trong đó bao nhiêu ước lượng).
    public (int Added, int Estimated) Apply(IReadOnlyList<PrimeXbtOrderReport> orders, string symbol)
    {
        var added = ApplyAndReturnAdded(orders, symbol);
        return (added.Count, added.Count(c => c.ProfitIsEstimated));
    }

    // Như Apply nhưng trả các giao dịch đóng vừa thêm (để log chi tiết).
    public IReadOnlyList<PrimeXbtClosedTrade> ApplyAndReturnAdded(IReadOnlyList<PrimeXbtOrderReport> orders, string symbol)
    {
        var fresh = orders
            .Where(o => string.Equals(o.Symbol, symbol, StringComparison.Ordinal) && !_orderIds.Contains(o.Id))
            .ToList();
        if (fresh.Count == 0)
        {
            return [];
        }

        var openPrices = _knownOpen.ToDictionary(kv => kv.Key, kv => kv.Value.OpenPrice);
        var closed = PrimeXbtHistoryProjector.Project(fresh, openPrices);
        if (closed.Count == 0)
        {
            return [];
        }

        var stamp = (ulong)_tickCount();
        foreach (var c in closed)
        {
            _orderIds.Add(c.CloseOrderId);
            _entries.Add(new Entry(c, _knownOpen.GetValueOrDefault(c.PositionId)?.OpenTime, stamp));
        }

        _entries.Sort((a, b) =>
        {
            var byTime = a.Trade.CloseTime.CompareTo(b.Trade.CloseTime);
            return byTime != 0 ? byTime : a.Trade.CloseOrderId.CompareTo(b.Trade.CloseOrderId);
        });

        while (_entries.Count > _capacity)
        {
            _orderIds.Remove(_entries[0].Trade.CloseOrderId);
            _entries.RemoveAt(0);
        }

        Version++;
        return closed;
    }

    public IReadOnlyList<HistorySharedRecord> ToHistoryRecords(string symbolName, decimal contractSizeB)
        => _entries.Select(e => ToRecord(e, symbolName, contractSizeB)).ToList();

    private static HistorySharedRecord ToRecord(Entry e, string symbolName, decimal contractSizeB)
    {
        var t = e.Trade;
        var sign = t.PositionSide == PrimeXbtSide.Buy ? 1m : -1m;
        var openPrice = t.OpenPrice ?? (t.Qty > 0m ? t.ClosePrice - t.Profit / (t.Qty * sign) : t.ClosePrice);
        return new HistorySharedRecord(
            Ticket: PrimeXbtTicketCodec.Encode(t.PositionId),
            TradeType: t.PositionSide == PrimeXbtSide.Buy ? 0 : 1,
            Volume: (double)(contractSizeB > 0m ? t.Qty / contractSizeB : t.Qty),
            OpenPrice: (double)openPrice,
            ClosePrice: (double)t.ClosePrice,
            Sl: 0,
            Tp: 0,
            Commission: (double)(-t.Fee),
            Profit: (double)t.Profit,
            OpenTimeMsc: e.OpenTime is { } open ? (ulong)Math.Max(0, open.ToUnixTimeMilliseconds()) : 0,
            CloseTimeMsc: (ulong)Math.Max(0, t.CloseTime.ToUnixTimeMilliseconds()),
            CloseEaTimeLocal: e.FirstSeenStamp,
            Symbol: symbolName);
    }

    private sealed record Entry(PrimeXbtClosedTrade Trade, DateTimeOffset? OpenTime, ulong FirstSeenStamp);
}
