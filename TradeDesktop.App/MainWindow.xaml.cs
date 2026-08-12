using System.ComponentModel;
using System.Windows;
using TradeDesktop.App.ViewModels;

namespace TradeDesktop.App;

public partial class MainWindow : Window
{
    private SignalLogWindow? _signalLogWindow;

    public MainWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
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
