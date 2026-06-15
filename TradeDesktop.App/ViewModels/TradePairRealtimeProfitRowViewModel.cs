namespace TradeDesktop.App.ViewModels;

public sealed class TradePairRealtimeProfitRowViewModel
{
    public TradePairRealtimeProfitRowViewModel(string stt, string profitRealtime, string pairId)
    {
        Stt = stt;
        ProfitRealtime = profitRealtime;
        PairId = pairId;
    }

    public string Stt { get; }
    public string ProfitRealtime { get; }
    public string PairId { get; }
}
