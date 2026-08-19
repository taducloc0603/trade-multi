namespace TradeDesktop.Infrastructure.Mt5Bridge;

public sealed class Mt5BridgeTransportProvider : IMt5BridgeTransportProvider
{
    private readonly Mt5BridgeOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<string, IMt5BridgeTransport> _transports =
        new(StringComparer.OrdinalIgnoreCase);

    public Mt5BridgeTransportProvider(Mt5BridgeOptions options)
    {
        _options = options;
    }

    public IMt5BridgeTransport Resolve(string exchange)
    {
        var key = exchange.Trim().ToUpperInvariant();
        if (key is not ("A" or "B"))
        {
            throw new ArgumentOutOfRangeException(nameof(exchange), exchange, "Exchange must be A or B.");
        }
        lock (_sync)
        {
            if (_transports.TryGetValue(key, out var transport))
            {
                return transport;
            }

            var endpoint = _options.ResolveEndpoint(key);
            if (string.IsNullOrWhiteSpace(endpoint.RoomId))
            {
                throw new InvalidOperationException($"MT5 Bridge RoomId is not configured for exchange {key}.");
            }

            transport = Mt5BridgeSharedMemoryTransport.Connect(
                endpoint.RoomId,
                staleAfter: _options.HeartbeatTimeout);
            _transports.Add(key, transport);
            return transport;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IMt5BridgeTransport[] transports;
        lock (_sync)
        {
            transports = _transports.Values.Distinct().ToArray();
            _transports.Clear();
        }

        foreach (var transport in transports)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
