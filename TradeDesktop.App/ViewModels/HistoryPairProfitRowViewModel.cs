namespace TradeDesktop.App.ViewModels;

public sealed class HistoryPairProfitRowViewModel : ObservableObject
{
    private string _stt;
    private string _profit;
    private string _profitDollar;

    public HistoryPairProfitRowViewModel(string stt, string profit, string profitDollar)
    {
        _stt = stt;
        _profit = profit;
        _profitDollar = profitDollar;
    }

    public string Stt { get => _stt; set => SetProperty(ref _stt, value); }
    public string Profit { get => _profit; set => SetProperty(ref _profit, value); }
    public string ProfitDollar { get => _profitDollar; set => SetProperty(ref _profitDollar, value); }
}
