using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Một slot độc lập đại diện 1 lệnh trong portfolio. Mỗi slot có CloseSignalEngine riêng
/// để window state close tick-by-tick không bị share giữa các slot khác (Phase 0 §0.1).
/// </summary>
public sealed class PositionSlot
{
    public PositionSlot(int slotId, string pairId, ICloseSignalEngine closeSignalEngine)
    {
        SlotId = slotId;
        PairId = pairId;
        Status = PositionSlotStatus.PendingOpen;
        CloseSignalEngine = closeSignalEngine;
    }

    public int SlotId { get; }
    public string PairId { get; }
    public TradingPositionSide Side { get; private set; } = TradingPositionSide.None;
    public TradingOpenMode OpenMode { get; private set; } = TradingOpenMode.None;
    public PositionSlotStatus Status { get; internal set; }
    public ulong? TicketA { get; private set; }
    public ulong? TicketB { get; private set; }
    public DateTime? OpenedAtUtc { get; private set; }
    public DateTime? OpenConfirmedAtUtc { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public DateTime? CloseConfirmedAtUtc { get; private set; }
    public int HoldingSeconds { get; private set; }
    public bool IsCloseExecutionPending { get; private set; }
    public double? LastProfitSnapshot { get; internal set; }
    public ICloseSignalEngine CloseSignalEngine { get; private set; }

    public void MarkOpenTriggered(
        TradingPositionSide side,
        TradingOpenMode mode,
        DateTime triggerAtUtc,
        int holdingSeconds)
    {
        Side = side;
        OpenMode = mode;
        OpenedAtUtc = triggerAtUtc;
        HoldingSeconds = holdingSeconds;
        Status = PositionSlotStatus.PendingOpen;
    }

    public void MarkOpenConfirmed(ulong ticketA, ulong ticketB, DateTime confirmedAtUtc)
    {
        TicketA = ticketA;
        TicketB = ticketB;
        OpenConfirmedAtUtc = confirmedAtUtc;
        Status = PositionSlotStatus.Live;
    }

    public void MarkCloseTriggered(DateTime triggerAtUtc)
    {
        ClosedAtUtc = triggerAtUtc;
        IsCloseExecutionPending = true;
        Status = PositionSlotStatus.PendingClose;
    }

    public void MarkCloseConfirmed(DateTime closedAtUtc)
    {
        CloseConfirmedAtUtc = closedAtUtc;
        IsCloseExecutionPending = false;
        Status = PositionSlotStatus.Closed;
    }

    public void ClearCloseExecutionPending()
    {
        IsCloseExecutionPending = false;
        ClosedAtUtc = null;
    }

    public bool IsHoldingTimeElapsed(DateTime nowUtc)
    {
        if (!OpenConfirmedAtUtc.HasValue || HoldingSeconds <= 0)
        {
            return false;
        }

        return nowUtc - OpenConfirmedAtUtc.Value >= TimeSpan.FromSeconds(HoldingSeconds);
    }

    /// <summary>
    /// Force slot vào trạng thái Live (sync recovery hoặc manual). Dùng cho path
    /// app restart, manual buttons, hoặc Phase 5 RecoverSlotsFromPersisted.
    /// </summary>
    public void MarkSynced(
        TradingPositionSide side,
        TradingOpenMode mode,
        ulong? ticketA,
        ulong? ticketB,
        DateTime openConfirmedAtUtc,
        int holdingSeconds)
    {
        Side = side;
        OpenMode = mode;
        TicketA = ticketA;
        TicketB = ticketB;
        OpenedAtUtc = openConfirmedAtUtc;
        OpenConfirmedAtUtc = openConfirmedAtUtc;
        HoldingSeconds = holdingSeconds;
        Status = PositionSlotStatus.Live;
        IsCloseExecutionPending = false;
    }

    /// <summary>
    /// Replace CloseSignalEngine instance (dùng cho Phase 5 recovery — fresh engine, không recover window state).
    /// </summary>
    public void ResetCloseSignalEngine(ICloseSignalEngine engine)
    {
        CloseSignalEngine = engine;
    }
}
