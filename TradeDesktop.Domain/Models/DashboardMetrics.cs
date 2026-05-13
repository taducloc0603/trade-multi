namespace TradeDesktop.Domain.Models;

public sealed record ExchangeDashboardMetrics(
    string Symbol,
    decimal? Bid,
    decimal? Ask,
    decimal? Spread,
    decimal? LatencyMs,
    float? Tps,
    string Time,
    decimal? MaxLatMs,
    decimal? AvgLatMs,
    bool IsConnected,
    string? Error);

public sealed record DashboardMetrics(
    ExchangeDashboardMetrics ExchangeA,
    ExchangeDashboardMetrics ExchangeB,
    int? GapBuy,
    int? GapSell,
    bool IsConnectedA,
    bool IsConnectedB,
    DateTime TimestampUtc);
