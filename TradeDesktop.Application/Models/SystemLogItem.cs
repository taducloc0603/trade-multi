namespace TradeDesktop.Application.Models;

public enum SystemLogSeverity
{
    Debug,
    Info,
    Warn,
    Error
}

public sealed record SystemLogItem(
    DateTime Timestamp,
    string Category,
    string EventType,
    SystemLogSeverity Severity,
    string Message)
{
    public string Domain => Category switch
    {
        "SLOT" or "CYCLE" or "FLOW" or "CLOSE_SELECT" or "TRADE_GATE" or "OPPOSITE_OPEN_GUARD"
            or "COOLDOWN" or "MIN_PROFIT" or "TRADE_POLICY" or "GUARD" => "Trading",
        "MT4" or "MT5" or "ROUTER" or "MANUAL" or "HWND_PROFILE" => "Execution",
        "RECOVERY" or "WATCHDOG" or "PERSIST" => "Recovery",
        "MARKET" or "MMF_TRADES" or "MMF_HISTORY" => "Market",
        "CONN" or "HWND" => "Connection",
        _ => "Application"
    };

    public bool IsSignal => Category.StartsWith("SIGNAL_", StringComparison.OrdinalIgnoreCase);

    public string DisplayText => $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {Message}";

    public static SystemLogItem Parse(DateTime timestamp, string message, SystemLogSeverity severity)
    {
        var category = ReadBracketToken(message, 0);
        var eventType = ReadBracketToken(message, 1);

        if (string.IsNullOrWhiteSpace(category))
        {
            category = "GENERAL";
        }

        if (string.IsNullOrWhiteSpace(eventType)
            || eventType.Equals("DEBUG", StringComparison.OrdinalIgnoreCase)
            || eventType.Equals("INFO", StringComparison.OrdinalIgnoreCase)
            || eventType.Equals("WARN", StringComparison.OrdinalIgnoreCase)
            || eventType.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            eventType = category;
        }

        return new SystemLogItem(
            timestamp,
            category.ToUpperInvariant(),
            eventType.ToUpperInvariant(),
            severity,
            message);
    }

    private static string ReadBracketToken(string message, int tokenIndex)
    {
        var searchFrom = 0;
        for (var index = 0; index <= tokenIndex; index++)
        {
            var start = message.IndexOf('[', searchFrom);
            if (start < 0)
            {
                return string.Empty;
            }

            var end = message.IndexOf(']', start + 1);
            if (end <= start + 1)
            {
                return string.Empty;
            }

            if (index == tokenIndex)
            {
                return message[(start + 1)..end];
            }

            searchFrom = end + 1;
        }

        return string.Empty;
    }
}
