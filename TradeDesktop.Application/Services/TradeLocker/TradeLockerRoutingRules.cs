using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.TradeLocker;

// Luật so khớp thuần cho sàn B = TradeLocker (docs/plans/tradelocker), đặt ở Application để test được.
// Tách riêng khỏi cTrader/PrimeXBT: mỗi nền tảng một kênh map, không được nhận nhầm của nhau.
// Hậu tố phải khớp OrderMapNameResolver (App/Helpers): "<tick>_Trades" / "<tick>_History".
public static class TradeLockerRoutingRules
{
    public const string PlatformName = "tradelocker";

    // Kênh map cố định khi platform_b = tradelocker. Không có EA nào ghi MMF tên này, nên trước khi có
    // quote session (Phase 4) reader MMF luôn trả Disconnected cho sàn B — fail-closed, không có signal.
    public const string ChannelMapName = "TRADELOCKER_B";
    public const string TradeMapName = ChannelMapName + "_Trades";
    public const string HistoryMapName = ChannelMapName + "_History";

    public static bool IsTradeLockerPlatform(string? platform)
        => string.Equals((platform ?? string.Empty).Trim(), PlatformName, StringComparison.OrdinalIgnoreCase);

    public static bool IsTradeLockerTradeMap(string? platformB, string? mapName)
        => IsTradeLockerPlatform(platformB) && MatchesExactly(mapName, TradeMapName);

    public static bool IsTradeLockerHistoryMap(string? platformB, string? mapName)
        => IsTradeLockerPlatform(platformB) && MatchesExactly(mapName, HistoryMapName);

    // Ngưỡng latency riêng cho chân B khi B là TradeLocker (sans_json.tradelocker.confirmLatencyB).
    // null ⇒ caller dùng ngưỡng chung. Chỉ có hiệu lực khi platform_b = tradelocker: giá trị còn sót trong sans_json
    // sau khi đổi B sang nền tảng khác KHÔNG được áp dụng. Không clamp: 0 = tắt guard latency riêng cho chân B.
    public static int? ResolveConfirmLatencyB(string? platformB, TradeLockerConfig? config)
        => IsTradeLockerPlatform(platformB) ? config?.ConfirmLatencyB : null;

    private static bool MatchesExactly(string? mapName, string expected)
        => !string.IsNullOrWhiteSpace(mapName)
           && string.Equals(mapName.Trim(), expected, StringComparison.Ordinal);
}
