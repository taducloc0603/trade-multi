namespace TradeDesktop.Application.Services;

public static class GuardReasonCodeResolver
{
    public static string Resolve(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "TRADE_POLICY_BLOCKED";
        if (reason.Contains("latency", StringComparison.OrdinalIgnoreCase)) return "LATENCY_GUARD";
        if (reason.Contains("spread", StringComparison.OrdinalIgnoreCase)) return "SPREAD_GUARD";
        if (reason.Contains("freeze", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("đóng băng", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("mẫu cuối", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("price", StringComparison.OrdinalIgnoreCase)) return "PRICE_FREEZE_GUARD";
        if (reason.Contains("gap", StringComparison.OrdinalIgnoreCase)) return "MAX_GAP_GUARD";
        return "TRADE_POLICY_BLOCKED";
    }
}
