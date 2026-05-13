namespace TradeDesktop.Domain.Models;

public sealed record MarketData(
    decimal Bid,
    decimal Ask,
    DateTime Timestamp,
    bool IsConnected)
{
    public decimal Spread => Ask - Bid;
}