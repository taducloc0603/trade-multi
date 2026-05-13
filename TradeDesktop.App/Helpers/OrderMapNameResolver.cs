namespace TradeDesktop.App.Helpers;

public static class OrderMapNameResolver
{
    public static string BuildTradeMapName(string tickMapName)
        => BuildTargetMapName(tickMapName, "_Trades");

    public static string BuildHistoryMapName(string tickMapName)
        => BuildTargetMapName(tickMapName, "_History");

    private static string BuildTargetMapName(string tickMapName, string targetSuffix)
    {
        if (string.IsNullOrWhiteSpace(tickMapName))
        {
            return string.Empty;
        }

        var normalized = tickMapName.Trim();
        const string tickSuffix = "_Tick";

        if (normalized.EndsWith(tickSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(normalized.AsSpan(0, normalized.Length - tickSuffix.Length), targetSuffix);
        }

        return string.Concat(normalized, targetSuffix);
    }
}