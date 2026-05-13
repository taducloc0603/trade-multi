using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Infrastructure.MarketData;

public sealed class MockSharedMemoryMarketDataReader : ISharedMemoryReader
{
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private decimal _lastMidPrice = 100.00m;
    private readonly Random _random = new();

    public event EventHandler<SharedMemorySnapshot>? SnapshotReceived;

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _worker = Task.Run(() => PublishLoopAsync(_cts.Token), CancellationToken.None);
            IsRunning = true;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? worker;

        lock (_syncRoot)
        {
            if (!IsRunning)
            {
                return;
            }

            _cts?.Cancel();
            worker = _worker;
            _worker = null;
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }

        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // expected on stop
            }
        }
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var drift = (decimal)(_random.NextDouble() - 0.5) * 0.20m;
            _lastMidPrice = Math.Round(Math.Max(1m, _lastMidPrice + drift), 5);

            var sanA = new ExchangeMetrics(
                Symbol: "BTCUSDT",
                Bid: _lastMidPrice - 0.005m,
                Ask: _lastMidPrice + 0.005m,
                Spread: 0.01m,
                LatencyMs: 5,
                Tps: 100,
                Time: DateTime.Now.ToString("HH:mm:ss"),
                MaxLatMs: 10,
                AvgLatMs: 7,
                IsConnected: true,
                Error: null);

            var sanB = sanA with
            {
                Bid = sanA.Bid - 0.01m,
                Ask = sanA.Ask + 0.01m,
                Spread = (sanA.Ask + 0.01m) - (sanA.Bid - 0.01m)
            };

            SnapshotReceived?.Invoke(this, new SharedMemorySnapshot(sanA, sanB, DateTime.UtcNow));
        }
    }
}