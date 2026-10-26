using System.Globalization;

namespace TradeDesktop.Application.Services.TradeLocker;

public enum TradeLockerSide
{
    Unspecified = 0,
    Buy = 1,
    Sell = 2
}

public enum TradeLockerIntent
{
    Unspecified = 0,
    Increase = 1,
    Decrease = 2
}

// ProtoPositionStatus (descriptors.json, trích 2026-10-26).
public static class TradeLockerPositionStatus
{
    public const int New = 1;
    public const int Open = 2;
    public const int Closing = 3;
    public const int Closed = 4;
    public const int Terminated = 5;
    public const int Aborting = 6;
    public const int Aborted = 7;
}

// ProtoOrderStatus: 8..16 = đã kết thúc (filled/canceled/rejected/expired/terminated) — như `isLive` của scan\trade-locker.
public static class TradeLockerOrderStatus
{
    public const int Filled = 8;
    public const int Rejected = 14;

    public static bool IsLive(int status) => status is < 8 or > 16;
}

public sealed record TradeLockerQuote(decimal Bid, decimal Ask);

public sealed record TradeLockerPositionMsg(
    string Id,
    string TradableUnitId,
    TradeLockerSide Side,
    int Status,
    long CreatedAtMs,
    decimal TotalCommission,
    decimal TotalSwap);

// Một order của position (oneof ProtoOrder). Kind = số field oneof: 1 market, 2 limit, 3 stop, 4 stop-limit,
// 5 trailing stop, 6 market-if-touched. Mỗi loại đánh số field khác nhau — xem TradeLockerMessages.OrderLayouts.
public sealed record TradeLockerOrderMsg(
    int Kind,
    string Id,
    string PositionId,
    TradeLockerSide Side,
    int Status,
    TradeLockerIntent Intent,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    decimal? AveragePrice);

public sealed record TradeLockerPositionEvent(
    IReadOnlyList<TradeLockerPositionMsg> Positions,
    IReadOnlyList<TradeLockerOrderMsg> Orders,
    IReadOnlyList<string> DeletedIds)
{
    public bool IsDeletion => DeletedIds.Count > 0;
}

public sealed record TradeLockerTicker(
    string Id,
    string Name,
    decimal TickSize,
    decimal LotSize,
    decimal LotStep,
    decimal MinimumLot,
    decimal MaximumLot);

public sealed record TradeLockerInstrument(string Id, string Code, string Name, int TradingStatus, string TickerId);

public sealed record TradeLockerTradableUnit(string Id, string InstrumentId);

public sealed record TradeLockerTradableUnitsEvent(
    IReadOnlyList<TradeLockerTradableUnit> TradableUnits,
    IReadOnlyList<TradeLockerInstrument> Instruments,
    IReadOnlyList<TradeLockerTicker> Tickers);

// ProtoError (code, description). Đến từ frame ERROR của request-response, message = base64(ProtoError).
public sealed record TradeLockerError(string Code, string Description);

// ProtoNotification: chỉ giữ những gì executor cần — correlation id, loại (số field oneof) và chi tiết reject.
public sealed record TradeLockerNotification(string CorrelationId, int Kind, string Title, string Body, string RejectDescription)
{
    public const int KindOrderRejected = 3;
    public bool IsOrderRejected => Kind == KindOrderRejected;
}

public static class TradeLockerMessages
{
    private readonly record struct OrderLayout(int Requested, int Position, int Status, int Filled, int Average, int Id, int Intent);

