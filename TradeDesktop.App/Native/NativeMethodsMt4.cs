using System.Runtime.InteropServices;

namespace TradeDesktop.App.Native;

internal static class NativeMethodsMt4
{
    private const string DllName = "mt5engine_capi";

    [DllImport(DllName, EntryPoint = "mt_is_valid_window", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IsValidWindow(ulong hwnd);

    [DllImport(DllName, EntryPoint = "mt_create_context_from_parent", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr CreateContextFromParent(ulong parentHwnd);

    [DllImport(DllName, EntryPoint = "mt_update_row_count", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int UpdateRowCount(IntPtr context);

    [DllImport(DllName, EntryPoint = "mt_close_position_mt4", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClosePositionMt4(IntPtr context, int rowIndex);

    [DllImport(DllName, EntryPoint = "mt_destroy_context", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DestroyContext(IntPtr context);

    [DllImport(DllName, EntryPoint = "mt_click_buy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClickBuy(ulong chartHwnd);

    [DllImport(DllName, EntryPoint = "mt_click_sell", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClickSell(ulong chartHwnd);
}