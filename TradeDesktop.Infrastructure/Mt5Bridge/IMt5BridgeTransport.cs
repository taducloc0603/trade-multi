namespace TradeDesktop.Infrastructure.Mt5Bridge;

public interface IMt5BridgeTransport : IAsyncDisposable
{
    Mt5BridgeHealth Health { get; }

    Task<Mt5BridgeInboundMessage> SendAsync(
        Mt5BridgeMessage message,
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Action<Mt5BridgeInboundMessage>? onProgress = null);

    Task<Mt5BridgeInboundMessage> PingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
