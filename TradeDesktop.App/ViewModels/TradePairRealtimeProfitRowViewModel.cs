namespace TradeDesktop.App.ViewModels;

public sealed class TradePairRealtimeProfitRowViewModel : ObservableObject
{
    private string _stt;
    private string _profitRealtime;

    public TradePairRealtimeProfitRowViewModel(string stt, string profitRealtime)
    {
        _stt = stt;
        _profitRealtime = profitRealtime;
    }

    public string Stt { get => _stt; set => SetProperty(ref _stt, value); }
    public string ProfitRealtime { get => _profitRealtime; set => SetProperty(ref _profitRealtime, value); }
}
