using System.Globalization;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Application.Services.Portfolio;
using TradeDesktop.Application.Services.PrimeXbt;
using TradeDesktop.Application.Services.TradeLocker;

namespace TradeDesktop.Tests.TradeLocker;

// Phase 1: ticket TradeLocker = bit 63 | position id. Phải loại trừ cTrader (bit 62), PrimeXBT (bit 61), ticket MT,
// và sống sót qua mọi đường lưu trữ ticket (current_slots JSON, current_tick_* dạng chuỗi).
public sealed class TradeLockerTicketCodecTests
{
    // Position id thật quan sát trên OPEN_DEMO 2026-10-26 (Phase 0).
    private const long RealDemoPositionId = 902413599387337493L;

    [Theory]
    [InlineData(1L)]
    [InlineData(RealDemoPositionId)]
    [InlineData(long.MaxValue)]
    public void EncodeDecode_RoundTrips(long positionId)
    {
        var ticket = TradeLockerTicketCodec.Encode(positionId);

        Assert.True((ticket & 0x8000_0000_0000_0000UL) != 0);
        Assert.True(TradeLockerTicketCodec.TryDecode(ticket, out var decoded));
        Assert.Equal(positionId, decoded);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Encode_NonPositiveId_Throws(long positionId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TradeLockerTicketCodec.Encode(positionId));
    }

    [Theory]
    [InlineData("902413599387337493", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData(" 5", false)]
    [InlineData("12a", false)]
    [InlineData("99999999999999999999", false)] // vượt long
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryEncode_WireString_AcceptsOnlyPositiveDecimalIds(string? wire, bool expected)
    {
        Assert.Equal(expected, TradeLockerTicketCodec.TryEncode(wire, out var ticket));
        if (expected)
        {
            Assert.True(TradeLockerTicketCodec.TryDecodeToWireId(ticket, out var back));
            Assert.Equal(wire, back);
        }
        else
        {
            Assert.Equal(0UL, ticket);
        }
    }

    [Fact]
    public void TradeLockerTicket_IsNotDecodedByCTraderOrPrimeXbt()
    {
        var ticket = TradeLockerTicketCodec.Encode(RealDemoPositionId);

        Assert.False(CTraderTicketCodec.TryDecode(ticket, out _));
        Assert.False(CTraderTicketCodec.IsCTraderTicket(ticket));
        Assert.False(PrimeXbtTicketCodec.TryDecode(ticket, out _));
        Assert.False(PrimeXbtTicketCodec.IsPrimeXbtTicket(ticket));
    }

    [Fact]
    public void OtherPlatformTickets_AreNotDecodedByTradeLocker()
    {
        Assert.False(TradeLockerTicketCodec.IsTradeLockerTicket(CTraderTicketCodec.Encode(123_456_789)));
        Assert.False(TradeLockerTicketCodec.IsTradeLockerTicket(PrimeXbtTicketCodec.Encode(12_345_678)));
        Assert.False(TradeLockerTicketCodec.IsTradeLockerTicket(77_460_990UL)); // ticket MT5 demo
        Assert.False(TradeLockerTicketCodec.IsTradeLockerTicket(0UL));
        Assert.False(TradeLockerTicketCodec.IsTradeLockerTicket(0x8000_0000_0000_0000UL)); // namespace, id 0
    }

    [Fact]
    public void Ticket_SurvivesCurrentSlotsJsonRoundTrip()
    {
        var ticketB = TradeLockerTicketCodec.Encode(RealDemoPositionId);
        var slot = new PositionSlot(1, "AUTO-0001-TL", new CloseSignalEngine());
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 30);
        slot.MarkOpenConfirmed(77_460_990UL, ticketB, DateTime.UtcNow);

        var recovered = Assert.Single(SlotPersistence.Deserialize(SlotPersistence.Serialize(new[] { slot })));

        Assert.Equal(ticketB, recovered.TicketB);
        Assert.True(TradeLockerTicketCodec.TryDecode(recovered.TicketB, out var id));
        Assert.Equal(RealDemoPositionId, id);
    }

    [Fact]
    public void Ticket_SurvivesCurrentTickStringRoundTrip()
    {
        // current_tick_b lưu dạng chuỗi và được đọc lại bằng ulong.TryParse (DashboardViewModel).
        var ticketB = TradeLockerTicketCodec.Encode(RealDemoPositionId);
        var stored = ticketB.ToString(CultureInfo.InvariantCulture);

        Assert.True(ulong.TryParse(stored, out var parsed));
        Assert.Equal(ticketB, parsed);
        Assert.False(long.TryParse(stored, out _)); // nhắc: đọc bằng long sẽ hỏng — luôn dùng ulong
    }
}
