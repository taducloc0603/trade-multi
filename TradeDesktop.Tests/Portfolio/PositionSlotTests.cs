using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class PositionSlotTests
{
    private static PositionSlot CreateSlot(int slotId = 1, string pairId = "AUTO-0001-1")
        => new(slotId, pairId, new CloseSignalEngine());

    [Fact]
    public void Constructor_SetsSlotIdAndPairId()
    {
        var slot = new PositionSlot(7, "AUTO-0007-12345", new CloseSignalEngine());

        Assert.Equal(7, slot.SlotId);
        Assert.Equal("AUTO-0007-12345", slot.PairId);
        Assert.Equal(PositionSlotStatus.PendingOpen, slot.Status);
        Assert.Equal(TradingPositionSide.None, slot.Side);
        Assert.Equal(TradingOpenMode.None, slot.OpenMode);
        Assert.Null(slot.TicketA);
        Assert.Null(slot.TicketB);
        Assert.NotNull(slot.CloseSignalEngine);
    }

    [Fact]
    public void MarkOpenTriggered_SetsStatusToPendingOpen()
    {
        var slot = CreateSlot();
        var ts = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, ts, holdingSeconds: 5);

        Assert.Equal(PositionSlotStatus.PendingOpen, slot.Status);
        Assert.Equal(TradingPositionSide.Buy, slot.Side);
        Assert.Equal(TradingOpenMode.GapBuy, slot.OpenMode);
        Assert.Equal(ts, slot.OpenedAtUtc);
        Assert.Equal(5, slot.HoldingSeconds);
    }

    [Fact]
    public void MarkOpenConfirmed_SetsStatusToLive_AndTickets()
    {
        var slot = CreateSlot();
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 3);
        var confirmed = new DateTime(2026, 5, 21, 10, 0, 1, DateTimeKind.Utc);

        slot.MarkOpenConfirmed(ticketA: 1001, ticketB: 1002, confirmed);

        Assert.Equal(PositionSlotStatus.Live, slot.Status);
        Assert.Equal((ulong)1001, slot.TicketA);
        Assert.Equal((ulong)1002, slot.TicketB);
        Assert.Equal(confirmed, slot.OpenConfirmedAtUtc);
    }

    [Fact]
    public void MarkCloseTriggered_SetsIsCloseExecutionPending()
    {
        var slot = CreateSlot();
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 3);
        slot.MarkOpenConfirmed(1, 2, DateTime.UtcNow);
        var ts = DateTime.UtcNow;

        slot.MarkCloseTriggered(ts);

        Assert.True(slot.IsCloseExecutionPending);
        Assert.Equal(PositionSlotStatus.PendingClose, slot.Status);
        Assert.Equal(ts, slot.ClosedAtUtc);
    }

    [Fact]
    public void MarkCloseConfirmed_SetsStatusToClosed()
    {
        var slot = CreateSlot();
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 3);
        slot.MarkOpenConfirmed(1, 2, DateTime.UtcNow);
        slot.MarkCloseTriggered(DateTime.UtcNow);
        var confirmed = DateTime.UtcNow.AddSeconds(1);

        slot.MarkCloseConfirmed(confirmed);

        Assert.Equal(PositionSlotStatus.Closed, slot.Status);
        Assert.False(slot.IsCloseExecutionPending);
        Assert.Equal(confirmed, slot.CloseConfirmedAtUtc);
    }

    [Fact]
    public void IsHoldingTimeElapsed_FalseBeforeHolding()
    {
        var slot = CreateSlot();
        var openedAt = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, openedAt, holdingSeconds: 5);
        slot.MarkOpenConfirmed(1, 2, openedAt);

        Assert.False(slot.IsHoldingTimeElapsed(openedAt.AddSeconds(3)));
    }

    [Fact]
    public void IsHoldingTimeElapsed_TrueAfterHolding()
    {
        var slot = CreateSlot();
        var openedAt = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, openedAt, holdingSeconds: 5);
        slot.MarkOpenConfirmed(1, 2, openedAt);

        Assert.True(slot.IsHoldingTimeElapsed(openedAt.AddSeconds(5)));
        Assert.True(slot.IsHoldingTimeElapsed(openedAt.AddSeconds(6)));
    }

    [Fact]
    public void IsHoldingTimeElapsed_FalseWhenOpenConfirmedAtUtcNull()
    {
        var slot = CreateSlot();
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, holdingSeconds: 5);

        Assert.False(slot.IsHoldingTimeElapsed(DateTime.UtcNow.AddSeconds(100)));
    }

    [Fact]
    public void MarkSynced_SetsLiveStateDirectly()
    {
        var slot = CreateSlot();
        var confirmedAt = new DateTime(2026, 5, 21, 14, 30, 0, DateTimeKind.Utc);

        slot.MarkSynced(
            side: TradingPositionSide.Sell,
            mode: TradingOpenMode.GapSell,
            ticketA: 555,
            ticketB: 666,
            openConfirmedAtUtc: confirmedAt,
            holdingSeconds: 12);

        Assert.Equal(PositionSlotStatus.Live, slot.Status);
        Assert.Equal(TradingPositionSide.Sell, slot.Side);
        Assert.Equal((ulong)555, slot.TicketA);
        Assert.Equal((ulong)666, slot.TicketB);
        Assert.Equal(confirmedAt, slot.OpenConfirmedAtUtc);
        Assert.Equal(12, slot.HoldingSeconds);
    }
}
