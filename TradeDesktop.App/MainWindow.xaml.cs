using System.ComponentModel;
using System.Windows;
using TradeDesktop.App.ViewModels;

namespace TradeDesktop.App;

public partial class MainWindow : Window
{
    public MainWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnMainWindowClosing;
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