    // Số field theo loại order (descriptors.json): market khác hẳn các loại còn lại.
    private static readonly Dictionary<int, OrderLayout> OrderLayouts = new()
    {
        [1] = new OrderLayout(Requested: 2, Position: 5, Status: 6, Filled: 8, Average: 9, Id: 13, Intent: 15),
        [2] = new OrderLayout(Requested: 3, Position: 6, Status: 7, Filled: 9, Average: 10, Id: 14, Intent: 16),
        [3] = new OrderLayout(Requested: 3, Position: 6, Status: 7, Filled: 9, Average: 10, Id: 14, Intent: 16),
        [4] = new OrderLayout(Requested: 4, Position: 7, Status: 8, Filled: 10, Average: 11, Id: 15, Intent: 17),
        [5] = new OrderLayout(Requested: 4, Position: 7, Status: 8, Filled: 10, Average: 11, Id: 15, Intent: 17),
        [6] = new OrderLayout(Requested: 3, Position: 6, Status: 7, Filled: 9, Average: 10, Id: 14, Intent: 16),
    };

    // ProtoQuote { ask = 1; bid = 2; } — chuỗi số; không parse được / <= 0 ⇒ false (không bao giờ đoán giá).
    public static bool TryDecodeQuote(ReadOnlyMemory<byte> data, out TradeLockerQuote quote)
    {
        quote = new TradeLockerQuote(0m, 0m);
        if (!ProtoMessage.TryParse(data, out var m)
            || !TryDecimal(m.String(1), out var ask)
            || !TryDecimal(m.String(2), out var bid)
            || ask <= 0m || bid <= 0m)
        {
            return false;
        }

        quote = new TradeLockerQuote(bid, ask);
        return true;
    }

    // ProtoPositionAggregateEvent { positions_aggregate = 1 { positions = 1; orders = 2 }; positions_deleted = 2 { ids = 1 } }.
    public static bool TryDecodePositionEvent(ReadOnlyMemory<byte> data, out TradeLockerPositionEvent positionEvent)
    {
        positionEvent = new TradeLockerPositionEvent([], [], []);
        if (!ProtoMessage.TryParse(data, out var m))
        {
            return false;
        }

        if (m.Message(2) is { } deleted)
        {
            positionEvent = new TradeLockerPositionEvent([], [], deleted.Strings(1).ToList());
            return true;
        }

        if (m.Message(1) is not { } aggregate)
        {
            return false;
        }

        var positions = new List<TradeLockerPositionMsg>();
        foreach (var p in aggregate.Messages(1))
        {
            if (string.IsNullOrEmpty(p.String(1)))
            {
                return false; // position không id = dạng lạ ⇒ fail-closed
            }

            positions.Add(new TradeLockerPositionMsg(
                p.String(1),
                p.String(3),
                (TradeLockerSide)p.Int(4),
                p.Int(5),
                TryLong(p.String(6)),
                DecimalOrZero(p.String(7)),
                DecimalOrZero(p.String(8))));
        }

        var orders = new List<TradeLockerOrderMsg>();
        foreach (var wrapper in aggregate.Messages(2))
        {
            var kind = wrapper.FieldNumbers.FirstOrDefault(OrderLayouts.ContainsKey);
            if (kind == 0 || wrapper.Message(kind) is not { } o)
            {
                continue; // loại order chưa biết: bỏ qua, không làm hỏng cả snapshot
            }

            var layout = OrderLayouts[kind];
            orders.Add(new TradeLockerOrderMsg(
                kind,
                o.String(layout.Id),
                o.String(layout.Position),
                (TradeLockerSide)o.Int(1),
                o.Int(layout.Status),
                (TradeLockerIntent)o.Int(layout.Intent),
                DecimalOrZero(o.String(layout.Requested)),
                DecimalOrZero(o.String(layout.Filled)),
                TryDecimal(o.String(layout.Average), out var avg) ? avg : null));
        }

        positionEvent = new TradeLockerPositionEvent(positions, orders, []);
        return true;
    }

