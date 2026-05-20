namespace TradeDesktop.App.ViewModels;

public sealed class TradeRowViewModel
{
    public TradeRowViewModel(
        string stt,
        string pairId,
        string timestamp,
        string count,
        string symbol,
        string ticket,
        string type,
        string lot,
        string price,
        string sl,
        string tp,
        string slippage,
        string profit,
        string feeSpread,
        string time,
        string openEaTimeLocal,
        string openExecution)
    {
        Stt = stt;
        PairId = pairId;
        Timestamp = timestamp;
        Count = count;
        Symbol = symbol;
        Ticket = ticket;
        Type = type;
        Lot = lot;
        Price = price;
        Sl = sl;
        Tp = tp;
        Slippage = slippage;
        Profit = profit;
        FeeSpread = feeSpread;
        Time = time;
        OpenEaTimeLocal = openEaTimeLocal;
        OpenExecution = openExecution;
    }

    public string Stt { get; }
    public string PairId { get; }
    public string Timestamp { get; }
    public string Count { get; }
    public string Symbol { get; }
    public string Ticket { get; }
    public string Type { get; }
    public string Lot { get; }
    public string Price { get; }
    public string Sl { get; }
    public string Tp { get; }
    public string Slippage { get; }
    public string Profit { get; }
    public string FeeSpread { get; }
    public string Time { get; }
    public string OpenEaTimeLocal { get; }
    public string OpenExecution { get; }
}