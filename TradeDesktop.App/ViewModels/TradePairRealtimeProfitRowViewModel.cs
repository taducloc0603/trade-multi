namespace TradeDesktop.App.ViewModels;

public sealed class TradePairRealtimeProfitRowViewModel : ObservableObject
{
    private string _profitRealtime;
    private string _pairId;
    private string _holdingText;
    private string _postOpenText;
    private string _hwndProfileText;

    public TradePairRealtimeProfitRowViewModel(
        string stt,
        string profitRealtime,
        string pairId,
        string holdingText = "-",
        string postOpenText = "-",
        string hwndProfileText = "-")
    {
        Stt = stt;
        _profitRealtime = profitRealtime;
        _pairId = pairId;
        _holdingText = holdingText;
        _postOpenText = postOpenText;
        _hwndProfileText = hwndProfileText;
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

    // Giá trị random theo cặp, chỉ để hiển thị (SlotRandomTextFormatter).
    public string HoldingText
    {
        get => _holdingText;
        private set => SetProperty(ref _holdingText, value);
    }

    public string PostOpenText
    {
        get => _postOpenText;
        private set => SetProperty(ref _postOpenText, value);
    }

    public string HwndProfileText
    {
        get => _hwndProfileText;
        private set => SetProperty(ref _hwndProfileText, value);
    }

    public void Update(
        string profitRealtime,
        string pairId,
        string holdingText = "-",
        string postOpenText = "-",
        string hwndProfileText = "-")
    {
        ProfitRealtime = profitRealtime;
        PairId = pairId;
        HoldingText = holdingText;
        PostOpenText = postOpenText;
        HwndProfileText = hwndProfileText;
    }
}
