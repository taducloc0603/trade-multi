using System.Text;
using TradeDesktop.Infrastructure.Mt5Bridge;

namespace TradeDesktop.Tests.Mt5Bridge;

public sealed class Mt5BridgeLayoutTests
{
    [Fact]
    public void LayoutV4_HasExpectedOffsetsAndSizes()
    {
        Assert.Equal(4u, Mt5BridgeLayout.Version);
        Assert.Equal(1000, Mt5BridgeLayout.MaxPayloadSize);
        Assert.Equal(320, Mt5BridgeLayout.LanesOffset);
        Assert.Equal(262_144, Mt5BridgeLayout.LaneSize);
        Assert.Equal(2_097_472, Mt5BridgeLayout.RegionSize);
        Assert.Equal(1016, Mt5BridgeLayout.SlotSequenceEndOffset);
    }

    [Fact]
    public void SlotOffset_WrapsAtCapacity()
    {
        var first = Mt5BridgeLayout.GetSlotOffset(3, 0);
        var wrapped = Mt5BridgeLayout.GetSlotOffset(3, 256);

        Assert.Equal(first, wrapped);
        Assert.Equal(first + Mt5BridgeLayout.SlotSize, Mt5BridgeLayout.GetSlotOffset(3, 1));
    }

    [Fact]
    public void RegionName_NormalizesRoomId()
    {
        Assert.Equal("Local\\CopyTradeRoom_12345", Mt5BridgeLayout.GetRegionName(" 12345 "));
    }

    [Fact]
    public void OpenCommand_SerializesWithinSlotUsingContractNames()
    {
        var command = new Mt5BridgeOpenCommand
        {
            RequestId = "request-1",
            Account = 12345,
            Symbol = "XAUUSD",
            Side = "BUY",
            Volume = 0.1,
            CreatedMilliseconds = 100,
            ExpiresMilliseconds = 1_600,
            PairId = "pair-1",
            Leg = "A"
        };

        var json = Mt5BridgeProtocol.Serialize(command);

        Assert.Contains("\"request_id\":\"request-1\"", json);
        Assert.Contains("\"created_ms\":100", json);
        Assert.DoesNotContain("RequestId", json);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= Mt5BridgeLayout.MaxPayloadSize);
    }

    [Fact]
    public void Serialize_RejectsMessageLargerThanSlotPayload()
    {
        var result = new Mt5BridgeExecutionResult
        {
            RequestId = "request-1",
            Status = Mt5BridgeExecutionStatuses.Rejected,
            Detail = new string('x', Mt5BridgeLayout.MaxPayloadSize)
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Mt5BridgeProtocol.Serialize(result));

        Assert.Contains("maximum is 1000", exception.Message);
    }
}
