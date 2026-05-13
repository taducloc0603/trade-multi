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

    private void OnRequestClose(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }
}
