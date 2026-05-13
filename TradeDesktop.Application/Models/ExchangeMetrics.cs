namespace TradeDesktop.Application.Models;

public sealed record ExchangeMetrics(
    string? Symbol,
    decimal? Bid,
    decimal? Ask,
    decimal? Spread,
    decimal? LatencyMs,
    float? Tps,
    string? Time,
    decimal? MaxLatMs,
    decimal? AvgLatMs,
    bool IsConnected,
    string? Error);