    // ProtoTradableUnitAggregateEvent { tradable_units_aggregate = 1 { tradable_units = 1; instruments = 2; tickers = 3 } }.
    public static bool TryDecodeTradableUnits(ReadOnlyMemory<byte> data, out TradeLockerTradableUnitsEvent unitsEvent)
    {
        unitsEvent = new TradeLockerTradableUnitsEvent([], [], []);
        if (!ProtoMessage.TryParse(data, out var m))
        {
            return false;
        }

        if (m.Message(1) is not { } agg)
        {
            return m.Has(2); // tradable_units_deleted: hợp lệ nhưng không có gì để thêm
        }

        unitsEvent = new TradeLockerTradableUnitsEvent(
            agg.Messages(1).Select(u => new TradeLockerTradableUnit(u.String(1), u.String(4))).ToList(),
            agg.Messages(2).Select(i => new TradeLockerInstrument(i.String(1), i.String(2), i.String(3), i.Int(6), i.String(7))).ToList(),
            agg.Messages(3).Select(t => new TradeLockerTicker(
                t.String(1),
                t.String(2),
                DecimalOrZero(t.String(8)),
                DecimalOrZero(t.String(9)),
                DecimalOrZero(t.String(10)),
                DecimalOrZero(t.String(11)),
                DecimalOrZero(t.String(12)))).ToList());
        return true;
    }

    // ProtoCommandAck { correlation_id = 1 }.
    public static string DecodeCommandAck(ReadOnlyMemory<byte> data)
        => ProtoMessage.TryParse(data, out var m) ? m.String(1) : string.Empty;

    public static bool TryDecodeError(ReadOnlyMemory<byte> data, out TradeLockerError error)
    {
        error = new TradeLockerError(string.Empty, string.Empty);
        if (!ProtoMessage.TryParse(data, out var m) || (!m.Has(1) && !m.Has(2)))
        {
            return false;
        }

        error = new TradeLockerError(m.String(1), m.String(2));
        return true;
    }

    // Message của frame ERROR: base64(ProtoError). Không phải base64/ProtoError ⇒ giữ nguyên văn làm Description.
    public static TradeLockerError DecodeErrorFrameMessage(string message)
    {
        try
        {
            if (TryDecodeError(Convert.FromBase64String(message), out var error))
            {
                return error;
            }
        }
        catch (FormatException)
        {
        }

        return new TradeLockerError(string.Empty, message);
    }

    // ProtoNotification { correlation_id = 1; title = 5; body = 6; oneof 2..47 }. order_rejected = 3 { error_code = 1; description = 2 }.
    public static bool TryDecodeNotification(ReadOnlyMemory<byte> data, out TradeLockerNotification notification)
    {
        notification = new TradeLockerNotification(string.Empty, 0, string.Empty, string.Empty, string.Empty);
        if (!ProtoMessage.TryParse(data, out var m))
        {
            return false;
        }

        var kind = m.FieldNumbers.Where(n => n is >= 2 and <= 47 and not 5 and not 6).DefaultIfEmpty(0).First();
        var reject = kind == TradeLockerNotification.KindOrderRejected ? m.Message(kind)?.String(2) ?? string.Empty : string.Empty;
        notification = new TradeLockerNotification(m.String(1), kind, m.String(5), m.String(6), reject);
        return true;
    }

    // ProtoPositionOpenRequest { side = 1; tradable_unit_id = 2; orders = 3 { market_order = 1 { side = 1; quantity = 2;
    // time_in_force = 3 (IOC = 2); intent = 5 (INCREASE = 1) } } } — đúng frame web đã bắt (fixtures/README.md).
    public static byte[] EncodeMarketOpenRequest(TradeLockerSide side, string tradableUnitId, string quantity)
    {
        var market = new ProtoWriter()
            .Varint(1, (ulong)side)
            .String(2, quantity)
            .Varint(3, 2)
            .Varint(5, (ulong)TradeLockerIntent.Increase);
        return new ProtoWriter()
            .Varint(1, (ulong)side)
            .String(2, tradableUnitId)
            .Message(3, new ProtoWriter().Message(1, market))
            .ToArray();
    }

    public static bool TryDecimal(string? text, out decimal value)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static decimal DecimalOrZero(string? text) => TryDecimal(text, out var v) ? v : 0m;

    private static long TryLong(string? text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0L;
}
