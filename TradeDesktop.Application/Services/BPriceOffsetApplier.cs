using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

// Cộng offset (point, có thể âm) từ DB vào giá Bid/Ask sàn B: B' = (B × point + offset) / point.
// Áp ở reader TRƯỚC khi phát snapshot nên mọi bước sau (gap, signal, router, profit chân B, slippage, UI, log)
// đều thấy giá đã dịch. Spread giữ nguyên giá trị thật của nguồn để guard spread vẫn đo spread thị trường.
public static class BPriceOffsetApplier
{
    public static ExchangeMetrics Apply(ExchangeMetrics sanB, int bidOffsetPts, int askOffsetPts, int point)
    {
        if (bidOffsetPts == 0 && askOffsetPts == 0)
        {
            return sanB;
        }

        // Cùng quy ước với GapCalculator: point <= 0 coi như 1.
        var pointMultiplier = point > 0 ? point : 1;

        return sanB with
        {
            Bid = sanB.Bid.HasValue ? sanB.Bid.Value + bidOffsetPts / (decimal)pointMultiplier : null,
            Ask = sanB.Ask.HasValue ? sanB.Ask.Value + askOffsetPts / (decimal)pointMultiplier : null,
        };
    }
}
