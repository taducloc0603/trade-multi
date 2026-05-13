namespace TradeDesktop.Application.Models;

public sealed record SharedMemoryTickRecord(
    int Version,
    long TimestampMs,
    double Bid,
    double Ask,
    double Spread,
    long TickTimeMsc,
    string Symbol);
