using System.Globalization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.FastLeg;

/// <summary>
/// Trạng thái hoãn của MỘT cycle khi sàn C chặn (Phase 4, quyết định D4/D8). Engine giữ cycle nguyên vẹn và hỏi lại
/// sàn C ở tick sau; trạng thái này chỉ đếm thời gian/tick đã hoãn để áp trần MaxDeferMs và ghi log một lần mỗi cycle.
/// KHÔNG tạo signal, KHÔNG tạo timer (CLAUDE.md Rule E).
/// </summary>
public sealed class FastLegDeferState
{
    public string? CycleId { get; private set; }

    public DateTime FirstDeferUtc { get; private set; }

    public int Ticks { get; private set; }

    public bool IsActive => CycleId is not null;

    /// <summary>Ghi nhận một tick bị chặn. Trả true nếu đây là tick đầu tiên hoãn cycle này (để log DEFER một lần).</summary>
    public bool Defer(string cycleId, DateTime nowUtc)
    {
        if (CycleId == cycleId)
        {
            Ticks++;
            return false;
        }

        CycleId = cycleId;
        FirstDeferUtc = nowUtc;
        Ticks = 1;
        return true;
    }

    public long ElapsedMs(DateTime nowUtc) => Math.Max(0L, (long)(nowUtc - FirstDeferUtc).TotalMilliseconds);

    public bool IsExpired(int maxDeferMs, DateTime nowUtc) => IsActive && ElapsedMs(nowUtc) >= maxDeferMs;

    public void Clear()
    {
        CycleId = null;
        Ticks = 0;
        FirstDeferUtc = default;
    }

    // ---------- Log: mỗi cycle một dòng DEFER, rồi đúng một dòng RELEASE hoặc ABANDON ----------

    public static void LogDefer(ISlotLogger? logger, string action, string side, int? slotId, string cycleId, FastLegVerdict verdict)
    {
        if (logger is null)
        {
            return;
        }

        logger.LogVerbose(string.Create(
            CultureInfo.InvariantCulture,
            $"[FAST_LEG][DEFER] action={action} side={side} slot={Slot(slotId)} cycle={cycleId} verdict={verdict.Kind} move_pts={verdict.MovePts:0.##} age_ms={Age(verdict)} max_defer_ms={verdict.MaxDeferMs}"));
    }

    public void LogRelease(ISlotLogger? logger, string action, string side, int? slotId, FastLegVerdict? verdict, DateTime nowUtc)
    {
        if (logger is null || !IsActive)
        {
            return;
        }

        logger.LogVerbose(string.Create(
            CultureInfo.InvariantCulture,
            $"[FAST_LEG][RELEASE] action={action} side={side} slot={Slot(slotId)} cycle={CycleId} verdict={verdict?.Kind.ToString() ?? "-"} move_pts={verdict?.MovePts ?? 0m:0.##} deferred_ticks={Ticks} deferred_ms={ElapsedMs(nowUtc)}"));
    }

    /// <summary>Ghi ABANDON (nếu đang hoãn) rồi xoá trạng thái. Gọi ở mọi điểm reset cycle.</summary>
    public void Abandon(ISlotLogger? logger, string action, string side, int? slotId, string reason, DateTime? nowUtc = null)
    {
        if (!IsActive)
        {
            return;
        }

        if (logger is not null)
        {
            var elapsed = nowUtc.HasValue ? ElapsedMs(nowUtc.Value).ToString(CultureInfo.InvariantCulture) : "-";
            logger.LogVerbose(string.Create(
                CultureInfo.InvariantCulture,
                $"[FAST_LEG][ABANDON] action={action} side={side} slot={Slot(slotId)} cycle={CycleId} reason={reason} deferred_ticks={Ticks} deferred_ms={elapsed}"));
        }

        Clear();
    }

    private static string Slot(int? slotId) => slotId?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Age(FastLegVerdict verdict) => verdict.AgeMs?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
