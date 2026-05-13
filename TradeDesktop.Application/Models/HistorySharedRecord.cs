namespace TradeDesktop.Application.Models;

public sealed record HistorySharedRecord(
    ulong Ticket,
    int TradeType,
    double Volume,
    double OpenPrice,
    double ClosePrice,
    double Sl,
    double Tp,
    double Commission,
    double Profit,
    ulong OpenTimeMsc,
    ulong CloseTimeMsc,
    ulong CloseEaTimeLocal,
    string Symbol);