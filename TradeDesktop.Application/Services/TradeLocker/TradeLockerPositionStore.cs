using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.TradeLocker;

public sealed record TradeLockerPositionView(
    string Id,
    string TradableUnitId,
    TradeLockerSide Side,
    int Status,
    decimal Quantity,
    decimal? EntryPrice,
    long CreatedAtMs,
    decimal Commission,
    decimal Swap);

// Vị thế mở + order của chúng từ `v3.position-aggregates` (port PositionStore.view() của scan\trade-locker):
// qty ròng = Σ filled(INCREASE) − Σ filled(DECREASE); giá vào = VWAP các order INCREASE.
// Event aggregate là upsert (snapshot đầu đầy đủ, sau đó là delta); positions_deleted xoá position + order của nó.
// THUẦN BỊ ĐỘNG (Rule E). Không thread-safe — session gọi trong lock của nó.
public sealed class TradeLockerPositionStore
{
    private readonly Func<long> _tickCount;
    private readonly Dictionary<string, TradeLockerPositionMsg> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TradeLockerOrderMsg> _orders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _firstSeenStamp = new(StringComparer.Ordinal);
    private Dictionary<string, (TradeLockerSide Side, decimal Qty, decimal? Entry)> _lastContent = new(StringComparer.Ordinal);

    public TradeLockerPositionStore(Func<long>? tickCount = null)
    {
        _tickCount = tickCount ?? (() => Environment.TickCount64);
    }

    // Chỉ tăng khi tập (id, chiều, qty, giá vào) của position có qty > 0 đổi — giống content-version của PrimeXBT.
    public ulong Version { get; private set; }

    // false cho tới event hợp lệ đầu tiên của kết nối hiện tại; về false ngay khi rớt / event hỏng.
    public bool Synced { get; private set; }

    public void MarkUnsynced() => Synced = false;

    // Kết nối mới: snapshot đầu là toàn bộ trạng thái ⇒ xoá dữ liệu kết nối cũ (position đóng lúc rớt mạng không bị giữ).
    public void ResetForNewConnection()
    {
        _positions.Clear();
        _orders.Clear();
        Synced = false;
    }

    public bool Apply(TradeLockerPositionEvent positionEvent)
    {
        if (positionEvent.IsDeletion)
        {
            foreach (var id in positionEvent.DeletedIds)
            {
                _positions.Remove(id);
                foreach (var orderId in _orders.Where(o => o.Value.PositionId == id).Select(o => o.Key).ToList())
                {
                    _orders.Remove(orderId);
                }
            }
        }
        else
        {
            foreach (var p in positionEvent.Positions)
            {
                _positions[p.Id] = p;
            }

            foreach (var o in positionEvent.Orders.Where(o => o.Id.Length > 0))
            {
                _orders[o.Id] = o;
            }
        }

        Synced = true;
        return RefreshVersion();
    }

    public IReadOnlyList<TradeLockerPositionView> All() => _positions.Values.Select(View).ToList();

    // Chỉ position đã khớp (qty > 0): NEW chưa khớp không phải vị thế vật lý.
    public IReadOnlyList<TradeLockerPositionView> Open() => All().Where(p => p.Quantity > 0m).ToList();

    public TradeLockerPositionView? Get(string id) => _positions.TryGetValue(id, out var p) ? View(p) : null;

    public IReadOnlyCollection<string> Ids => _positions.Keys;

    // Lot = quantity (đơn vị wire là lot, khác PrimeXBT dùng oz). Profit = 0: app tự tính từ giá (như MMF/PrimeXBT).
    public IReadOnlyList<TradeSharedRecord> ToTradeRecords(Func<string, string?> symbolOfTradableUnit)
        => Open()
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => TradeLockerTicketCodec.TryEncode(p.Id, out var ticket)
                ? new TradeSharedRecord(
                    Ticket: ticket,
                    Symbol: symbolOfTradableUnit(p.TradableUnitId) ?? p.TradableUnitId,
                    TradeType: p.Side == TradeLockerSide.Buy ? 0 : 1,
                    Lot: (double)p.Quantity,
                    Price: (double)(p.EntryPrice ?? 0m),
                    Sl: 0,
                    Tp: 0,
                    Profit: 0,
                    TimeMsc: (ulong)Math.Max(0, p.CreatedAtMs),
                    OpenEaTimeLocal: _firstSeenStamp.GetValueOrDefault(p.Id))
                : null)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

    private TradeLockerPositionView View(TradeLockerPositionMsg p)
    {
        decimal qty = 0m, notional = 0m, opened = 0m;
        foreach (var o in _orders.Values)
        {
            if (o.PositionId != p.Id)
            {
                continue;
            }

            if (o.Intent == TradeLockerIntent.Increase)
            {
                qty += o.FilledQuantity;
                opened += o.FilledQuantity;
                if (o.FilledQuantity > 0m && o.AveragePrice is { } avg)
                {
                    notional += o.FilledQuantity * avg;
                }
            }
            else if (o.Intent == TradeLockerIntent.Decrease)
            {
                qty -= o.FilledQuantity;
            }
        }

        return new TradeLockerPositionView(
            p.Id,
            p.TradableUnitId,
            p.Side,
            p.Status,
            Math.Max(0m, qty),
            opened > 0m ? notional / opened : null,
            p.CreatedAtMs,
            p.TotalCommission,
            p.TotalSwap);
    }

    private bool RefreshVersion()
    {
        var content = Open().ToDictionary(p => p.Id, p => (p.Side, p.Quantity, p.EntryPrice), StringComparer.Ordinal);
        var changed = content.Count != _lastContent.Count
            || content.Any(kv => !_lastContent.TryGetValue(kv.Key, out var old) || old != kv.Value);
        if (!changed)
        {
            return false;
        }

        var stampNow = (ulong)_tickCount();
        foreach (var id in content.Keys)
        {
            _firstSeenStamp.TryAdd(id, stampNow);
        }

        foreach (var gone in _firstSeenStamp.Keys.Where(id => !content.ContainsKey(id)).ToList())
        {
            _firstSeenStamp.Remove(gone);
        }

        _lastContent = content;
        Version++;
        return true;
    }
}
