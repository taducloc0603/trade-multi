namespace TradeDesktop.App.ViewModels;

public sealed class TradePairRealtimeProfitRowViewModel
{
    public TradePairRealtimeProfitRowViewModel(string stt, string profitRealtime)
    {
        Stt = stt;
        ProfitRealtime = profitRealtime;
    }

    public string Stt { get; }
    public string ProfitRealtime { get; }
}
