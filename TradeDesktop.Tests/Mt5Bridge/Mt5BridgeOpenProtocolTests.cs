using TradeDesktop.Infrastructure.Mt5Bridge;

namespace TradeDesktop.Tests.Mt5Bridge;

public sealed class Mt5BridgeOpenProtocolTests
{
    [Theory]
    [InlineData("confirmed", true)]
    [InlineData("rejected", true)]
    [InlineData("invalid", true)]
    [InlineData("expired", true)]
    [InlineData("timeout", true)]
    [InlineData("unknown", true)]
    [InlineData("received", false)]
    [InlineData("dispatched", false)]
    [InlineData("duplicate", false)]
    public void ExecutionStatus_HasExpectedFinalSemantics(string status, bool expected)
    {
        Assert.Equal(expected, Mt5BridgeExecutionStatuses.IsFinal(status));
    }

    [Fact]
    public void OpenCommand_RoundTripsAllCorrelationFields()
    {
        var source = new Mt5BridgeOpenCommand
        {
            RequestId = "open-request-1",
            Account = 12345678,
            Symbol = "XAUUSD.a",
            Side = "SELL",
            Volume = 0.25,
            CreatedMilliseconds = 1_000,
            ExpiresMilliseconds = 2_500,
            PairId = "pair-10",
            Leg = "B"
        };

        var json = Mt5BridgeProtocol.Serialize(source);
        var parsed = Mt5BridgeProtocol.Deserialize<Mt5BridgeOpenCommand>(json);

        Assert.NotNull(parsed);
        Assert.Equal(source, parsed);
    }
}
