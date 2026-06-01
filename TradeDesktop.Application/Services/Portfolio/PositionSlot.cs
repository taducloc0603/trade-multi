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
    public double? LastProfitA { get; private set; }
    public double? LastProfitB { get; private set; }
    public bool HasCompleteProfitSnapshot => LastProfitA.HasValue && LastProfitB.HasValue;

    /// <summary>
    /// P/L thật từ broker (HistorySharedRecord.Profit) khi lệnh đã đóng — tổng 2 leg, gồm
    /// commission/swap. KHÁC với <see cref="LastProfitSnapshot"/> (mark-to-market theo giá quote
    /// lúc trigger). Chỉ dùng để log/hiển thị net P/L thật, KHÔNG tham gia quyết định TP/Rule D.
    /// </summary>
    public double? RealizedCloseProfit { get; private set; }
    private double? _realizedCloseProfitA;
    private double? _realizedCloseProfitB;
    public CloseSignalReason? LastCloseReason { get; private set; }
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
        ClearProfitSnapshot();
        LastCloseReason = null;
    }

    public void MarkOpenConfirmed(ulong ticketA, ulong ticketB, DateTime confirmedAtUtc)
    {
        TicketA = ticketA;
        TicketB = ticketB;
        OpenConfirmedAtUtc = confirmedAtUtc;
        Status = PositionSlotStatus.Live;
    }

    public void MarkCloseTriggered(DateTime triggerAtUtc, CloseSignalReason closeReason = CloseSignalReason.Gap)
    {
        ClosedAtUtc = triggerAtUtc;
        LastCloseReason = closeReason;
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
        ClearProfitSnapshot();
        LastCloseReason = null;
    }

    public void UpdateProfit(ulong ticket, double profit)
    {
        if (TicketA.HasValue && ticket == TicketA.Value)
        {
            LastProfitA = profit;
        }
        else if (TicketB.HasValue && ticket == TicketB.Value)
        {
            LastProfitB = profit;
        }
        else
        {
            return;
        }

        LastProfitSnapshot = HasCompleteProfitSnapshot
            ? LastProfitA!.Value + LastProfitB!.Value
            : null;
    }

    /// <summary>
    /// Cập nhật P/L thật từ broker cho 1 leg (theo ticket). Tổng 2 leg chỉ sẵn sàng khi đã có
    /// cả A và B; thiếu 1 leg → <see cref="RealizedCloseProfit"/> = null. Ticket lạ → no-op.
    /// </summary>
    public void UpdateRealizedCloseProfit(ulong ticket, double profit)
    {
        if (TicketA.HasValue && ticket == TicketA.Value)
        {
            _realizedCloseProfitA = profit;
        }
        else if (TicketB.HasValue && ticket == TicketB.Value)
        {
            _realizedCloseProfitB = profit;
        }
        else
        {
            return;
        }

        RealizedCloseProfit = _realizedCloseProfitA.HasValue && _realizedCloseProfitB.HasValue
            ? _realizedCloseProfitA.Value + _realizedCloseProfitB.Value
            : null;
    }

    /// <summary>
    /// Replace CloseSignalEngine instance (dùng cho Phase 5 recovery — fresh engine, không recover window state).
    /// </summary>
    public void ResetCloseSignalEngine(ICloseSignalEngine engine)
    {
        CloseSignalEngine = engine;
    }

    private void ClearProfitSnapshot()
    {
        LastProfitA = null;
        LastProfitB = null;
        LastProfitSnapshot = null;
        _realizedCloseProfitA = null;
        _realizedCloseProfitB = null;
        RealizedCloseProfit = null;
    }
}
