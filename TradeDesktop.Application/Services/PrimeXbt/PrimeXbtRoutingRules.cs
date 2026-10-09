using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.PrimeXbt;

// Luật so khớp thuần cho sàn B = PrimeXBT, đặt ở Application để test được (test project không reference App).
// Tách riêng khỏi CTraderRoutingRules: hai nền tảng dùng kênh map khác nhau, không được nhận nhầm của nhau.
// Hậu tố phải khớp OrderMapNameResolver (App/Helpers): "<tick>_Trades" / "<tick>_History".
public static class PrimeXbtRoutingRules
{
    public const string PlatformName = "primexbt";

    // Kênh map cố định khi platform_b = primexbt. Không có EA nào ghi MMF tên này, nên trước khi có
    // quote session (Phase 4) reader MMF luôn trả Disconnected cho sàn B — fail-closed, không có signal.
    public const string ChannelMapName = "PRIMEXBT_B";
    public const string TradeMapName = ChannelMapName + "_Trades";
    public const string HistoryMapName = ChannelMapName + "_History";

    public static bool IsPrimeXbtPlatform(string? platform)
        => string.Equals((platform ?? string.Empty).Trim(), PlatformName, StringComparison.OrdinalIgnoreCase);

    public static bool IsPrimeXbtTradeMap(string? platformB, string? mapName)
        => IsPrimeXbtPlatform(platformB) && MatchesExactly(mapName, TradeMapName);

    public static bool IsPrimeXbtHistoryMap(string? platformB, string? mapName)
        => IsPrimeXbtPlatform(platformB) && MatchesExactly(mapName, HistoryMapName);

    // Ngưỡng latency riêng cho chân B khi B là PrimeXBT (sans_json.primexbt.confirmLatencyB).
    // null ⇒ caller dùng ngưỡng chung. Chỉ có hiệu lực khi platform_b = primexbt: giá trị còn sót trong sans_json
    // sau khi đổi B về MT/cTrader KHÔNG được áp dụng. Không clamp: 0 = tắt guard latency riêng cho chân B.
    public static int? ResolveConfirmLatencyB(string? platformB, PrimeXbtConfig? config)
        => IsPrimeXbtPlatform(platformB) ? config?.ConfirmLatencyB : null;

    private static bool MatchesExactly(string? mapName, string expected)
        => !string.IsNullOrWhiteSpace(mapName)
           && string.Equals(mapName.Trim(), expected, StringComparison.Ordinal);
}
