using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.FastLeg;

/// <summary>
/// Router kiểm lại sàn C ngay trước khi gửi lệnh Open (Phase 5): engine đã xét verdict ở tick phát signal, nhưng giữa
/// lúc đó và lúc dispatch (chờ mutex, debounce) giá C có thể vừa chạy. Chỉ chặn Open, không bao giờ áp cho Close.
/// Mã cố ý KHÔNG theo dạng LATEST_*_INVALID để phân biệt với các guard giá A/B.
/// </summary>
public static class FastLegOpenPolicy
{
    public const string TooFastCode = "FAST_LEG_TOO_FAST";
    public const string StaleCode = "FAST_LEG_STALE";
    public const string DownCode = "FAST_LEG_DOWN";

    /// <param name="verdict">null = sàn C tắt ⇒ luôn cho phép (hành vi như chưa có sàn C).</param>
    public static (bool Allowed, string Code) Check(FastLegVerdict? verdict)
    {
        if (verdict is null || !verdict.BlocksOpen)
        {
            return (true, "ALLOWED");
        }

        return verdict.Kind switch
        {
            FastLegVerdictKind.TooFast => (false, TooFastCode),
            FastLegVerdictKind.Stale => (false, StaleCode),
            _ => (false, DownCode)
        };
    }
}
