using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using TradeDesktop.App.ViewModels;
using TradeDesktop.Application.Models;

namespace TradeDesktop.App;

public partial class SignalLogWindow : Window
{
    private INotifyCollectionChanged? _logItems;
    private INotifyCollectionChanged? _systemLogItems;
    private ScrollViewer? _signalScrollViewer;
    private ScrollViewer? _systemScrollViewer;
    private bool _followSignalLog = true;
    private bool _followSystemLog = true;
    private DispatcherOperation? _signalScrollOperation;
    private DispatcherOperation? _systemScrollOperation;
    private const double FollowTopThreshold = 1.0;

    public SignalLogWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel)
        {
            return;
        }

        _logItems = viewModel.SignalLogItems;
        _logItems.CollectionChanged += OnLogItemsChanged;
        _systemLogItems = viewModel.SystemLogItems;
        _systemLogItems.CollectionChanged += OnSystemLogItemsChanged;
        CollectionViewSource.GetDefaultView(SignalLogList.ItemsSource).Filter = FilterSignalLog;
        CollectionViewSource.GetDefaultView(SystemLogList.ItemsSource).Filter = FilterSystemLog;
        SignalLogList.UpdateLayout();
        SystemLogList.UpdateLayout();
        _signalScrollViewer = FindVisualChild<ScrollViewer>(SignalLogList);
        _systemScrollViewer = FindVisualChild<ScrollViewer>(SystemLogList);
        if (_signalScrollViewer is not null)
        {
            _signalScrollViewer.ScrollChanged += OnSignalScrollChanged;
        }
        if (_systemScrollViewer is not null)
        {
            _systemScrollViewer.ScrollChanged += OnSystemScrollChanged;
        }
        ScrollToNewestLog();
        ScrollToNewestSystemLog();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        AbortPendingScroll(ref _signalScrollOperation);
        AbortPendingScroll(ref _systemScrollOperation);

        if (_logItems is not null)
        {
            _logItems.CollectionChanged -= OnLogItemsChanged;
            _logItems = null;
        }

        if (_systemLogItems is not null)
        {
            _systemLogItems.CollectionChanged -= OnSystemLogItemsChanged;
            _systemLogItems = null;
        }
        if (_signalScrollViewer is not null)
        {
            _signalScrollViewer.ScrollChanged -= OnSignalScrollChanged;
            _signalScrollViewer = null;
        }
        if (_systemScrollViewer is not null)
        {
            _systemScrollViewer.ScrollChanged -= OnSystemScrollChanged;
            _systemScrollViewer = null;
        }
    }

    private void OnLogItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_followSignalLog)
        {
            ScheduleSignalScrollToNewest();
        }
        else
        {
            PreservePausedPosition(_signalScrollViewer, e);
        }
    }

    private void OnSystemLogItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_followSystemLog)
        {
            ScheduleSystemScrollToNewest();
        }
        else
        {
            PreservePausedPosition(_systemScrollViewer, e);
        }
    }

    private void OnSignalScrollChanged(object sender, ScrollChangedEventArgs e)
        => HandleScrollChanged((ScrollViewer)sender, e, isSignalLog: true);

    private void OnSystemScrollChanged(object sender, ScrollChangedEventArgs e)
        => HandleScrollChanged((ScrollViewer)sender, e, isSignalLog: false);

    private void HandleScrollChanged(ScrollViewer viewer, ScrollChangedEventArgs e, bool isSignalLog)
    {
        var shouldFollow = e.VerticalOffset <= FollowTopThreshold;
        if (isSignalLog)
        {
            _followSignalLog = shouldFollow;
            SignalFollowButton.Visibility = shouldFollow ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            _followSystemLog = shouldFollow;
            SystemFollowButton.Visibility = shouldFollow ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void PreservePausedPosition(ScrollViewer? viewer, NotifyCollectionChangedEventArgs e)
    {
        if (viewer is null
            || e.Action != NotifyCollectionChangedAction.Add
            || e.NewStartingIndex != 0
            || e.NewItems is null
            || e.NewItems.Count == 0)
        {
            return;
        }

        var offsetBeforeInsert = viewer.VerticalOffset;
        var insertedCount = e.NewItems.Count;
        Dispatcher.BeginInvoke(() =>
            viewer.ScrollToVerticalOffset(offsetBeforeInsert + insertedCount));
    }

    private void OnSignalFollowClick(object sender, RoutedEventArgs e)
    {
        _followSignalLog = true;
        SignalFollowButton.Visibility = Visibility.Collapsed;
        ScrollToNewestLog();
    }

    private void OnSystemFollowClick(object sender, RoutedEventArgs e)
    {
        _followSystemLog = true;
        SystemFollowButton.Visibility = Visibility.Collapsed;
        ScrollToNewestSystemLog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnSignalFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SignalLogList?.ItemsSource is not null)
        {
            CollectionViewSource.GetDefaultView(SignalLogList.ItemsSource).Refresh();
        }
    }

    private void OnSystemFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SystemLogList?.ItemsSource is not null)
        {
            CollectionViewSource.GetDefaultView(SystemLogList.ItemsSource).Refresh();
        }
    }

    private bool FilterSignalLog(object item)
    {
        var selected = (SignalFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        return item is MinimalSignalLogItem logItem
               && (selected == "All" || logItem.Category == selected);
    }

    private bool FilterSystemLog(object item)
    {
        if (item is not SystemLogItem logItem)
        {
            return false;
        }

        var domain = (SystemDomainFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        var level = (SystemLevelFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        return (domain == "All" || logItem.Domain == domain)
               && (level == "All" || logItem.Severity.ToString() == level);
    }

    private void ScrollToNewestLog()
    {
        if (SignalLogList.Items.Count > 0)
        {
            SignalLogList.ScrollIntoView(SignalLogList.Items[0]);
        }
    }

    private void ScrollToNewestSystemLog()
    {
        if (SystemLogList.Items.Count > 0)
        {
            SystemLogList.ScrollIntoView(SystemLogList.Items[0]);
        }
    }

    private void ScheduleSignalScrollToNewest()
    {
        if (_signalScrollOperation?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        _signalScrollOperation = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _signalScrollOperation = null;
                if (_followSignalLog)
                {
                    ScrollToNewestLog();
                }
            }));
    }

    private void ScheduleSystemScrollToNewest()
    {
        if (_systemScrollOperation?.Status == DispatcherOperationStatus.Pending)
        {
            return;
        }

        _systemScrollOperation = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _systemScrollOperation = null;
                if (_followSystemLog)
                {
                    ScrollToNewestSystemLog();
                }
            }));
    }

    private static void AbortPendingScroll(ref DispatcherOperation? operation)
    {
        if (operation?.Status == DispatcherOperationStatus.Pending)
        {
            operation.Abort();
        }

        operation = null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
