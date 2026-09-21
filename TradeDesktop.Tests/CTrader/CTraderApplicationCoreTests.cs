using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Tests.CTrader;

public sealed class CTraderApplicationCoreTests
{
    public static IEnumerable<object[]> AllFlagCombinations()
    {
        for (var mask = 0; mask < 32; mask++)
        {
            yield return [(mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0, (mask & 16) != 0];
        }
    }

    // R2 bảng sự thật đầy đủ: 16 tổ hợp của 4 cờ map × 2 giá trị HasTopOfBook.
    [Theory]
    [MemberData(nameof(AllFlagCombinations))]
    public void SessionHealth_TruthTable(bool quote, bool trade, bool symbol, bool synced, bool top)
    {
        var health = new CTraderSessionHealth(quote, trade, symbol, synced, top, 0);

        Assert.Equal(quote && symbol && top, health.IsQuoteConnected);
        Assert.Equal(trade && symbol && synced, health.IsMapAvailable);
        Assert.Equal(quote && trade ? 1 : 0, health.ConnectedFlag);
    }

    [Fact]
    public void SessionHealth_NotSyncedYet_MapNeverAvailable()
    {
        Assert.False(new CTraderSessionHealth(true, true, true, false, true, 0).IsMapAvailable);
    }

    [Fact]
    public void SessionHealth_Disconnected_IsFailClosedAndNeverMinusOne()
    {
        var health = CTraderSessionHealth.Disconnected;

        Assert.False(health.IsQuoteConnected);
        Assert.False(health.IsMapAvailable);
        Assert.Equal(0, health.ConnectedFlag);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(123456789L)]
    [InlineData(long.MaxValue >> 2)]
    public void TicketCodec_RoundTrip(long positionId)
    {
        var ticket = CTraderTicketCodec.Encode(positionId);

        Assert.True(CTraderTicketCodec.IsCTraderTicket(ticket));
        Assert.True(CTraderTicketCodec.TryDecode(ticket, out var decoded));
        Assert.Equal(positionId, decoded);
    }

    [Theory]
    [InlineData(12345678UL)]
    [InlineData(9_999_999_999UL)]
    [InlineData(0UL)]
    [InlineData(0x3FFF_FFFF_FFFF_FFFFUL)]
    public void TicketCodec_MtTicketsNeverDecode(ulong mtTicket)
    {
        Assert.False(CTraderTicketCodec.TryDecode(mtTicket, out _));
        Assert.False(CTraderTicketCodec.IsCTraderTicket(mtTicket));
    }

    [Fact]
    public void TicketCodec_AnyValidPositionIdIsOutsideMtRange()
    {
        Assert.True(CTraderTicketCodec.Encode(0) > 0x3FFF_FFFF_FFFF_FFFFUL);
        Assert.True(CTraderTicketCodec.Encode(CTraderTicketCodec.MaxPositionId) < 0x8000_0000_0000_0000UL);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0x4000_0000_0000_0000L)]
    public void TicketCodec_OutOfRange_Throws(long positionId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CTraderTicketCodec.Encode(positionId));
    }

    [Fact]
    public void TicketCodec_HighBitSetTicket_IsNotCTrader()
    {
        Assert.False(CTraderTicketCodec.TryDecode(0xC000_0000_0000_0001UL, out _));
    }

    [Theory]
    [InlineData("8=FIX.4.4|35=A|553=8220816|554=Secret$$|10=092|", "8=FIX.4.4|35=A|553=8220816|554=***|10=092|")]
    [InlineData("8=FIX.4.4\u000135=5\u0001554=Secret$$\u000110=1\u0001", "8=FIX.4.4\u000135=5\u0001554=***\u000110=1\u0001")]
    [InlineData("{553: \"8220816\"},\n{554: \"Secret$$\"},", "{553: \"8220816\"},\n{554: \"***\"},")]
    [InlineData("554=Secret|10=1", "554=***|10=1")]
    public void LogMasker_MasksPasswordInEveryForm(string input, string expected)
    {
        var masked = CTraderFixLogMasker.Apply(input);

        Assert.Equal(expected, masked);
        Assert.DoesNotContain("Secret", masked);
    }

    [Theory]
    [InlineData("8=FIX.4.4|35=W|270=4350.77|10=042|")]
    [InlineData("55=41|1554=x|2554=y")]
    [InlineData("")]
    public void LogMasker_LeavesOtherTagsUntouched(string input)
    {
        Assert.Equal(input, CTraderFixLogMasker.Apply(input));
    }

    [Fact]
    public void LogMasker_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CTraderFixLogMasker.Apply(null));
    }
}
