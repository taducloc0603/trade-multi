using TradeDesktop.Infrastructure.Mt5Bridge;

namespace TradeDesktop.Tests.Mt5Bridge;

public sealed class Mt5BridgeOptionsTests
{
    [Fact]
    public void ResolveEndpoint_ReturnsIndependentExchangeConfiguration()
    {
        var options = new Mt5BridgeOptions
        {
            ExchangeA = new Mt5BridgeEndpointOptions { RoomId = "OCTBridge_A", Account = 11 },
            ExchangeB = new Mt5BridgeEndpointOptions { RoomId = "OCTBridge_B", Account = 22 }
        };

        Assert.Equal("OCTBridge_A", options.ResolveEndpoint("A").RoomId);
        Assert.Equal(11, options.ResolveEndpoint("a").Account);
        Assert.Equal("OCTBridge_B", options.ResolveEndpoint("B").RoomId);
        Assert.Equal(22, options.ResolveEndpoint("b").Account);
    }

    [Fact]
    public void ResolveEndpoint_RejectsUnknownExchange()
    {
        var options = new Mt5BridgeOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.ResolveEndpoint("C"));
    }
}
