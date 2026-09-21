using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.CTrader;

// Luật so khớp thuần cho ICTraderRouting, đặt ở Application để test được (test project không reference App).
// Hậu tố phải khớp OrderMapNameResolver (App/Helpers): "<tick>_Trades" / "<tick>_History".
public static class CTraderRoutingRules
{
    public const string TradeMapName = CTraderFixConfig.ChannelMapName + "_Trades";
    public const string HistoryMapName = CTraderFixConfig.ChannelMapName + "_History";

    public static bool IsCTraderPlatform(string? platform)
        => string.Equals((platform ?? string.Empty).Trim(), "ctrader", StringComparison.OrdinalIgnoreCase);

    public static bool IsCTraderTradeMap(string? platformB, string? mapName)
        => IsCTraderPlatform(platformB) && MatchesExactly(mapName, TradeMapName);

    public static bool IsCTraderHistoryMap(string? platformB, string? mapName)
        => IsCTraderPlatform(platformB) && MatchesExactly(mapName, HistoryMapName);

    private static bool MatchesExactly(string? mapName, string expected)
        => !string.IsNullOrWhiteSpace(mapName)
           && string.Equals(mapName.Trim(), expected, StringComparison.Ordinal);
}
