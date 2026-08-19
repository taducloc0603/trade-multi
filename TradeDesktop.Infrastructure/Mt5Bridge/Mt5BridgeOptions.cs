namespace TradeDesktop.Infrastructure.Mt5Bridge;

public sealed record Mt5BridgeOptions
{
    public string RoomId { get; init; } = "OCTBridge";
    public long Account { get; init; }
    public string SymbolA { get; init; } = string.Empty;
    public string SymbolB { get; init; } = string.Empty;
    public double VolumeA { get; init; }
    public double VolumeB { get; init; }
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public string ResolveSymbol(string exchange) =>
        string.Equals(exchange, "A", StringComparison.OrdinalIgnoreCase) ? SymbolA : SymbolB;

    public double ResolveVolume(string exchange) =>
        string.Equals(exchange, "A", StringComparison.OrdinalIgnoreCase) ? VolumeA : VolumeB;
}
