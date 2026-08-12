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

public sealed class SignalLogCollection : CappedObservableCollection<TradeDesktop.Application.Models.SignalLogItem>
{
    private readonly Action<string> _legacyLogSink;

    public SignalLogCollection(int maxCount, Action<string> legacyLogSink)
        : base(maxCount)
    {
        _legacyLogSink = legacyLogSink;
    }

    // Keep legacy strings completely outside the Signal UI collection. They are
    // written file-only until System / Execution log distribution is implemented.
    public void Insert(int index, string legacyMessage)
    {
        if (!string.IsNullOrWhiteSpace(legacyMessage))
        {
            _legacyLogSink(legacyMessage);
        }
    }
}
