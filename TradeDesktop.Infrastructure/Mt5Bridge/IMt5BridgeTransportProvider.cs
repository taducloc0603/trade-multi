namespace TradeDesktop.Infrastructure.Mt5Bridge;

public interface IMt5BridgeTransportProvider : IAsyncDisposable
{
    IMt5BridgeTransport Resolve(string exchange);
}
