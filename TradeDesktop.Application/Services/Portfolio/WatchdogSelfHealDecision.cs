namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Hành động watchdog quyết định sau khi đánh giá invariant.
/// </summary>
public enum WatchdogAction
{
    /// <summary>Không có vi phạm — auto-open tiếp tục bình thường.</summary>
    NoViolation,

    /// <summary>Có vi phạm thật (over-trading / nguyên nhân khác) — pause auto-open.</summary>
    Pause,

    /// <summary>
    /// Coordinator under-count drift (số tool-pair mở thật > số slot coordinator giữ, nhưng
    /// trạng thái vật lý vẫn hợp lệ ≤ cap) — resync slot từ MMF để watchdog tự nhả.
    /// </summary>
    SelfHeal,
}

/// <summary>
/// Hàm thuần quyết định hành động self-heal/pause cho invariant watchdog (Rule E integrity recovery).
/// Tách khỏi <c>DashboardViewModel</c> để unit-test độc lập (Tests không reference App layer).
/// </summary>
public static class WatchdogSelfHealDecision
{
    /// <summary>
    /// Quyết định watchdog nên làm gì.
    /// </summary>
    /// <param name="quotaTotalViolation">coordinatorActiveCount &gt; maxTotal (over-count thật).</param>
    /// <param name="quotaSideViolation">LiveBuy/LiveSell &gt; cap (over-count thật).</param>
    /// <param name="autoPairCapViolation">liveAutoPairCount &gt; maxTotal (over-count thật).</param>
    /// <param name="toolOverTrackingViolation">toolRowsA/B &gt; coordinatorActiveCount (under-count drift).</param>
    /// <param name="coordinatorActiveCount">Số slot coordinator đang giữ (Live + Pending).</param>
    /// <param name="rebuiltCount">Số pair hợp lệ rebuild được từ MMF tool-pair.</param>
    /// <param name="rebuiltBuyCount">Số pair side Buy trong rebuild.</param>
    /// <param name="rebuiltSellCount">Số pair side Sell trong rebuild.</param>
    /// <param name="maxTotal">Cap tổng (CurrentMaxTotalOpens).</param>
    /// <param name="maxBuy">Cap Buy (CurrentMaxBuyOpens).</param>
    /// <param name="maxSell">Cap Sell (CurrentMaxSellOpens).</param>
    /// <param name="hasOpenOrCloseInFlight">True nếu đang có open/close dang dở — KHÔNG được đè resync.</param>
    public static WatchdogAction Decide(
        bool quotaTotalViolation,
        bool quotaSideViolation,
        bool autoPairCapViolation,
        bool toolOverTrackingViolation,
        int coordinatorActiveCount,
        int rebuiltCount,
        int rebuiltBuyCount,
        int rebuiltSellCount,
        int maxTotal,
        int maxBuy,
        int maxSell,
        bool hasOpenOrCloseInFlight)
    {
        var hasViolation =
            quotaTotalViolation
            || quotaSideViolation
            || autoPairCapViolation
            || toolOverTrackingViolation;

        if (!hasViolation)
        {
            return WatchdogAction.NoViolation;
        }

        // Bất kỳ vi phạm over-count nào (quota total/side, auto-pair cap) đều là vấn đề thật,
        // KHÔNG được self-heal — giữ pause.
        if (quotaTotalViolation || quotaSideViolation || autoPairCapViolation)
        {
            return WatchdogAction.Pause;
        }

        // Đến đây chỉ còn toolOverTracking (under-count drift). Phòng thủ.
        if (!toolOverTrackingViolation)
        {
            return WatchdogAction.Pause;
        }

        // Xác nhận đúng là under-count: rebuild ra nhiều pair hơn số slot coordinator đang giữ.
        if (rebuiltCount <= coordinatorActiveCount)
        {
            return WatchdogAction.Pause;
        }

        // Trạng thái vật lý sau rebuild phải hợp lệ (≤ cap) — nếu vượt cap thì đây là over-trading thật,
        // không được hợp thức hoá bằng resync.
        if (rebuiltCount > maxTotal || rebuiltBuyCount > maxBuy || rebuiltSellCount > maxSell)
        {
            return WatchdogAction.Pause;
        }

        // Không đè lên close/open đang chạy.
        if (hasOpenOrCloseInFlight)
        {
            return WatchdogAction.Pause;
        }

        return WatchdogAction.SelfHeal;
    }
}
