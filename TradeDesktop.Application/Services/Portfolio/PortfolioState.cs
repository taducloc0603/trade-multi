using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Container quản lý collection slots + global counters cho portfolio coordinator (Phase 0 §0.1).
/// </summary>
public sealed class PortfolioState
{
    private readonly List<PositionSlot> _slots = new();
    private int _nextSlotId = 0;

    public IReadOnlyList<PositionSlot> Slots => _slots;

    // Phase 0 default: cap=1. Phase 2 raise to 7/4/4 via UpdateQuotaConfig from DB.
    public int MaxTotalOpens { get; set; } = 1;
    public int MaxBuyOpens { get; set; } = 1;
    public int MaxSellOpens { get; set; } = 1;
    public DateTime? GlobalActionLockUntilUtc { get; set; }
    public int GlobalCooldownMinSec { get; set; } = 0;
    public int GlobalCooldownMaxSec { get; set; } = 0;
    public DateTime? LastOpenConfirmedAtUtc { get; set; }
    public TradingPositionSide LastOpenConfirmedSide { get; set; } = TradingPositionSide.None;

    public int CountLiveAndPendingBuy()
        => _slots.Count(s => s.Side == TradingPositionSide.Buy && IsLiveOrPending(s.Status));

    public int CountLiveAndPendingSell()
        => _slots.Count(s => s.Side == TradingPositionSide.Sell && IsLiveOrPending(s.Status));

    public int CountLiveAndPendingTotal()
        => _slots.Count(s => IsLiveOrPending(s.Status));

    public IEnumerable<PositionSlot> GetLiveSlots()
        => _slots.Where(s => s.Status == PositionSlotStatus.Live);

    public PositionSlot? GetSlotByPairId(string pairId)
        => _slots.FirstOrDefault(s => string.Equals(s.PairId, pairId, StringComparison.Ordinal));

    public PositionSlot? GetSlotByTicket(ulong ticket)
    {
        foreach (var slot in _slots)
        {
            if (slot.Status == PositionSlotStatus.Closed) continue;
            if (slot.TicketA == ticket || slot.TicketB == ticket) return slot;
        }
        return null;
    }

    public PositionSlot AllocateNewSlot(string pairId, ICloseSignalEngineFactory closeEngineFactory)
    {
        _nextSlotId++;
        var slot = new PositionSlot(_nextSlotId, pairId, closeEngineFactory.Create());
        _slots.Add(slot);
        return slot;
    }

    public void RemoveClosed()
    {
        _slots.RemoveAll(s => s.Status == PositionSlotStatus.Closed);
    }

    public void RemoveSlot(PositionSlot slot)
    {
        _slots.Remove(slot);
    }

    public void SetNextSlotId(int nextId)
    {
        _nextSlotId = Math.Max(_nextSlotId, nextId);
    }

    /// <summary>
    /// Phase 5: add a fully-formed PositionSlot (with restored state) to the internal list
    /// WITHOUT bumping _nextSlotId — caller responsibility to call SetNextSlotId after batch.
    /// </summary>
    public void AddRecoveredSlot(PositionSlot slot)
    {
        _slots.Add(slot);
    }

    public void Clear()
    {
        _slots.Clear();
        GlobalActionLockUntilUtc = null;
        LastOpenConfirmedAtUtc = null;
        LastOpenConfirmedSide = TradingPositionSide.None;
    }

    private static bool IsLiveOrPending(PositionSlotStatus status)
        => status == PositionSlotStatus.PendingOpen
            || status == PositionSlotStatus.Live
            || status == PositionSlotStatus.PendingClose;
}
