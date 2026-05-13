using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IExchangePairReader
{
    event EventHandler<SharedMemorySnapshot>? SnapshotReceived;
    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
