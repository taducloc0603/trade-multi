using System.Globalization;
using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Application.Services;

/// <summary>
/// Stateless guard: kiểm tra 4 điều kiện lọc sau khi signal engine đã fire trigger.
/// Thứ tự: Latency → MaxGap → Spread → PriceFreeze.
/// Gặp fail đầu tiên thì trả về ngay, không check tiếp.
/// Giá trị 0 ở bất kỳ điều kiện nào = disabled (bỏ qua).
/// </summary>
public static class SignalEntryGuard
{
    private const int PriceHistoryCapacityMs = 60_000;

    public sealed record GuardConfig(
        int ConfirmLatencyMs,
        int MaxGap,
        int MaxSpread,
        int PointMultiplier,
        // Ngưỡng latency RIÊNG cho chân B (task R8-B, cột `ctrader_confirm_latency_b`). `-1` = không cấu hình → chân B dùng chung
        // `ConfirmLatencyMs` y như trước. Lý do tách: MT đo EA→app (dưới 1 ms) còn cTrader đo "bao lâu rồi
        // chưa có tick" (đo trên live: p50 317 ms, p95 2 043 ms) — dùng chung một ngưỡng thì hoặc chặn sạch
        // chân cTrader, hoặc phải nới ngưỡng và làm mất tác dụng guard ở chân MT.
        int ConfirmLatencyMsB = -1);

    public sealed record GuardResult(bool CanTrade, string? SkipReason);

    public readonly record struct PriceHistoryEntry(
        DateTime TimestampUtc,
        decimal? BidA,
        decimal? AskA,
        decimal? BidB,
        decimal? AskB);

    /// <summary>
    /// Gọi mỗi snapshot để duy trì sliding window giá. Prune entries cũ hơn 60s.
    /// </summary>
    public static void TrackPriceHistory(
        Queue<PriceHistoryEntry> history,
        DashboardMetrics metrics)
    {
        history.Enqueue(new PriceHistoryEntry(
            metrics.TimestampUtc,
            metrics.ExchangeA.Bid,
            metrics.ExchangeA.Ask,
            metrics.ExchangeB.Bid,
            metrics.ExchangeB.Ask));

        while (history.Count > 0 &&
               (metrics.TimestampUtc - history.Peek().TimestampUtc).TotalMilliseconds > PriceHistoryCapacityMs)
        {
            history.Dequeue();
        }
    }

    /// <summary>
    /// Kiểm tra tuần tự 5 điều kiện. Trả về CanTrade=false + lý do nếu fail.
    /// </summary>
    public static GuardResult Check(
        GapSignalTriggerResult trigger,
        DashboardMetrics? metrics,
        GuardConfig config,
        Queue<PriceHistoryEntry> priceHistory,
        int priceFreezeMs)
    {
        // 1. Latency
        var latencyResult = CheckLatency(metrics, config.ConfirmLatencyMs, config.ConfirmLatencyMsB);
        if (!latencyResult.CanTrade) return latencyResult;

        // 2. Max gap
        var gapResult = CheckMaxGap(trigger, config.MaxGap);
        if (!gapResult.CanTrade) return gapResult;

        // 3. Spread (đơn vị pts = Spread_raw × Point)
        var spreadResult = CheckSpread(metrics, config.MaxSpread, config.PointMultiplier);
        if (!spreadResult.CanTrade) return spreadResult;

        // 4. Price freeze
        var freezeResult = CheckPriceFreeze(trigger.TriggeredAtUtc, priceHistory, priceFreezeMs);
        if (!freezeResult.CanTrade) return freezeResult;

        return new GuardResult(true, null);
    }

    // Chân A luôn dùng confirm_latency. Chân B dùng ngưỡng riêng khi caller truyền (>= 0), không thì dùng chung.
    // Ai quyết định 'có truyền hay không' là RuntimeConfigState (chỉ khi platform_b = ctrader) — guard thuần theo tham số.
    // Mỗi ngưỡng tự tắt riêng khi <= 0 — đặt ngưỡng B = 0 chỉ tắt guard ở chân B, không đụng chân A.
    // TradeExecutionRouter phải giữ ĐÚNG luật này (re-check sau mutex) — sửa một nơi mà quên nơi kia sẽ lệch.
    public static int ResolveLatencyLimitB(int confirmLatencyMs, int confirmLatencyMsB)
        => confirmLatencyMsB >= 0 ? confirmLatencyMsB : confirmLatencyMs;

