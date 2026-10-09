using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Infrastructure.PrimeXbt;

// Tập sub-position sàn B dựng từ snapshot `positions` (mỗi frame — RESPONSE lẫn EVENT — là snapshot ĐẦY ĐỦ, ~1 s/lần khi
// có vị thế). THUẦN BỊ ĐỘNG (Rule E): chỉ ghi nhận. Không thread-safe — PrimeXbtFwsSession gọi trong _stateLock.
public sealed class PrimeXbtPositionCache
{
    private readonly Func<long> _tickCount;
    private readonly Dictionary<long, PrimeXbtSubPosition> _positions = [];
    private readonly Dictionary<long, ulong> _firstSeenStamp = [];

    public PrimeXbtPositionCache(Func<long>? tickCount = null)
    {
        _tickCount = tickCount ?? (() => Environment.TickCount64);
    }

    // R3: content-version, CHỈ tăng khi tập (id, chiều, qty, giá mở) đổi. `upl`/`markPrice` đổi mỗi giây nhưng KHÔNG
    // tăng version: record không mang profit (Profit = 0, app tự tính từ giá) nên version nhảy chỉ làm grid dựng lại.
    public ulong Version { get; private set; }

    // P4: false cho tới snapshot hợp lệ đầu tiên của kết nối hiện tại; về false NGAY khi rớt / snapshot hỏng.
    public bool Synced { get; private set; }

    // positionMode của snapshot gần nhất (P8: khác HEDGE ⇒ map không available).
    public string PositionMode { get; private set; } = string.Empty;

    public int Count => _positions.Count;

    public IReadOnlyCollection<PrimeXbtSubPosition> Positions => _positions.Values;

    public void MarkUnsynced() => Synced = false;

    // Trả true khi tập vị thế đổi. Snapshot không hợp lệ ⇒ mất đồng bộ (fail-closed), GIỮ nội dung cũ (không coi là
    // "đã đóng hết") cho tới snapshot hợp lệ kế tiếp.
    public bool Apply(PrimeXbtPositionsSnapshot snapshot)
    {
        if (!snapshot.IsValid)
        {
            Synced = false;
            return false;
        }

        PositionMode = snapshot.PositionMode;
        Synced = true;

        var changed = snapshot.Positions.Count != _positions.Count
            || snapshot.Positions.Any(p => !_positions.TryGetValue(p.Id, out var current) || !SameContent(current, p));
        if (!changed)
        {
            return false;
        }

        var stampNow = (ulong)_tickCount();
        _positions.Clear();
        foreach (var position in snapshot.Positions)
        {
            _positions[position.Id] = position;
            // Stamp thật lúc LẦN ĐẦU thấy sub id, giữ nguyên qua các snapshot sau (không để 0).
            _firstSeenStamp.TryAdd(position.Id, stampNow);
        }

        foreach (var gone in _firstSeenStamp.Keys.Where(id => !_positions.ContainsKey(id)).ToList())
        {
            _firstSeenStamp.Remove(gone);
        }

        Version++;
        return true;
    }

    public PrimeXbtSubPosition? TryGet(long positionId) => _positions.GetValueOrDefault(positionId);

    public IReadOnlyList<TradeSharedRecord> ToTradeRecords(string symbolName, decimal contractSizeB)
        => _positions.Values
            .OrderBy(p => p.Id)
            .Select(p => new TradeSharedRecord(
                Ticket: PrimeXbtTicketCodec.Encode(p.Id),
                Symbol: symbolName,
                TradeType: p.Side == PrimeXbtSide.Buy ? 0 : 1,
                Lot: (double)(contractSizeB > 0m ? p.Qty / contractSizeB : p.Qty),
                Price: (double)p.OpenPrice,
                Sl: 0,
                Tp: 0,
                Profit: 0,
                TimeMsc: (ulong)Math.Max(0, p.OpenTime.ToUnixTimeMilliseconds()),
                OpenEaTimeLocal: _firstSeenStamp.GetValueOrDefault(p.Id)))
            .ToList();

    private static bool SameContent(PrimeXbtSubPosition a, PrimeXbtSubPosition b)
        => a.Side == b.Side && a.Qty == b.Qty && a.OpenPrice == b.OpenPrice && a.Symbol == b.Symbol;
}
