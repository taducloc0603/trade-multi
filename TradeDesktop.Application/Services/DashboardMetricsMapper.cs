using System.Globalization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Application.Services;

public sealed class DashboardMetricsMapper(IGapCalculator gapCalculator) : IDashboardMetricsMapper
{
    public DashboardMetrics Map(SharedMemorySnapshot snapshot)
    {
        var exchangeA = MapExchange(snapshot.SanA, snapshot.TimestampUtc);
        var exchangeB = MapExchange(snapshot.SanB, snapshot.TimestampUtc);
        var (gapBuy, gapSell) = gapCalculator.Calculate(snapshot.SanA, snapshot.SanB);

        return new DashboardMetrics(
            ExchangeA: exchangeA,
            ExchangeB: exchangeB,
            GapBuy: gapBuy,
            GapSell: gapSell,
            IsConnectedA: exchangeA.IsConnected,
            IsConnectedB: exchangeB.IsConnected,
            TimestampUtc: snapshot.TimestampUtc);
    }

    private static ExchangeDashboardMetrics MapExchange(ExchangeMetrics source, DateTime timestampUtc)
    {
        // TODO: Khi chốt schema SHM chính thức, map Time/Latency/TPS theo field name cố định.
        var spread = source.Spread ?? CalculateSpread(source.Bid, source.Ask);
        var localTime = string.IsNullOrWhiteSpace(source.Time)
            ? timestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : source.Time;

        return new ExchangeDashboardMetrics(
            Symbol: string.IsNullOrWhiteSpace(source.Symbol) ? "-" : source.Symbol,
            Bid: source.Bid,
            Ask: source.Ask,
            Spread: spread,
            LatencyMs: source.LatencyMs,
            Tps: source.Tps,
            Time: localTime,
            MaxLatMs: source.MaxLatMs,
            AvgLatMs: source.AvgLatMs,
            IsConnected: source.IsConnected,
            Error: source.Error);
    }

    private static decimal? CalculateSpread(decimal? bid, decimal? ask)
        => bid.HasValue && ask.HasValue ? ask.Value - bid.Value : null;
}
