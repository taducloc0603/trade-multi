using QuickFix;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Infrastructure.CTrader;

public sealed record CTraderClosedPosition(
    CTraderPosition Position,
    decimal ClosePrice,
    ulong CloseTimeMsc,
    ulong CloseEaTimeLocal);

// Tập position sàn B dựng từ ExecutionReport (nguồn chính) + PositionReport (lưới an toàn / sync lúc logon).
// THUẦN BỊ ĐỘNG (Rule E): chỉ ghi nhận, không bao giờ gửi lệnh flatten/retry/reconcile.
public sealed class CTraderPositionCache
{
    private const int MaxRememberedExecutions = 4096;

    private readonly Func<long> _tickCount;
    private readonly Dictionary<long, CTraderPosition> _positions = [];
    private readonly HashSet<string> _seenExecutions = [];
    private readonly Queue<string> _seenOrder = new();

    private string? _batchId;
    private readonly Dictionary<long, CTraderPosition> _batch = [];

    public CTraderPositionCache(Func<long>? tickCount = null)
    {
        _tickCount = tickCount ?? (() => Environment.TickCount64);
    }

    // R3: content-version, CHỈ tăng khi tập record thực sự đổi. Không phải UtcNow (rebuild mỗi 500 ms →
    // nút Đóng nuốt click), không phải hằng số (ApplyTradeResult không chạy → open không bao giờ confirm).
    public ulong Version { get; private set; }

    // R2: false cho tới khi nhận xong một batch PositionReport sau logon; về false NGAY khi logout.
    public bool PositionsSynced { get; private set; }

    public int Count => _positions.Count;

    public event Action<CTraderClosedPosition>? PositionClosed;

    public IReadOnlyCollection<CTraderPosition> Positions => _positions.Values;

    public void OnLoggedOut()
    {
        PositionsSynced = false;
        _batchId = null;
        _batch.Clear();
    }

    // Trả true khi tập position đổi.
    public bool ApplyPositionReport(CTraderPositionReport report)
    {
        if (report.PosReqResult == CTraderPositionReport.ResultNoPositions)
        {
            // 728=2 = không có position: batch HOÀN TẤT với danh sách rỗng. Thiếu nhánh này thì tài khoản trống
            // không bao giờ qua được R2 gate.
            _batchId = null;
            _batch.Clear();
            PositionsSynced = true;
            return ReplaceAll([]);
        }

        if (report.PosReqResult != CTraderPositionReport.ResultValid)
        {
            return false;
        }

        if (!string.Equals(_batchId, report.PosReqId, StringComparison.Ordinal))
        {
            _batchId = report.PosReqId;
            _batch.Clear();
        }

        if (report.Position is not null)
        {
            _batch[report.Position.PositionId] = report.Position;
        }

        if (report.TotalNumPosReports > 0 && _batch.Count >= report.TotalNumPosReports)
        {
            var completed = _batch.Values.ToList();
            _batchId = null;
            _batch.Clear();
            PositionsSynced = true;
            return ReplaceAll(completed);
        }

        return false;
    }

    // Chỉ fill thật (150=F) mới đổi position. 150=0 (New) / 150=8 (Rejected) / khác → bỏ qua (R11: market order
    // sinh hai report 150=0 rồi 150=F — không coi cái đầu là khớp). Report trùng (nhiều connection) → bỏ qua.
    public bool ApplyExecutionReport(Message message)
    {
        if (FixFieldReader.MsgType(message) != "8" || FixFieldReader.Char(message, 150) != 'F')
        {
            return false;
        }

        var positionId = FixFieldReader.Long(message, 721);
        var symbolId = FixFieldReader.Int(message, 55);
        var side = FixFieldReader.Char(message, 54);
        var quantity = FixFieldReader.Decimal(message, 32) ?? FixFieldReader.Decimal(message, 14);
        var price = FixFieldReader.Decimal(message, 31) ?? FixFieldReader.Decimal(message, 6);
        if (positionId is null || symbolId is null || side is not ('1' or '2') || quantity is not > 0m || price is null)
        {
            return false;
        }

        if (!RememberExecution(ExecutionKey(message)))
        {
            return false;
        }

        var isBuy = side == '1';
        var transactMs = FixFieldReader.UtcTimestampMs(message, 60);

        if (!_positions.TryGetValue(positionId.Value, out var existing))
        {
            _positions[positionId.Value] = new CTraderPosition(
                positionId.Value, symbolId.Value, isBuy, quantity.Value, price.Value, transactMs, (ulong)_tickCount());
            Version++;
            return true;
        }

        if (existing.IsBuy == isBuy)
        {
            var total = existing.VolumeUnits + quantity.Value;
            var average = (existing.EntryPrice * existing.VolumeUnits + price.Value * quantity.Value) / total;
            _positions[positionId.Value] = existing with { VolumeUnits = total, EntryPrice = average };
            Version++;
            return true;
        }

        var remaining = existing.VolumeUnits - quantity.Value;
        if (remaining > 0m)
        {
            _positions[positionId.Value] = existing with { VolumeUnits = remaining };
            Version++;
            return true;
        }

        _positions.Remove(positionId.Value);
        Version++;
        PositionClosed?.Invoke(new CTraderClosedPosition(existing, price.Value, transactMs, (ulong)_tickCount()));
        return true;
    }

