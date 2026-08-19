namespace TradeDesktop.Infrastructure.Mt5Bridge;

public sealed class Mt5BridgeSharedMemoryTransport : IMt5BridgeTransport
{
    private readonly Mt5BridgeSharedMemoryRing _ring;
    private readonly Mt5BridgeHealthMonitor _health;
    private readonly Mt5BridgeResponsePump _pump;
    private bool _disposed;

    private Mt5BridgeSharedMemoryTransport(
        Mt5BridgeSharedMemoryRing ring,
        Mt5BridgeHealthMonitor health)
    {
        _ring = ring;
        _health = health;
        _pump = new Mt5BridgeResponsePump(ring, health);
    }

    public Mt5BridgeHealth Health => _health.Current;

    public static Mt5BridgeSharedMemoryTransport Connect(
        string roomId,
        uint? writerUid = null,
        TimeSpan? staleAfter = null)
    {
        var uid = writerUid ?? CreateWriterUid($"TradeDesktop:{roomId.Trim()}");
        var generation = (ulong)Math.Max(1, Environment.TickCount64);
        var memory = Mt5BridgeMappedMemory.CreateOrOpen(roomId);
        try
        {
            var ring = new Mt5BridgeSharedMemoryRing(memory, uid, generation);
            return new Mt5BridgeSharedMemoryTransport(ring, new Mt5BridgeHealthMonitor(staleAfter));
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }

    internal static Mt5BridgeSharedMemoryTransport ConnectForTest(
        IMt5BridgeMemory memory,
        uint writerUid,
        ulong generation,
        TimeSpan? staleAfter = null)
    {
        return new Mt5BridgeSharedMemoryTransport(
            new Mt5BridgeSharedMemoryRing(memory, writerUid, generation),
            new Mt5BridgeHealthMonitor(staleAfter));
    }

    public async Task<Mt5BridgeInboundMessage> SendAsync(
        Mt5BridgeMessage message,
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var waiter = _pump.Register(requestId);
        try
        {
            var json = Mt5BridgeProtocol.Serialize(message);
            if (!_ring.TryWrite(json))
            {
                throw new InvalidOperationException("MT5 Bridge command exceeds the shared-memory payload limit.");
            }

            return await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pump.Remove(requestId);
        }
    }

    public Task<Mt5BridgeInboundMessage> PingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        return SendAsync(
            new Mt5BridgePing { RequestId = requestId },
            requestId,
            timeout,
            cancellationToken);
    }

    private static uint CreateWriterUid(string identity)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var character in identity)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash == 0 ? 1 : hash;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _pump.DisposeAsync().ConfigureAwait(false);
        _ring.Dispose();
    }
}
