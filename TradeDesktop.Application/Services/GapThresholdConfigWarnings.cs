namespace TradeDesktop.Application.Services;

/// <summary>
/// Cảnh báo thuần đọc cho 2 cặp ngưỡng gap thường (Open và Normal Close) và cho hold-time
/// của nhánh TIME. Chỉ sinh chuỗi cảnh báo, KHÔNG chặn load và KHÔNG đổi giá trị config.
///
/// Quy ước ngưỡng có dấu: nhánh GapBuy dùng "gap >= threshold", nhánh GapSell dùng
/// "gap &lt;= -threshold". Vì vậy gate mẫu cuối chỉ có tác dụng khi confirm &lt; final.
/// </summary>
public static class GapThresholdConfigWarnings
{
    /// <param name="holdConfirmMs">
    /// <c>open_hold_confirm_ms</c>. Truyền giá trị âm (mặc định) để bỏ qua nhóm cảnh báo hold-time.
    /// </param>
    /// <param name="closeHoldConfirmMs">
    /// <c>close_hold_confirm_ms</c>. Truyền giá trị âm (mặc định) để bỏ qua nhóm cảnh báo hold-time.
    /// </param>
    public static IReadOnlyList<string> Evaluate(
        int confirmGapPts,
        int openPts,
        int closeConfirmGapPts,
        int closePts,
        int holdConfirmMs = -1,
        int closeHoldConfirmMs = -1)
    {
        var warnings = new List<string>(capacity: 6);
        AddPairWarnings(warnings, "OPEN", "confirm_gap_pts", confirmGapPts, "open_pts", openPts);
        AddPairWarnings(
            warnings,
            "NORMAL CLOSE",
            "close_confirm_gap_pts",
            closeConfirmGapPts,
            "close_pts",
            closePts);
        AddHoldConfirmWarnings(warnings, holdConfirmMs, closeHoldConfirmMs);
        return warnings;
    }

    /// <summary>
    /// Nhánh TIME chốt chu kỳ khi đạt CẢ <c>min_stable_samples</c> lẫn <c>*_hold_confirm_ms</c>.
    /// Đặt hold = 0 làm điều kiện thời gian biến mất hoàn toàn — hợp lệ nhưng gần như chắc chắn
    /// là do 4 cột này bị bỏ trống từ thời nhánh TICK, nên phải cảnh báo rõ.
    /// </summary>
    private static void AddHoldConfirmWarnings(
        List<string> warnings,
        int holdConfirmMs,
        int closeHoldConfirmMs)
    {
        if (holdConfirmMs == 0)
        {
            warnings.Add(
                "[OPEN] open_hold_confirm_ms=0 nên điều kiện thời gian bị tắt: Open Cycle chốt ngay " +
                "khi đủ open_gap_min_stable_samples mẫu, vào lệnh dày hơn hẳn nhánh TICK. " +
                "Nếu đây không phải chủ ý, hãy đặt open_hold_confirm_ms > 0 trước khi chạy tiền thật.");
        }

        if (closeHoldConfirmMs == 0)
        {
            warnings.Add(
                "[CLOSE] close_hold_confirm_ms=0 nên điều kiện thời gian bị tắt cho Normal Close, " +
                "SOS Close và TP. Riêng TP sẽ trigger NGAY tick đầu tiên đạt close_tp_profit (không " +
                "còn cửa sổ xác nhận). Nếu đây không phải chủ ý, hãy đặt close_hold_confirm_ms > 0 " +
                "trước khi chạy tiền thật.");
        }
    }

    private static void AddPairWarnings(
        List<string> warnings,
        string group,
        string confirmName,
        int confirmValue,
        string finalName,
        int finalValue)
    {
        // Đúng một giá trị bằng 0 còn giá trị kia âm: cặp này tương đương (0, 0).
        if (confirmValue == 0 && finalValue < 0)
        {
            warnings.Add(
                $"[{group}] {confirmName}=0 nên {finalName}={finalValue} không có tác dụng: " +
                $"mọi mẫu đã phải không nghịch dấu nên gate mẫu cuối luôn đạt. " +
                $"Cặp này tương đương (0, 0); muốn nới về phía trong phải đặt cả hai giá trị âm.");
            return;
        }

        if (finalValue == 0 && confirmValue < 0)
        {
            warnings.Add(
                $"[{group}] {finalName}=0 làm {confirmName}={confirmValue} gần như vô nghĩa: " +
                $"Cycle bắt buộc cùng dấu nên chỉ Cycle không nghịch dấu mới qua được gate mẫu cuối. " +
                $"Cặp này gần tương đương (0, 0); muốn nới về phía trong phải đặt cả hai giá trị âm.");
            return;
        }

        if (confirmValue >= finalValue)
        {
            warnings.Add(
                $"[{group}] {confirmName}={confirmValue} >= {finalName}={finalValue} nên gate mẫu cuối " +
                $"vô hiệu: mẫu nào qua được confirm cũng tự động qua gate cuối, chỉ còn " +
                $"min_stable_samples + hold_confirm_ms quyết định.");
        }
    }
}
