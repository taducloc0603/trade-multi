using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

/// <summary>
/// Parse cột <c>hold_confirm_mode</c>. Strict: chỉ đúng <c>"ms"</c> hoặc <c>"ticks"</c> (ordinal, không trim,
/// không lower). <c>null</c> = cột chưa migrate ⇒ <see cref="HoldConfirmMode.Ms"/> (hành vi cũ).
/// Mọi giá trị khác (<c>"tick"</c>, <c>"MS"</c>, <c>"ms "</c>, <c>""</c>...) bị từ chối để config không
/// lặng lẽ chạy sai mode — ConfigService trả <c>ConfigLoadResult.Failed</c>.
/// </summary>
public static class HoldConfirmModeParser
{
    public const string MsValue = "ms";
    public const string TicksValue = "ticks";

    public static bool TryParse(string? raw, out HoldConfirmMode mode, out string error)
    {
        switch (raw)
        {
            case null:
            case MsValue:
                mode = HoldConfirmMode.Ms;
                error = string.Empty;
                return true;
            case TicksValue:
                mode = HoldConfirmMode.Ticks;
                error = string.Empty;
                return true;
            default:
                mode = HoldConfirmMode.Ms;
                error = $"giá trị '{raw}' không hợp lệ, chỉ nhận đúng '{MsValue}' hoặc '{TicksValue}'.";
                return false;
        }
    }

    /// <summary>Chuỗi ghi log/DB của mode.</summary>
    public static string ToConfigValue(HoldConfirmMode mode) =>
        mode == HoldConfirmMode.Ticks ? TicksValue : MsValue;

    /// <summary>
    /// Kiểm tra cả cụm cột hold_confirm_mode. Ticks âm bị từ chối (DB có check constraint nhưng app không
    /// dựa vào đó). Ticks được kiểm ở cả hai mode để lỗi cấu hình lộ ra trước khi đổi sang ticks.
    /// </summary>
    public static bool TryValidate(
        string? raw,
        int openHoldConfirmTicks,
        int closeHoldConfirmTicks,
        out HoldConfirmMode mode,
        out string error)
    {
        if (!TryParse(raw, out mode, out var modeError))
        {
            error = $"Cấu hình hold_confirm_mode không hợp lệ: {modeError}";
            return false;
        }

        if (openHoldConfirmTicks < 0 || closeHoldConfirmTicks < 0)
        {
            error = "Cấu hình hold_confirm_mode không hợp lệ: open_hold_confirm_ticks / close_hold_confirm_ticks phải >= 0 " +
                $"(open={openHoldConfirmTicks}, close={closeHoldConfirmTicks}).";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
