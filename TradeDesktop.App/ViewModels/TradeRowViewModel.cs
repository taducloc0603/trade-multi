namespace TradeDesktop.App.ViewModels;

public sealed class TradeRowViewModel : ObservableObject
{
    private string _stt;
    private string _pairId;
    private string _timestamp;
    private string _count;
    private string _symbol;
    private string _ticket;
    private string _type;
    private string _lot;
    private string _price;
    private string _sl;
    private string _tp;
    private string _slippage;
    private string _profit;
    private string _feeSpread;
    private string _time;
    private string _openEaTimeLocal;
    private string _openExecution;

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
        _stt = stt;
        _pairId = pairId;
        _timestamp = timestamp;
        _count = count;
        _symbol = symbol;
        _ticket = ticket;
        _type = type;
        _lot = lot;
        _price = price;
        _sl = sl;
        _tp = tp;
        _slippage = slippage;
        _profit = profit;
        _feeSpread = feeSpread;
        _time = time;
        _openEaTimeLocal = openEaTimeLocal;
        _openExecution = openExecution;
    }

    public string Stt { get => _stt; set => SetProperty(ref _stt, value); }
    public string PairId { get => _pairId; set => SetProperty(ref _pairId, value); }
    public string Timestamp { get => _timestamp; set => SetProperty(ref _timestamp, value); }
    public string Count { get => _count; set => SetProperty(ref _count, value); }
    public string Symbol { get => _symbol; set => SetProperty(ref _symbol, value); }
    public string Ticket { get => _ticket; set => SetProperty(ref _ticket, value); }
    public string Type { get => _type; set => SetProperty(ref _type, value); }
    public string Lot { get => _lot; set => SetProperty(ref _lot, value); }
    public string Price { get => _price; set => SetProperty(ref _price, value); }
    public string Sl { get => _sl; set => SetProperty(ref _sl, value); }
    public string Tp { get => _tp; set => SetProperty(ref _tp, value); }
    public string Slippage { get => _slippage; set => SetProperty(ref _slippage, value); }
    public string Profit { get => _profit; set => SetProperty(ref _profit, value); }
    public string FeeSpread { get => _feeSpread; set => SetProperty(ref _feeSpread, value); }
    public string Time { get => _time; set => SetProperty(ref _time, value); }
    public string OpenEaTimeLocal { get => _openEaTimeLocal; set => SetProperty(ref _openEaTimeLocal, value); }
    public string OpenExecution { get => _openExecution; set => SetProperty(ref _openExecution, value); }
}
