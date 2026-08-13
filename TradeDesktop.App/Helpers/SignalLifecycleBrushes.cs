using System.Windows.Media;

namespace TradeDesktop.App.Helpers;

public static class SignalLifecycleBrushes
{
    public static readonly Brush Detected = Create("#175CD3");
    public static readonly Brush Confirmed = Create("#067647");
    public static readonly Brush Blocked = Create("#B54708");
    public static readonly Brush Failed = Create("#B42318");

    public static Brush Resolve(string outcome)
        => outcome switch
        {
            "Confirmed" => Confirmed,
            "Blocked" => Blocked,
            "Failed" => Failed,
            _ => Detected
        };

    public static string ResolveSystemOutcome(string eventType)
        => eventType.EndsWith("_CONFIRMED", StringComparison.Ordinal) ? "Confirmed"
            : eventType.EndsWith("_BLOCKED", StringComparison.Ordinal)
              || eventType.EndsWith("_CANCELLED", StringComparison.Ordinal) ? "Blocked"
            : eventType.EndsWith("_FAILED", StringComparison.Ordinal) ? "Failed"
            : "Detected";

    private static Brush Create(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
