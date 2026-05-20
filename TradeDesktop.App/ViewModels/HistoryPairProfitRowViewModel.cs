namespace TradeDesktop.App.ViewModels;

public sealed class HistoryPairProfitRowViewModel
{
    public HistoryPairProfitRowViewModel(string stt, string profit, string profitDollar)
    {
        Stt = stt;
        Profit = profit;
        ProfitDollar = profitDollar;
    }

    public string Stt { get; }
    public string Profit { get; }
    public string ProfitDollar { get; }
}
