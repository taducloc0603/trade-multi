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
        int PointMultiplier);

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
    /// Kiểm tra tuần tự 4 điều kiện. Trả về CanTrade=false + lý do nếu fail.
    /// </summary>
    public static GuardResult Check(
        GapSignalTriggerResult trigger,
        DashboardMetrics? metrics,
        GuardConfig config,
        Queue<PriceHistoryEntry> priceHistory,
        int holdConfirmMs)
    {
        // 1. Latency
        var latencyResult = CheckLatency(metrics, config.ConfirmLatencyMs);
        if (!latencyResult.CanTrade) return latencyResult;

        // 2. Max gap
        var gapResult = CheckMaxGap(trigger, config.MaxGap);
        if (!gapResult.CanTrade) return gapResult;

        // 3. Spread (đơn vị pts = Spread_raw × Point)
        var spreadResult = CheckSpread(metrics, config.MaxSpread, config.PointMultiplier);
        if (!spreadResult.CanTrade) return spreadResult;

        // 4. Price freeze
        var freezeResult = CheckPriceFreeze(trigger.TriggeredAtUtc, priceHistory, holdConfirmMs);
        if (!freezeResult.CanTrade) return freezeResult;

        return new GuardResult(true, null);
    }

    private static GuardResult CheckLatency(DashboardMetrics? metrics, int confirmLatencyMs)
    {
        if (confirmLatencyMs <= 0 || metrics is null)
            return new GuardResult(true, null);

        var latA = metrics.ExchangeA.LatencyMs;
        var latB = metrics.ExchangeB.LatencyMs;

        if (latA.HasValue && latA.Value > confirmLatencyMs)
            return new GuardResult(false,
                $"Latency sàn A={latA.Value.ToString("0", CultureInfo.InvariantCulture)} ms > confirm_latency={confirmLatencyMs} ms");

        if (latB.HasValue && latB.Value > confirmLatencyMs)
            return new GuardResult(false,
                $"Latency sàn B={latB.Value.ToString("0", CultureInfo.InvariantCulture)} ms > confirm_latency={confirmLatencyMs} ms");

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
        int holdConfirmMs)
    {
        if (holdConfirmMs <= 0) return new GuardResult(true, null);

        var windowStart = triggeredAtUtc.AddMilliseconds(-holdConfirmMs);
        var window = priceHistory
            .Where(e => e.TimestampUtc >= windowStart && e.TimestampUtc <= triggeredAtUtc)
            .ToList();

        // Cần ít nhất 2 ticks để phát hiện freeze
        if (window.Count < 2) return new GuardResult(true, null);

        var first = window[0];

        if (first.BidA is decimal bidA0 && window.All(e => e.BidA == first.BidA))
            return new GuardResult(false,
                $"Giá Bid sàn A đóng băng suốt {holdConfirmMs} ms ({bidA0.ToString("0.#####", CultureInfo.InvariantCulture)})");

        if (first.AskA is decimal askA0 && window.All(e => e.AskA == first.AskA))
            return new GuardResult(false,
                $"Giá Ask sàn A đóng băng suốt {holdConfirmMs} ms ({askA0.ToString("0.#####", CultureInfo.InvariantCulture)})");

        if (first.BidB is decimal bidB0 && window.All(e => e.BidB == first.BidB))
            return new GuardResult(false,
                $"Giá Bid sàn B đóng băng suốt {holdConfirmMs} ms ({bidB0.ToString("0.#####", CultureInfo.InvariantCulture)})");

        if (first.AskB is decimal askB0 && window.All(e => e.AskB == first.AskB))
            return new GuardResult(false,
                $"Giá Ask sàn B đóng băng suốt {holdConfirmMs} ms ({askB0.ToString("0.#####", CultureInfo.InvariantCulture)})");

        return new GuardResult(true, null);
    }
}