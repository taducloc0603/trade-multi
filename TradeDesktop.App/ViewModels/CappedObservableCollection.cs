using System.Collections.ObjectModel;

namespace TradeDesktop.App.ViewModels;

public class CappedObservableCollection<T> : ObservableCollection<T>
{
    private readonly int _maxCount;

    public CappedObservableCollection(int maxCount)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        _maxCount = maxCount;
    }

    protected override void InsertItem(int index, T item)
    {
        base.InsertItem(index, item);

        // Trim oldest entries from the tail. Safe to call RemoveAt here because
        // base.InsertItem has already returned from its CollectionChanged dispatch,
        // so reentrancy is no longer blocked.
        while (Count > _maxCount)
        {
            RemoveAt(Count - 1);
        }
    }
}

public sealed class SignalLogCollection : CappedObservableCollection<TradeDesktop.Application.Models.MinimalSignalLogItem>
{
    private readonly Action<string> _legacyLogSink;

    public SignalLogCollection(int maxCount, Action<string> legacyLogSink)
        : base(maxCount)
    {
        _legacyLogSink = legacyLogSink;
    }

    // Only dev-4 trade lines belong to Minimal Signal. Diagnostics remain file-only.
    public void Insert(int index, string legacyMessage)
    {
        if (!string.IsNullOrWhiteSpace(legacyMessage))
        {
            _legacyLogSink(legacyMessage);
            if (IsMinimalTradeLine(legacyMessage))
            {
                var category = legacyMessage.Contains("CLOSE", StringComparison.OrdinalIgnoreCase)
                    ? "Close"
                    : "Open";
                var outcome = ResolveOutcome(legacyMessage);
                base.InsertItem(index, new TradeDesktop.Application.Models.MinimalSignalLogItem(
                    AppendVietnameseDescription(legacyMessage, category, outcome),
                    category,
                    outcome));
            }
        }
    }

    private static bool IsMinimalTradeLine(string message)
        => message.Contains("> [", StringComparison.Ordinal)
           && message.Contains("]. ", StringComparison.Ordinal)
           && (message.Contains("OPEN", StringComparison.OrdinalIgnoreCase)
               || message.Contains("CLOSE", StringComparison.OrdinalIgnoreCase)
               || message.Contains(" by Gap ", StringComparison.OrdinalIgnoreCase)
               || message.Contains(" by Manual", StringComparison.OrdinalIgnoreCase));

    private static string ResolveOutcome(string message)
        => message.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "Failed"
            : message.Contains("blocked", StringComparison.OrdinalIgnoreCase)
              || message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ? "Blocked"
            : message.Contains("Slippage=", StringComparison.OrdinalIgnoreCase) ? "Confirmed"
            : "Detected";

    private static string AppendVietnameseDescription(string message, string category, string outcome)
    {
        if (message.Contains("Mô tả=", StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        var exchange = message.Contains(":B]", StringComparison.Ordinal) ? "B" : "A";
        var action = category == "Close" ? "đóng" : "mở";
        var description = outcome switch
        {
            "Confirmed" => $"Chân {exchange} đã {action} thành công",
            "Failed" => $"{char.ToUpperInvariant(action[0])}{action[1..]} chân {exchange} thất bại",
            "Blocked" => $"Chân {exchange} chưa được phép {action}",
            _ => $"Phát hiện tín hiệu {action} chân {exchange}"
        };
        return $"{message} | Mô tả=\"{description}\"";
    }
}
