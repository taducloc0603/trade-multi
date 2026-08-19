using System.Collections.Concurrent;
using System.Text.Json;

namespace TradeDesktop.Infrastructure.Mt5Bridge;

internal sealed class Mt5BridgeResponsePump : IAsyncDisposable
{
    private readonly Mt5BridgeSharedMemoryRing _ring;
    private readonly Mt5BridgeHealthMonitor _health;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Mt5BridgeInboundMessage>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pumpTask;

    public Mt5BridgeResponsePump(Mt5BridgeSharedMemoryRing ring, Mt5BridgeHealthMonitor health)
    {
        _ring = ring;
        _health = health;
        _pumpTask = Task.Run(PumpAsync);
    }

    public TaskCompletionSource<Mt5BridgeInboundMessage> Register(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var source = new TaskCompletionSource<Mt5BridgeInboundMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, source))
        {
            throw new InvalidOperationException($"MT5 Bridge request '{requestId}' is already pending.");
        }

        return source;
    }

    public void Remove(string requestId)
    {
        _pending.TryRemove(requestId, out _);
    }

    private async Task PumpAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                foreach (var json in _ring.Drain())
                {
                    Mt5BridgeInboundMessage message;
                    try
                    {
                        message = Mt5BridgeProtocol.DeserializeInbound(json);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    _health.Observe(message, _ring.GapCount);
                    if (!string.IsNullOrWhiteSpace(message.RequestId) &&
                        _pending.TryGetValue(message.RequestId, out var waiter) &&
                        IsTerminalResponse(message))
                    {
                        _pending.TryRemove(message.RequestId, out _);
                        waiter.TrySetResult(message);
                    }
                }

                _health.SetGapCount(_ring.GapCount);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            foreach (var waiter in _pending.Values)
            {
                waiter.TrySetException(new ObjectDisposedException(nameof(Mt5BridgeResponsePump)));
            }
            _pending.Clear();
        }
    }

    private static bool IsTerminalResponse(Mt5BridgeInboundMessage message)
    {
        if (message.Type == "pong")
        {
            return true;
        }

        return message.Type == "execution_result" &&
               Mt5BridgeExecutionStatuses.IsFinal(message.Status);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _pumpTask.ConfigureAwait(false);
        _stop.Dispose();
    }
}
