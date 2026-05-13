using TradeDesktop.Domain.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IMarketDataReader
{
    event EventHandler<MarketData>? MarketDataReceived;
    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}