    public IReadOnlyList<TradeSharedRecord> ToTradeRecords(string symbolName, decimal contractSizeB)
        => _positions.Values
            .OrderBy(p => p.PositionId)
            .Select(p => new TradeSharedRecord(
                Ticket: CTraderTicketCodec.Encode(p.PositionId),
                Symbol: symbolName,
                TradeType: p.IsBuy ? 0 : 1,
                Lot: (double)(contractSizeB > 0m ? p.VolumeUnits / contractSizeB : p.VolumeUnits),
                Price: (double)p.EntryPrice,
                Sl: 0,
                Tp: 0,
                Profit: 0,
                TimeMsc: p.OpenTimeMsc,
                OpenEaTimeLocal: p.OpenEaTimeLocal))
            .ToList();

    // R2: map chỉ available khi health cho phép; KHÔNG BAO GIỜ trả IsMapAvailable=true, Count=0 lúc chưa sync.
    public SharedMapReadResult<TradeSharedRecord> ReadAsMapResult(
        CTraderSessionHealth health,
        string mapName,
        string symbolName,
        decimal contractSizeB)
    {
        if (!health.IsMapAvailable)
        {
            return SharedMapReadResult<TradeSharedRecord>.MapNotFound(mapName);
        }

        var records = ToTradeRecords(symbolName, contractSizeB);
        return SharedMapReadResult<TradeSharedRecord>.Success(Version, records, records.Count, health.ConnectedFlag);
    }

    private bool ReplaceAll(IReadOnlyList<CTraderPosition> positions)
    {
        var changed = positions.Count != _positions.Count
            || positions.Any(p => !_positions.TryGetValue(p.PositionId, out var current) || !SameContent(current, p));

        if (!changed)
        {
            return false;
        }

        // Phase 5 (§4.4): AP không mang thời điểm mở/EA time. Position đã biết (từ ER hoặc batch trước) GIỮ stamp cũ;
        // position lần đầu thấy qua AP được stamp Environment.TickCount64 lúc parse — không để 0.
        var stampNow = (ulong)_tickCount();
        var merged = positions
            .Select(p => _positions.TryGetValue(p.PositionId, out var known)
                ? p with { OpenTimeMsc = known.OpenTimeMsc, OpenEaTimeLocal = known.OpenEaTimeLocal }
                : p.OpenEaTimeLocal == 0 ? p with { OpenEaTimeLocal = stampNow } : p)
            .ToList();

        _positions.Clear();
        foreach (var position in merged)
        {
            _positions[position.PositionId] = position;
        }

        Version++;
        return true;
    }

    // AP không mang thời điểm mở/EA time: so theo nội dung giao dịch để reconciliation định kỳ không làm version nhảy.
    private static bool SameContent(CTraderPosition a, CTraderPosition b)
        => a.SymbolId == b.SymbolId && a.IsBuy == b.IsBuy && a.VolumeUnits == b.VolumeUnits && a.EntryPrice == b.EntryPrice;

    private static string ExecutionKey(Message message)
        => FixFieldReader.String(message, 17)
           ?? string.Join('|',
               FixFieldReader.String(message, 11),
               FixFieldReader.String(message, 14),
               FixFieldReader.String(message, 32),
               FixFieldReader.String(message, 60));

    private bool RememberExecution(string key)
    {
        if (!_seenExecutions.Add(key))
        {
            return false;
        }

        _seenOrder.Enqueue(key);
        while (_seenOrder.Count > MaxRememberedExecutions)
        {
            _seenExecutions.Remove(_seenOrder.Dequeue());
        }

        return true;
    }
}
