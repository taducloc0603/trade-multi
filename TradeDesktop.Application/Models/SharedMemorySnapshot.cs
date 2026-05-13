namespace TradeDesktop.Application.Models;

public sealed record SharedMemorySnapshot(
    ExchangeMetrics SanA,
    ExchangeMetrics SanB,
    DateTime TimestampUtc);
