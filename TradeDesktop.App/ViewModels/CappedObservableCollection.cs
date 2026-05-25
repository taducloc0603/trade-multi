using System.Collections.ObjectModel;

namespace TradeDesktop.App.ViewModels;

internal sealed class CappedObservableCollection<T> : ObservableCollection<T>
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
