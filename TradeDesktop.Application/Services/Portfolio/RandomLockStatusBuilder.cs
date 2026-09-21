using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Một dòng của khối "Random Locks" trên panel Trading Signal. Chỉ để hiển thị.
/// </summary>
public sealed record RandomLockStatusRow(
    string Key,
    string Name,
    string RangeText,
    string SelectedText,
    string RemainingText,
    string StatusText,
    bool IsBlocking);

/// <summary>
/// Ảnh chụp các giá trị cần để tính khối "Random Locks". Tách khỏi coordinator để test được.
/// </summary>
public sealed record RandomLockInputs(
    bool IsSameActionLockEnabled,
    int RdStartSameActionLockSeconds,
    int RdEndSameActionLockSeconds,
    bool IsCloseSameActionLockEnabled,
    int CloseRdStartSameActionLockSeconds,
    int CloseRdEndSameActionLockSeconds,
    AutoTradeActionType LastAutoDispatchType,
    TradingPositionSide LastAutoDispatchSide,
    DateTime? LastAutoDispatchAtUtc,
    int LastAutoRandomIntervalSeconds,
    int RdStartPostCloseLockSeconds,
    int RdEndPostCloseLockSeconds,
    int LastSelectedPostCloseLockSeconds,
    DateTime? LastCloseConfirmedAtUtc,
    int OppositeSideLockSeconds,
    DateTime? LastOpenConfirmedAtUtc,
    TradingPositionSide LastOpenConfirmedSide,
    DateTime? GlobalActionLockUntilUtc)
{
    public static RandomLockInputs From(IPortfolioCoordinator coordinator) => new(
        coordinator.IsSameActionLockEnabled,
        coordinator.RdStartSameActionLockSeconds,
        coordinator.RdEndSameActionLockSeconds,
        coordinator.IsCloseSameActionLockEnabled,
        coordinator.CloseRdStartSameActionLockSeconds,
        coordinator.CloseRdEndSameActionLockSeconds,
        coordinator.LastAutoDispatchType,
        coordinator.LastAutoDispatchSide,
        coordinator.LastAutoDispatchAtUtc,
        coordinator.LastAutoRandomIntervalSeconds,
        coordinator.RdStartPostCloseLockSeconds,
        coordinator.RdEndPostCloseLockSeconds,
        coordinator.LastSelectedPostCloseLockSeconds,
        coordinator.LastCloseConfirmedAtUtc,
        coordinator.OppositeSideLockSeconds,
        coordinator.LastOpenConfirmedAtUtc,
        coordinator.LastOpenConfirmedSide,
        coordinator.GlobalActionLockUntilUtc);
}

/// <summary>
/// Dựng khối "Random Locks" từ trạng thái coordinator. Deadline tính giống hệt gate đang chặn,
/// để con số trên màn hình khớp với lý do block trong log. KHÔNG tham gia quyết định Open/Close.
/// </summary>
public static class RandomLockStatusBuilder
{
    public const string KeySameOpen = "SAME_OPEN";
    public const string KeySameClose = "SAME_CLOSE";
    public const string KeyPostClose = "POST_CLOSE";
    public const string KeyOpposite = "OPPOSITE";
    public const string KeyGlobal = "GLOBAL";

    private const string None = "-";

    public static IReadOnlyList<RandomLockStatusRow> Build(RandomLockInputs s, DateTime nowUtc)
    {
        var lastAt = s.LastAutoDispatchAtUtc;
        var lastIsOpen = lastAt.HasValue && s.LastAutoDispatchType == AutoTradeActionType.Open;
        var lastIsClose = lastAt.HasValue && s.LastAutoDispatchType == AutoTradeActionType.Close;

        return
        [
            // Open (rd_*): giá trị random lúc Auto Open chặn Open cùng chiều và Open→Close.
            SameActionRow(
                KeySameOpen, "Same Open",
                s.IsSameActionLockEnabled, s.RdStartSameActionLockSeconds, s.RdEndSameActionLockSeconds,
                lastIsOpen, lastAt, s.LastAutoRandomIntervalSeconds, nowUtc,
                $"CHẶN Open {SideText(s.LastAutoDispatchSide)} + Close"),
            // Close (close_rd_*): giá trị random lúc Auto Close chặn Close→Close.
            SameActionRow(
                KeySameClose, "Same Close",
                s.IsCloseSameActionLockEnabled, s.CloseRdStartSameActionLockSeconds, s.CloseRdEndSameActionLockSeconds,
                lastIsClose, lastAt, s.LastAutoRandomIntervalSeconds, nowUtc,
                "CHẶN Close"),
            PostCloseRow(s, lastIsClose, nowUtc),
            OppositeRow(s, nowUtc),
            GlobalRow(s, nowUtc),
        ];
    }

    public static string FormatLastAutoDispatch(RandomLockInputs s)
    {
        if (s.LastAutoDispatchAtUtc is not { } at || s.LastAutoDispatchType == AutoTradeActionType.None)
        {
            return "Chưa có Auto dispatch";
        }

        var type = s.LastAutoDispatchType == AutoTradeActionType.Open ? "OPEN" : "CLOSE";
        return $"{type} {SideText(s.LastAutoDispatchSide)} lúc {at.ToLocalTime():HH:mm:ss}";
    }

