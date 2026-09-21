using System.Text.RegularExpressions;

namespace TradeDesktop.Application.Services.CTrader;

// Mọi log raw FIX của app PHẢI đi qua đây. Phase 0 (2026-09-17): sample Spotware in Logout kèm 554 nguyên văn
// ra console — mật khẩu lộ qua message admin KHÁC Logon. Che cho mọi message, mọi dạng hiển thị:
//   raw SOH  "…<SOH>554=secret<SOH>…" · raw '|' "…|554=secret|…" · GetMessageText "{554: "secret"}"
public static partial class CTraderFixLogMasker
{
    public const string Mask = "***";

    [GeneratedRegex("(^|[\\u0001|])554=[^\\u0001|\\r\\n]*")]
    private static partial Regex RawTagRegex();

    [GeneratedRegex("(\\{\\s*554\\s*:\\s*\")[^\"]*(\")")]
    private static partial Regex DisplayTagRegex();

    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("554", StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }

        var masked = RawTagRegex().Replace(text, m => m.Groups[1].Value + "554=" + Mask);
        return DisplayTagRegex().Replace(masked, m => m.Groups[1].Value + Mask + m.Groups[2].Value);
    }
}
