using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

// Hai cột của bảng History "Profit Realtime (A + B)". Chỉ dùng để HIỂN THỊ — không nuôi TP / Rule D.
public static class HistoryProfitCalculator
{
    // Cột pt: dịch chuyển giá × point, bỏ lot (cùng quy ước CalculateTradeProfit của tab Trade).
    public static double CalculatePoints(HistorySharedRecord record, int point)
    {
        var pointValue = Math.Max(1, point);
        return record.TradeType == 0
            ? (record.ClosePrice - record.OpenPrice) * pointValue
            : (record.OpenPrice - record.ClosePrice) * pointValue;
    }

    // Tổng "$" theo cặp: tiền NET = Profit + Commission (cột "Profit ($)" từng chân vẫn hiện Profit thô). MT4/MT5: số broker (USD). cTrader: tính lại
    // move × units với Commission = 0 (R10, CTraderHistoryProjector). Swap không có trong MMF.
    public static double CalculateMoney(HistorySharedRecord record)
        => record.Profit + record.Commission;
}
