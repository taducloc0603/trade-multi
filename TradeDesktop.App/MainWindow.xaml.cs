using System.ComponentModel;
using System.Windows;
using TradeDesktop.App.Services;
using TradeDesktop.App.ViewModels;

namespace TradeDesktop.App;

public partial class MainWindow : Window
{
    private SignalLogWindow? _signalLogWindow;
    private readonly IPrimeXbtBrowserService? _primeXbtBrowserService;

    public MainWindow(DashboardViewModel viewModel, IPrimeXbtBrowserService? primeXbtBrowserService = null)
    {
        InitializeComponent();
        _primeXbtBrowserService = primeXbtBrowserService;
        DataContext = viewModel;
        Closing += OnMainWindowClosing;
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        if (_signalLogWindow is { IsLoaded: true })
        {
            if (_signalLogWindow.WindowState == WindowState.Minimized)
            {
                _signalLogWindow.WindowState = WindowState.Normal;
            }

            _signalLogWindow.Activate();
            return;
        }

        _signalLogWindow = new SignalLogWindow
        {
            Owner = this,
            DataContext = DataContext
        };
        _signalLogWindow.Closed += (_, _) => _signalLogWindow = null;
        _signalLogWindow.Show();
    }

    private async void OnOpenPrimeXbtClick(object sender, RoutedEventArgs e)
    {
        if (_primeXbtBrowserService is null)
        {
            return;
        }

        await _primeXbtBrowserService.OpenOrFocusAsync();
        var status = _primeXbtBrowserService.Status;
        // Chỉ báo khi không mở/attach được; trường hợp bình thường Chrome tự hiện lên.
        if (!_primeXbtBrowserService.IsSyncing)
        {
            MessageBox.Show(this, status, "PrimeXBT", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Bạn có chắc muốn tắt ứng dụng không?",
            "Xác nhận thoát",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }
}
