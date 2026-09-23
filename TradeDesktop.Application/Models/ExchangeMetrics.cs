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
    string? Error,
    // Khoảng cách giữa hai tick gần nhất — CHỈ để hiển thị, và chỉ chân cTrader mới set (chân MMF để null).
    // Cố ý đặt CUỐI record: đây là positional record, chèn vào giữa sẽ dịch tham số của mọi call site.
    // KHÔNG dùng cho quyết định: guard/router vẫn so `LatencyMs` (tuổi tick) với ctrader_confirm_latency_b.
    decimal? TickIntervalMs = null);
