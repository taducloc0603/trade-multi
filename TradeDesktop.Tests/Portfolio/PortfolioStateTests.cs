using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class PortfolioStateTests
{
    private static readonly CloseSignalEngineFactory Factory = new();

    [Fact]
    public void AllocateNewSlot_IncreasesSlotId()
    {
        var state = new PortfolioState();

        var first = state.AllocateNewSlot("AUTO-0001-1", Factory);
        var second = state.AllocateNewSlot("AUTO-0002-2", Factory);

        Assert.Equal(1, first.SlotId);
        Assert.Equal(2, second.SlotId);
        Assert.Equal(2, state.Slots.Count);
    }

    [Fact]
    public void AllocateNewSlot_AssignsOwnCloseSignalEngine()
    {
        var state = new PortfolioState();

        var first = state.AllocateNewSlot("p1", Factory);
        var second = state.AllocateNewSlot("p2", Factory);

        Assert.NotSame(first.CloseSignalEngine, second.CloseSignalEngine);
    }

    [Fact]
    public void CountLiveAndPendingBuy_CountsPendingAndLive_NotClosed()
    {
        var state = new PortfolioState();
        var s1 = state.AllocateNewSlot("p1", Factory);
        s1.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 5);

        var s2 = state.AllocateNewSlot("p2", Factory);
        s2.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 5);
        s2.MarkOpenConfirmed(1, 2, DateTime.UtcNow);

        var s3 = state.AllocateNewSlot("p3", Factory);
        s3.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 5);
        s3.MarkOpenConfirmed(3, 4, DateTime.UtcNow);
        s3.MarkCloseTriggered(DateTime.UtcNow);
        s3.MarkCloseConfirmed(DateTime.UtcNow);

        var sell = state.AllocateNewSlot("p4", Factory);
        sell.MarkOpenTriggered(TradingPositionSide.Sell, TradingOpenMode.GapSell, DateTime.UtcNow, 5);

        Assert.Equal(2, state.CountLiveAndPendingBuy());
    }

    [Fact]
    public void CountLiveAndPendingTotal_SumsBuyAndSell()
    {
        var state = new PortfolioState();
        var s1 = state.AllocateNewSlot("p1", Factory);
        s1.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 5);
        var s2 = state.AllocateNewSlot("p2", Factory);
        s2.MarkOpenTriggered(TradingPositionSide.Sell, TradingOpenMode.GapSell, DateTime.UtcNow, 5);

        Assert.Equal(2, state.CountLiveAndPendingTotal());
    }

    [Fact]
    public void GetSlotByPairId_ReturnsCorrectSlot()
    {
        var state = new PortfolioState();
        state.AllocateNewSlot("p1", Factory);
        var target = state.AllocateNewSlot("p2", Factory);
        state.AllocateNewSlot("p3", Factory);

        Assert.Same(target, state.GetSlotByPairId("p2"));
        Assert.Null(state.GetSlotByPairId("missing"));
    }

    [Fact]
    public void GetSlotByTicket_ReturnsSlotWhenTicketAMatches()
    {
        var state = new PortfolioState();
        var slot = state.AllocateNewSlot("p1", Factory);
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 5);
        slot.MarkOpenConfirmed(ticketA: 1234, ticketB: 5678, DateTime.UtcNow);

        Assert.Same(slot, state.GetSlotByTicket(1234));
    }

    [Fact]
    public void GetSlotByTicket_ReturnsSlotWhenTicketBMatches()
    {
        var state = new PortfolioState();
        var slot = state.AllocateNewSlot("p1", Factory);
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 5);
        slot.MarkOpenConfirmed(ticketA: 1234, ticketB: 5678, DateTime.UtcNow);

        Assert.Same(slot, state.GetSlotByTicket(5678));
    }

    [Fact]
    public void GetSlotByTicket_IgnoresClosedSlots()
    {
        var state = new PortfolioState();
        var slot = state.AllocateNewSlot("p1", Factory);
        slot.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 5);
        slot.MarkOpenConfirmed(7777, 8888, DateTime.UtcNow);
        slot.MarkCloseTriggered(DateTime.UtcNow);
        slot.MarkCloseConfirmed(DateTime.UtcNow);

        Assert.Null(state.GetSlotByTicket(7777));
    }

    [Fact]
    public void RemoveClosed_KeepsOnlyNonClosedSlots()
    {
        var state = new PortfolioState();
        var live = state.AllocateNewSlot("p-live", Factory);
        live.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 5);
        live.MarkOpenConfirmed(1, 2, DateTime.UtcNow);

        var closed = state.AllocateNewSlot("p-closed", Factory);
        closed.MarkOpenTriggered(TradingPositionSide.Sell, TradingOpenMode.GapSell, DateTime.UtcNow, 5);
        closed.MarkOpenConfirmed(3, 4, DateTime.UtcNow);
        closed.MarkCloseTriggered(DateTime.UtcNow);
        closed.MarkCloseConfirmed(DateTime.UtcNow);

        state.RemoveClosed();

        Assert.Single(state.Slots);
        Assert.Same(live, state.Slots[0]);
    }
}
