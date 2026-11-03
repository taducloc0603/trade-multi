using System.Globalization;

namespace TradeDesktop.Application.Services.FastLeg;

/// <summary>Cột `fast_leg` cần ghi gì khi người dùng bấm Lưu ở cửa sổ Config.</summary>
public enum FastLegWriteKind
{
    // Không ghi: người dùng không đụng mục Sàn C (Lưu chỉ vì sửa A/B), hoặc máy chưa từng có sàn C mà vẫn để tắt.
    None,
    // Ghi cấu hình đang bật (phải qua FastLegConfig.TryParse trước).
    Enabled,
    // Ghi enabled=false (giữ thông số) để tắt sàn C.
    Disabled
}

/// <summary>Logic thuần của mục "Sàn C" trong cửa sổ Config (tách khỏi ViewModel để test được).</summary>
public static class FastLegFormLogic
{
    /// <summary>
    /// Số thập phân CHỈ dùng dấu chấm. Cố ý không dùng NumberStyles.Number: nó cho phép dấu phẩy hàng nghìn mà không
    /// kiểm nhóm, nên "5,5" (người Việt gõ 5.5) sẽ thành 55 — ngưỡng gấp 10 lần và bộ lọc gần như tắt im lặng.
    /// </summary>
    public static bool TryParseDecimal(string? text, out decimal value) =>
        decimal.TryParse((text ?? string.Empty).Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    public static bool TryParseInt(string? text, out int value) =>
        int.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <param name="formEnabled">Ô tick "Bật sàn C".</param>
    /// <param name="formDirty">Người dùng đã sửa ít nhất một ô của mục Sàn C kể từ lần nạp/lưu gần nhất.</param>
    /// <param name="hasStoredJson">Cột `fast_leg` trên DB đang có giá trị (kể cả enabled=false hoặc hỏng).</param>
    /// <param name="runtimeActive">Sàn C đang chạy trong app.</param>
    public static FastLegWriteKind Decide(bool formEnabled, bool formDirty, bool hasStoredJson, bool runtimeActive)
    {
        if (!formDirty)
        {
            // Không đụng mục Sàn C ⇒ không ghi đè cột: giữ nguyên các khoá form không hiển thị (heartbeatMapName tuỳ chỉnh)
            // và không biến một cấu hình đang hỏng thành mặc định mà người dùng không hề chọn.
            return FastLegWriteKind.None;
        }

        if (formEnabled)
        {
            return FastLegWriteKind.Enabled;
        }

        return hasStoredJson || runtimeActive ? FastLegWriteKind.Disabled : FastLegWriteKind.None;
    }
}
