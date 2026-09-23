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
    string? Error,
    // Khoảng cách giữa hai tick gần nhất — CHỈ để hiển thị, chỉ chân cTrader set (chân MMF để null).
    // Đặt CUỐI record vì đây là positional record. Guard/router KHÔNG dùng trường này.
    decimal? TickIntervalMs = null);

public sealed record DashboardMetrics(
    ExchangeDashboardMetrics ExchangeA,
    ExchangeDashboardMetrics ExchangeB,
    int? GapBuy,
    int? GapSell,
    bool IsConnectedA,
    bool IsConnectedB,
    DateTime TimestampUtc);
