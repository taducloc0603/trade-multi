using TradeDesktop.Infrastructure.Mt5Bridge;

namespace TradeDesktop.Tests.Mt5Bridge;

public sealed class Mt5BridgeCloseProtocolTests
{
    [Fact]
    public void CloseCommand_RoundTripsTicketAndPartialVolume()
    {
        var source = new Mt5BridgeCloseCommand
        {
            RequestId = "close-request-1",
            Account = 12345678,
            Ticket = 987654321,
            Symbol = "XAUUSD.a",
            Volume = 0.15,
            CreatedMilliseconds = 1_000,
            ExpiresMilliseconds = 2_500
        };

        var json = Mt5BridgeProtocol.Serialize(source);
        var parsed = Mt5BridgeProtocol.Deserialize<Mt5BridgeCloseCommand>(json);

        Assert.NotNull(parsed);
        Assert.Equal(source, parsed);
        Assert.Contains("\"ticket\":987654321", json);
        Assert.Contains("\"volume\":0.15", json);
    }

    [Fact]
    public void CloseCommand_AllowsZeroVolumeToRepresentFullClose()
    {
        var source = new Mt5BridgeCloseCommand
        {
            RequestId = "close-full-1",
            Account = 12345678,
            Ticket = 42,
            Symbol = "EURUSD",
            Volume = 0,
            CreatedMilliseconds = 1_000,
            ExpiresMilliseconds = 2_500
        };

        var json = Mt5BridgeProtocol.Serialize(source);

        Assert.Contains("\"volume\":0", json);
    }

    [Fact]
    public void AlreadyClosed_IsAnIdempotentFinalResult()
    {
        var json =
            "{\"v\":1,\"type\":\"execution_result\",\"request_id\":\"close-1\",\"status\":\"already_closed\",\"ticket\":42}";

        var result = Mt5BridgeProtocol.DeserializeInbound(json);

        Assert.True(Mt5BridgeExecutionStatuses.IsFinal(result.Status));
        Assert.Equal(42UL, result.Ticket);
    }
}
