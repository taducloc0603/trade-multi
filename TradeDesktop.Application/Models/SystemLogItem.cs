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
        _ when IsSignal => "Signal",
        "SLOT" or "CYCLE" or "FLOW" or "CLOSE_SELECT" or "TRADE_GATE" or "OPPOSITE_OPEN_GUARD"
            or "COOLDOWN" or "MIN_PROFIT" or "TRADE_POLICY" or "GUARD" => "Trading",
        "MT4" or "MT5" or "ROUTER" or "MANUAL" or "HWND_PROFILE" => "Execution",
        "RECOVERY" or "WATCHDOG" or "PERSIST" => "Recovery",
        "MARKET" or "MMF_TRADES" or "MMF_HISTORY" => "Market",
        "CONN" or "HWND" => "Connection",
        _ => "Application"
    };

    public bool IsSignal => Category.StartsWith("SIGNAL_", StringComparison.OrdinalIgnoreCase);

    // Structured SignalLogItem already carries its own timestamp. Avoid rendering a second
    // logger timestamp when the same line is mirrored into System / All.
    public string DisplayText => IsSignal
        ? Message
        : $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {Message}";

    public static SystemLogItem Parse(DateTime timestamp, string message, SystemLogSeverity severity)
    {
        var category = ReadBracketToken(message, 0);
        var eventType = ReadBracketToken(message, 1);

        // Structured SignalLogItem.ToString() starts with its own [yyyy-MM-dd HH:mm:ss.fff]
        // before [SIGNAL_*][LEVEL]. Normalize to the signal tokens for domain/filter/color.
        if (eventType.StartsWith("SIGNAL_", StringComparison.OrdinalIgnoreCase))
        {
            category = eventType;
            eventType = ReadBracketToken(message, 2);
        }

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
            ToUpperInvariantIfNeeded(category),
            ToUpperInvariantIfNeeded(eventType),
            severity,
            message);
    }

    /// <summary>
    /// Token category/event trong log gần như luôn đã viết hoa, nên <c>ToUpperInvariant()</c> chỉ
    /// cấp phát thêm một chuỗi giống hệt. Parse chạy cho MỌI dòng publish realtime, tức trên UI
    /// thread, nên hai lần cấp phát thừa mỗi dòng là đáng bỏ.
    ///
    /// Chỉ trả về nguyên chuỗi khi nó thuần ASCII và không có chữ thường — trong trường hợp đó
    /// <c>ToUpperInvariant()</c> chắc chắn là phép đồng nhất. Gặp bất kỳ ký tự non-ASCII nào thì
    /// vẫn gọi như cũ, tránh mọi khác biệt ở các ký tự Unicode titlecase.
    /// </summary>
    private static string ToUpperInvariantIfNeeded(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character > 127 || (character >= 'a' && character <= 'z'))
            {
                return value.ToUpperInvariant();
            }
        }

        return value;
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
