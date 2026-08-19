namespace TradeDesktop.Infrastructure.Mt5Bridge;

public sealed record Mt5BridgeOptions
{
    public Mt5BridgeEndpointOptions ExchangeA { get; init; } = new() { RoomId = "OCTBridge_A" };
    public Mt5BridgeEndpointOptions ExchangeB { get; init; } = new() { RoomId = "OCTBridge_B" };
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public Mt5BridgeEndpointOptions ResolveEndpoint(string exchange) => exchange.Trim().ToUpperInvariant() switch
    {
        "A" => ExchangeA,
        "B" => ExchangeB,
        _ => throw new ArgumentOutOfRangeException(nameof(exchange), exchange, "Exchange must be A or B.")
    };
}

public sealed record Mt5BridgeEndpointOptions
{
    public string RoomId { get; init; } = string.Empty;
    public long Account { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public double Volume { get; init; }
}
