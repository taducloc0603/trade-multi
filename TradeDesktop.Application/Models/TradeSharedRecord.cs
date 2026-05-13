namespace TradeDesktop.Application.Models;

public sealed record TradeSharedRecord(
    ulong Ticket,
    string Symbol,
    int TradeType,
    double Lot,
    double Price,
    double Sl,
    double Tp,
    double Profit,
    ulong TimeMsc,
    ulong OpenEaTimeLocal);