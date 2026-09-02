namespace TradeDesktop.Application.Services;

/// <summary>
/// Cảnh báo thuần đọc cho 2 cặp ngưỡng gap thường (Open và Normal Close). Chỉ sinh chuỗi
/// cảnh báo, KHÔNG chặn load và KHÔNG đổi giá trị config.
///
/// Quy ước ngưỡng có dấu: nhánh GapBuy dùng "gap >= threshold", nhánh GapSell dùng
/// "gap &lt;= -threshold". Vì vậy gate mẫu cuối chỉ có tác dụng khi confirm &lt; final.
/// </summary>
public static class GapThresholdConfigWarnings
{
    public static IReadOnlyList<string> Evaluate(
        int confirmGapPts,
        int openPts,
        int closeConfirmGapPts,
        int closePts)
    {
        var warnings = new List<string>(capacity: 4);
        AddPairWarnings(warnings, "OPEN", "confirm_gap_pts", confirmGapPts, "open_pts", openPts);
        AddPairWarnings(
            warnings,
            "NORMAL CLOSE",
            "close_confirm_gap_pts",
            closeConfirmGapPts,
            "close_pts",
            closePts);
        return warnings;
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
                $"signal_cycle_size quyết định.");
        }
    }
}
