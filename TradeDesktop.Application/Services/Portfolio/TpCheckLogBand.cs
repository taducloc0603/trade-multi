namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Xác định "band" trạng thái close của một slot theo profit so với ngưỡng confirm/TP.
/// Dùng để dedup log <c>[SLOT][TP_CHECK]</c>: chỉ log lại ngay khi band đổi (profit vượt ngưỡng),
/// ngoài ra throttle theo interval. Hàm thuần để unit-test độc lập.
/// </summary>
public static class TpCheckLogBand
{
    public const string Incomplete = "incomplete";
    public const string Below = "below";
    public const string Confirm = "confirm";
    public const string Tp = "tp";

    /// <summary>
    /// Trả band hiện tại của slot.
    /// </summary>
    /// <param name="profit">Profit snapshot của slot; null nếu chưa đủ snapshot.</param>
    /// <param name="confirmThreshold">Ngưỡng confirm (CloseConfirmTpProfit) — lấy Math.Abs như log hiện tại.</param>
    /// <param name="tpThreshold">Ngưỡng TP (CloseTpProfit) — lấy Math.Abs như log hiện tại.</param>
    public static string Resolve(double? profit, double confirmThreshold, double tpThreshold)
    {
        if (!profit.HasValue)
        {
            return Incomplete;
        }

        var confirm = System.Math.Abs(confirmThreshold);
        var tp = System.Math.Abs(tpThreshold);
        var value = profit.Value;

        if (value >= tp)
        {
            return Tp;
        }

        return value >= confirm ? Confirm : Below;
    }
}