    private static GuardResult CheckLatency(DashboardMetrics? metrics, int confirmLatencyMs, int confirmLatencyMsB)
    {
        if (metrics is null)
            return new GuardResult(true, null);

        var limitB = ResolveLatencyLimitB(confirmLatencyMs, confirmLatencyMsB);
        var latA = metrics.ExchangeA.LatencyMs;
        var latB = metrics.ExchangeB.LatencyMs;

        if (confirmLatencyMs > 0 && latA.HasValue && latA.Value > confirmLatencyMs)
            return new GuardResult(false,
                $"Latency sàn A={latA.Value.ToString("0", CultureInfo.InvariantCulture)} ms > confirm_latency={confirmLatencyMs} ms");

        if (limitB > 0 && latB.HasValue && latB.Value > limitB)
            return new GuardResult(false,
                $"Latency sàn B={latB.Value.ToString("0", CultureInfo.InvariantCulture)} ms > confirm_latency{(confirmLatencyMsB >= 0 ? "_b" : string.Empty)}={limitB} ms");

        return new GuardResult(true, null);
    }

    private static GuardResult CheckMaxGap(GapSignalTriggerResult trigger, int maxGap)
    {
        if (maxGap <= 0) return new GuardResult(true, null);

        var lastGap = trigger.TriggerType is
            GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.CloseByGapBuy
            ? trigger.LastBuyGap
            : trigger.LastSellGap;

        if (!lastGap.HasValue)
        {
            return new GuardResult(true, null);
        }

        if (lastGap.Value >= 0)
        {
            if (lastGap.Value > maxGap)
            {
                return new GuardResult(false,
                    $"Gap={lastGap.Value.ToString(CultureInfo.InvariantCulture)} pts > max_gap={maxGap} pts");
            }
        }
        else
        {
            if (lastGap.Value < -maxGap)
            {
                return new GuardResult(false,
                    $"Gap={lastGap.Value.ToString(CultureInfo.InvariantCulture)} pts < -max_gap={(-maxGap).ToString(CultureInfo.InvariantCulture)} pts");
            }
        }

        return new GuardResult(true, null);
    }

    private static GuardResult CheckSpread(DashboardMetrics? metrics, int maxSpreadPts, int pointMultiplier)
    {
        if (maxSpreadPts <= 0 || metrics is null)
            return new GuardResult(true, null);

        var point = Math.Max(1, pointMultiplier);
        var spreadA = metrics.ExchangeA.Spread;
        var spreadB = metrics.ExchangeB.Spread;

        if (spreadA.HasValue)
        {
            var spreadAPts = (int)(spreadA.Value * point);
            if (spreadAPts > maxSpreadPts)
                return new GuardResult(false,
                    $"Spread sàn A={spreadAPts.ToString(CultureInfo.InvariantCulture)} pts > max_spread={maxSpreadPts} pts");
        }

        if (spreadB.HasValue)
        {
            var spreadBPts = (int)(spreadB.Value * point);
            if (spreadBPts > maxSpreadPts)
                return new GuardResult(false,
                    $"Spread sàn B={spreadBPts.ToString(CultureInfo.InvariantCulture)} pts > max_spread={maxSpreadPts} pts");
        }

        return new GuardResult(true, null);
    }

    private static GuardResult CheckPriceFreeze(
        DateTime triggeredAtUtc,
        Queue<PriceHistoryEntry> priceHistory,
        int priceFreezeMs)
    {
        if (priceFreezeMs <= 0) return new GuardResult(true, null);

        var windowStart = triggeredAtUtc.AddMilliseconds(-priceFreezeMs);
        var window = priceHistory
            .Where(e => e.TimestampUtc >= windowStart && e.TimestampUtc <= triggeredAtUtc)
            .ToList();

        // Cần ít nhất 2 ticks để phát hiện freeze
        if (window.Count < 2) return new GuardResult(true, null);

        var first = window[0];

        if (first.BidA is decimal bidA0 && window.All(e => e.BidA == first.BidA))
            return new GuardResult(false,
                $"Giá Bid sàn A đóng băng suốt {priceFreezeMs} ms ({bidA0.ToString("0.#####", CultureInfo.InvariantCulture)})");

        if (first.AskA is decimal askA0 && window.All(e => e.AskA == first.AskA))
            return new GuardResult(false,
                $"Giá Ask sàn A đóng băng suốt {priceFreezeMs} ms ({askA0.ToString("0.#####", CultureInfo.InvariantCulture)})");

        if (first.BidB is decimal bidB0 && window.All(e => e.BidB == first.BidB))
            return new GuardResult(false,
                $"Giá Bid sàn B đóng băng suốt {priceFreezeMs} ms ({bidB0.ToString("0.#####", CultureInfo.InvariantCulture)})");

        if (first.AskB is decimal askB0 && window.All(e => e.AskB == first.AskB))
            return new GuardResult(false,
                $"Giá Ask sàn B đóng băng suốt {priceFreezeMs} ms ({askB0.ToString("0.#####", CultureInfo.InvariantCulture)})");

        return new GuardResult(true, null);
    }

}
