using System.Windows;
using TradeDesktop.App.ViewModels;

namespace TradeDesktop.App;

public partial class ConfigWindow : Window
{
    private readonly ConfigViewModel _viewModel;

    public ConfigWindow(ConfigViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _viewModel.RequestClose += OnRequestClose;
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        base.OnClosed(e);
    }

    // PasswordBox không bind hai chiều được: đẩy giá trị một chiều vào ViewModel, không bao giờ đọc ngược.
    private void OnCTraderPasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.SetCTraderPassword(CTraderPasswordBox.Password);
    }

    private void OnRequestClose(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }
}
