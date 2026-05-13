namespace TradeDesktop.App.ViewModels;

public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(
        string stt,
        string pairId,
        string timestamp,
        string count,
        string symbol,
        string ticket,
        string type,
        string volume,
        string openPrice,
        string closePrice,
        string openSlippage,
        string closeSlippage,
        string profit,
        string feeSpread,
        string commission,
        string sl,
        string tp,
        string openTime,
        string closeTime,
        string closeEaTimeLocal,
        string openExecution,
        string closeExecution)
    {
        Stt = stt;
        PairId = pairId;
        Timestamp = timestamp;
        Count = count;
        Symbol = symbol;
        Ticket = ticket;
        Type = type;
        Volume = volume;
        OpenPrice = openPrice;
        ClosePrice = closePrice;
        OpenSlippage = openSlippage;
        CloseSlippage = closeSlippage;
        Profit = profit;
        FeeSpread = feeSpread;
        Commission = commission;
        Sl = sl;
        Tp = tp;
        OpenTime = openTime;
        CloseTime = closeTime;
        CloseEaTimeLocal = closeEaTimeLocal;
        OpenExecution = openExecution;
        CloseExecution = closeExecution;
    }

    public string Stt { get; }
    public string PairId { get; }
    public string Timestamp { get; }
    public string Count { get; }
    public string Symbol { get; }
    public string Ticket { get; }
    public string Type { get; }
    public string Volume { get; }
    public string OpenPrice { get; }
    public string ClosePrice { get; }
    public string OpenSlippage { get; }
    public string CloseSlippage { get; }
    public string Profit { get; }
    public string FeeSpread { get; }
    public string Commission { get; }
    public string Sl { get; }
    public string Tp { get; }
    public string OpenTime { get; }
    public string CloseTime { get; }
    public string CloseEaTimeLocal { get; }
    public string OpenExecution { get; }
    public string CloseExecution { get; }
}