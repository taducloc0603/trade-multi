namespace TradeDesktop.App.ViewModels;

public sealed class OrderRecordItemViewModel
{
    public OrderRecordItemViewModel(string summary)
    {
        Summary = summary;
        HasStructuredColumns = false;
    }

    public string Summary { get; }

    public bool HasStructuredColumns { get; }

    public string Index { get; } = string.Empty;
    public string Symbol { get; } = string.Empty;
    public string Ticket { get; } = string.Empty;
    public string Type { get; } = string.Empty;
    public string Lot { get; } = string.Empty;
    public string Price { get; } = string.Empty;
    public string Sl { get; } = string.Empty;
    public string Tp { get; } = string.Empty;
    public string Profit { get; } = string.Empty;
    public string Time { get; } = string.Empty;
    public string TimeMsc { get; } = string.Empty;

    public string Timestamp { get; } = string.Empty;
    public string Count { get; } = string.Empty;
    public string Volume { get; } = string.Empty;
    public string OpenPrice { get; } = string.Empty;
    public string ClosePrice { get; } = string.Empty;
    public string Pnl { get; } = string.Empty;
    public string Commission { get; } = string.Empty;
    public string OpenTime { get; } = string.Empty;
    public string CloseTime { get; } = string.Empty;

    public OrderRecordItemViewModel(
        string index,
        string symbol,
        string ticket,
        string type,
        string lot,
        string price,
        string sl,
        string tp,
        string profit,
        string time,
        string timeMsc)
    {
        Index = index;
        Symbol = symbol;
        Ticket = ticket;
        Type = type;
        Lot = lot;
        Price = price;
        Sl = sl;
        Tp = tp;
        Profit = profit;
        Time = time;
        TimeMsc = timeMsc;
        HasStructuredColumns = true;

        Summary = string.Join(" | ",
            index,
            symbol,
            ticket,
            type,
            lot,
            price,
            sl,
            tp,
            profit,
            time,
            timeMsc);
    }

    public OrderRecordItemViewModel(
        string timestamp,
        string count,
        string symbol,
        string ticket,
        string type,
        string volume,
        string openPrice,
        string closePrice,
        string pnl,
        string commission,
        string openTime,
        string closeTime,
        string sl,
        string tp)
    {
        Timestamp = timestamp;
        Count = count;
        Symbol = symbol;
        Ticket = ticket;
        Type = type;
        Volume = volume;
        OpenPrice = openPrice;
        ClosePrice = closePrice;
        Pnl = pnl;
        Commission = commission;
        OpenTime = openTime;
        CloseTime = closeTime;
        Sl = sl;
        Tp = tp;
        HasStructuredColumns = true;

        Summary = string.Join(" | ",
            timestamp,
            count,
            symbol,
            ticket,
            type,
            volume,
            openPrice,
            closePrice,
            pnl,
            commission,
            openTime,
            closeTime,
            sl,
            tp);
    }
}