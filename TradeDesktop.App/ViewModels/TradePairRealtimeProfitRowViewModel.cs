namespace TradeDesktop.App.ViewModels;

public sealed class TradePairRealtimeProfitRowViewModel : ObservableObject
{
    private string _profitRealtime;
    private string _pairId;

    public TradePairRealtimeProfitRowViewModel(string stt, string profitRealtime, string pairId)
    {
        Stt = stt;
        _profitRealtime = profitRealtime;
        _pairId = pairId;
    }

    public string Stt { get; }
    public string ProfitRealtime
    {
        get => _profitRealtime;
        private set => SetProperty(ref _profitRealtime, value);
    }

    public string PairId
    {
        get => _pairId;
        private set => SetProperty(ref _pairId, value);
    }

    public void Update(string profitRealtime, string pairId)
    {
        ProfitRealtime = profitRealtime;
        PairId = pairId;
    }
}
