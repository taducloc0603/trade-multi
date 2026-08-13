using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TradeDesktop.App.ViewModels;
using TradeDesktop.Application.Models;

namespace TradeDesktop.App;

public partial class SignalLogWindow : Window
{
    private INotifyCollectionChanged? _logItems;
    private INotifyCollectionChanged? _systemLogItems;

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
        ScrollToNewestLog();
        ScrollToNewestSystemLog();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
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
    }

    private void OnLogItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(ScrollToNewestLog);
    }

    private void OnSystemLogItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(ScrollToNewestSystemLog);
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
}
