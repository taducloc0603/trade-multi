using System.Globalization;
using QuickFix.Fields;

namespace TradeDesktop.Infrastructure.CTrader;

// Dựng message bằng kiểu typed của QuickFIX/n (không ghép chuỗi tay): thư viện lo 8/9/35/10 và thứ tự header.
// cTrader drop IM LẶNG message sai → mọi message ở đây có test validate qua FIX44-CSERVER.xml (R11).
// Mẫu theo spotware/quickfixnsamples.net ConsoleSample/Program.cs và AspNetCoreSample/Services/FixClient.cs.
public static class CTraderMessageFactory
{
    // SDK dùng MDReqID cố định; unsubscribe PHẢI dùng lại đúng chuỗi này với 263=2.
    public const string MarketDataRequestId = "MARKETDATAID";

    // 264 cTrader NGƯỢC FIX chuẩn: 1 = spot (top-of-book), 0 = full depth. Phase 0 câu 2 đo trên live.
    public const int SpotMarketDepth = 1;

    // 559=0 (SYMBOL) — giá trị duy nhất cServer khai trong dictionary. KÈM `55=<symbolId>` để chỉ xin đúng một
    // symbol: hỏi trắng trả về 316 symbol ≈ 9,4 KB, và trên socket QUOTE (đang stream W) message lớn đó làm bộ đọc
    // của QuickFIX/n mất đồng bộ → `UnsupportedVersion: Incorrect BeginString` → tự logout, lặp vô hạn
    // (sự cố live 2026-09-21 19:23). Bỏ symbolId = hỏi trắng như spike Phase 0.
    public static QuickFix.FIX44.SecurityListRequest SecurityListRequest(string securityReqId, int? symbolId = null)
    {
        var message = new QuickFix.FIX44.SecurityListRequest(new SecurityReqID(securityReqId), new SecurityListRequestType(0));
        if (symbolId is { } id)
        {
            message.Set(new Symbol(id.ToString(CultureInfo.InvariantCulture)));
        }

        return message;
    }

    public static QuickFix.FIX44.MarketDataRequest MarketDataRequest(int symbolId, bool subscribe)
    {
        var message = new QuickFix.FIX44.MarketDataRequest(
            new MDReqID(MarketDataRequestId),
            new SubscriptionRequestType(subscribe ? '1' : '2'),
            new MarketDepth(SpotMarketDepth));

        message.AddGroup(new QuickFix.FIX44.MarketDataRequest.NoMDEntryTypesGroup { MDEntryType = new MDEntryType('0') });
        message.AddGroup(new QuickFix.FIX44.MarketDataRequest.NoMDEntryTypesGroup { MDEntryType = new MDEntryType('1') });
        message.AddGroup(new QuickFix.FIX44.MarketDataRequest.NoRelatedSymGroup
        {
            Symbol = new Symbol(symbolId.ToString(CultureInfo.InvariantCulture))
        });

        return message;
    }

    // Market order. positionId null → tạo position mới (không 721). positionId có → tác động position đó
    // (đóng = side ngược + đủ volume; R1 chứng minh ở Phase 7 Bước A). OrderQty tính bằng UNIT, không phải lot.
    public static QuickFix.FIX44.NewOrderSingle MarketOrder(
        string clOrdId,
        int symbolId,
        bool isBuy,
        decimal quantityUnits,
        long? positionId,
        DateTime transactTimeUtc)
    {
        var message = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID(clOrdId),
            new Symbol(symbolId.ToString(CultureInfo.InvariantCulture)),
            new Side(isBuy ? Side.BUY : Side.SELL),
            new TransactTime(DateTime.SpecifyKind(transactTimeUtc, DateTimeKind.Utc)),
            new OrdType(OrdType.MARKET));

        message.Set(new OrderQty(quantityUnits));
        message.Set(new TimeInForce(TimeInForce.IMMEDIATE_OR_CANCEL));

        if (positionId.HasValue)
        {
            message.SetField(new StringField(721, positionId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        return message;
    }

    // Hỏi TẤT CẢ position: có 710, không có 721.
    public static QuickFix.FIX44.RequestForPositions RequestForPositions(string posReqId)
        => new() { PosReqID = new PosReqID(posReqId) };
}
