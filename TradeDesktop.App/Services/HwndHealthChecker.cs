using TradeDesktop.App.Native;
using TradeDesktop.Application.Services;

namespace TradeDesktop.App.Services;

/// <summary>
/// Probe thật dùng native <c>mt_is_valid_window</c> trong <c>mt5engine_capi</c> (Windows-only).
/// MT4 và MT5 wrapper cùng trỏ entrypoint <c>mt_is_valid_window</c> nên 1 probe đủ cho cả 2 sàn.
/// </summary>
public sealed class NativeWindowProbe : IWindowProbe
{
    public bool WindowExists(ulong hwnd)
    {
        try
        {
            return NativeMethodsMt5.IsValidWindow(hwnd) == 1;
        }
        catch
        {
            return false;
        }
    }
}