    private static RandomLockStatusRow SameActionRow(
        string key, string name,
        bool enabled, int start, int end,
        bool isLastDispatch, DateTime? lastAt, int selectedSeconds, DateTime nowUtc,
        string blockingText)
    {
        if (!enabled)
        {
            return new RandomLockStatusRow(key, name, "OFF", None, None, "TẮT", false);
        }

        var range = $"{start}..{end}s";
        if (!isLastDispatch || lastAt is null)
        {
            return new RandomLockStatusRow(key, name, range, None, None, "RẢNH", false);
        }

        var remaining = RemainingSeconds(lastAt.Value.AddSeconds(selectedSeconds), nowUtc);
        return new RandomLockStatusRow(
            key, name, range, $"{selectedSeconds}s",
            remaining > 0 ? $"{remaining}s" : None,
            remaining > 0 ? blockingText : "RẢNH",
            remaining > 0);
    }

    private static RandomLockStatusRow PostCloseRow(RandomLockInputs s, bool lastIsClose, DateTime nowUtc)
    {
        var range = $"{s.RdStartPostCloseLockSeconds}..{s.RdEndPostCloseLockSeconds}s";
        var selected = s.LastSelectedPostCloseLockSeconds;

        // Close→Open có hai lớp dùng CÙNG duration: gate tính từ Auto Close dispatch, CanOpenNewSlot
        // tính từ Close confirm. Deadline thực tế là cái muộn hơn, không cộng dồn (Rule C).
        DateTime? deadline = null;
        if (lastIsClose && s.LastAutoDispatchAtUtc is { } dispatchAt)
        {
            deadline = dispatchAt.AddSeconds(selected);
        }
        if (s.LastCloseConfirmedAtUtc is { } confirmAt)
        {
            var fromConfirm = confirmAt.AddSeconds(selected);
            deadline = deadline is { } d && d > fromConfirm ? d : fromConfirm;
        }

        if (deadline is null)
        {
            return new RandomLockStatusRow(KeyPostClose, "Post-close", range, None, None, "RẢNH", false);
        }

        var remaining = RemainingSeconds(deadline.Value, nowUtc);
        return new RandomLockStatusRow(
            KeyPostClose, "Post-close", range, $"{selected}s",
            remaining > 0 ? $"{remaining}s" : None,
            remaining > 0 ? "CHẶN Open" : "RẢNH",
            remaining > 0);
    }

    private static RandomLockStatusRow OppositeRow(RandomLockInputs s, DateTime nowUtc)
    {
        var range = $"{s.OppositeSideLockSeconds}s (cố định)";
        if (s.LastOpenConfirmedAtUtc is not { } openAt || s.LastOpenConfirmedSide == TradingPositionSide.None)
        {
            return new RandomLockStatusRow(KeyOpposite, "Opposite", range, None, None, "RẢNH", false);
        }

        var remaining = RemainingSeconds(openAt.AddSeconds(s.OppositeSideLockSeconds), nowUtc);
        var lockedSide = s.LastOpenConfirmedSide == TradingPositionSide.Buy ? "SELL" : "BUY";
        return new RandomLockStatusRow(
            KeyOpposite, "Opposite", range, None,
            remaining > 0 ? $"{remaining}s" : None,
            remaining > 0 ? $"CHẶN Open {lockedSide}" : "RẢNH",
            remaining > 0);
    }

    private static RandomLockStatusRow GlobalRow(RandomLockInputs s, DateTime nowUtc)
    {
        var remaining = s.GlobalActionLockUntilUtc is { } until ? RemainingSeconds(until, nowUtc) : 0;
        return new RandomLockStatusRow(
            KeyGlobal, "Global cd", None, None,
            remaining > 0 ? $"{remaining}s" : None,
            remaining > 0 ? "CHẶN mọi Auto" : "RẢNH",
            remaining > 0);
    }

    private static int RemainingSeconds(DateTime deadlineUtc, DateTime nowUtc)
        => deadlineUtc > nowUtc ? (int)Math.Ceiling((deadlineUtc - nowUtc).TotalSeconds) : 0;

    private static string SideText(TradingPositionSide side) => side switch
    {
        TradingPositionSide.Buy => "BUY",
        TradingPositionSide.Sell => "SELL",
        _ => string.Empty
    };
}

/// <summary>
/// Chuỗi hiển thị cho các giá trị random theo từng cặp (cột mới ở bảng Profit Realtime).
/// </summary>
public static class SlotRandomTextFormatter
{
    public static string FormatHolding(PositionSlot? slot, DateTime nowUtc)
        => slot is null ? "-" : FormatCountdown(slot.HoldingSeconds, slot.OpenConfirmedAtUtc ?? slot.OpenedAtUtc, nowUtc);

    public static string FormatPostOpen(PositionSlot? slot, DateTime nowUtc)
        => slot is null ? "-" : FormatCountdown(slot.SelectedPostOpenLockSeconds, slot.OpenConfirmedAtUtc, nowUtc);

    public static string FormatHwndProfile(PositionSlot? slot)
        => slot?.HwndProfileIndex is { } index ? $"#{index + 1}" : "-";

    /// <summary>
    /// "45s" khi chưa có mốc (chưa confirm), "45s còn 12s" khi đang chờ, "45s ✓" khi đã hết.
    /// </summary>
    public static string FormatCountdown(int seconds, DateTime? anchorUtc, DateTime nowUtc)
    {
        if (anchorUtc is not { } anchor)
        {
            return $"{seconds}s";
        }

        var deadline = anchor.AddSeconds(seconds);
        if (deadline <= nowUtc)
        {
            return $"{seconds}s ✓";
        }

        var remaining = (int)Math.Ceiling((deadline - nowUtc).TotalSeconds);
        return $"{seconds}s còn {remaining}s";
    }
}
