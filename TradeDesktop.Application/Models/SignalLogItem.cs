using System.Globalization;

namespace TradeDesktop.Application.Models;

public enum SignalLogLevel
{
    Info,
    Warn,
    Error
}

public sealed record SignalLogItem(
    DateTime Timestamp,
    string EventType,
    SignalLogLevel Level,
    string Description,
    string Details)
{
    public string Category => EventType.StartsWith("SIGNAL_HEDGE", StringComparison.Ordinal)
        ? "Hedge"
        : EventType.StartsWith("SIGNAL_CLOSE", StringComparison.Ordinal)
            ? "Close"
            : EventType.StartsWith("SIGNAL_OPEN", StringComparison.Ordinal)
                ? "Open"
                : "Other";

    public string Outcome => EventType.EndsWith("_FAILED", StringComparison.Ordinal)
        ? "Failed"
        : EventType.EndsWith("_BLOCKED", StringComparison.Ordinal)
          || EventType.EndsWith("_CANCELLED", StringComparison.Ordinal)
            ? "Blocked"
            : EventType.EndsWith("_CONFIRMED", StringComparison.Ordinal)
                ? "Confirmed"
                : "Detected";

    public string DisplayText => ToString();

    public override string ToString()
    {
        var detailPrefix = string.IsNullOrWhiteSpace(Details) ? string.Empty : $"{Details} ";
        return $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{EventType}][{Level.ToString().ToUpperInvariant()}] " +
               $"{detailPrefix}description=\"{Description}\"";
    }

    public static string Field(string name, object? value)
    {
        var formatted = value switch
        {
            null => "-",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "-"
        };
        return $"{name}={formatted}";
    }
}
