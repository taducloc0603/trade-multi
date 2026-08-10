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
    public int? HwndProfileIndex { get; private set; }
    public string ChartHwndA { get; private set; } = string.Empty;
    public string ChartHwndB { get; private set; } = string.Empty;
    public string TradeHwndA { get; private set; } = string.Empty;
    public string TradeHwndB { get; private set; } = string.Empty;
    public DateTime? OpenedAtUtc { get; private set; }
    public DateTime? OpenConfirmedAtUtc { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public DateTime? CloseConfirmedAtUtc { get; private set; }
    public int HoldingSeconds { get; private set; }
    // Sampled once and retained so later config reloads/other slots cannot move this slot's deadlines.
    public int SelectedPostOpenLockSeconds { get; private set; }
    public int SelectedPostCloseLockSeconds { get; private set; }
    public bool IsCloseExecutionPending { get; private set; }
    public CloseExecutionOwner CloseOwner { get; private set; }
    public double? LastProfitSnapshot { get; internal set; }
    public double? LastProfitA { get; private set; }
    public double? LastProfitB { get; private set; }
    public bool HasCompleteProfitSnapshot => LastProfitA.HasValue && LastProfitB.HasValue;
    public CloseSignalReason? LastCloseReason { get; private set; }
    public CloseGapMode LastCloseGapMode { get; private set; } = CloseGapMode.Normal;
    public int? LastCloseConfirmGapPts { get; private set; }
    public int? LastCloseGapPts { get; private set; }
    public int? LastCloseHoldMs { get; private set; }
    public bool IsSosActive { get; private set; }
    public string SosActivationSource { get; private set; } = "NONE";
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
        ClearCloseSignalMetadata();
        IsSosActive = false;
        SosActivationSource = "NONE";
    }

    public void MarkOpenConfirmed(ulong ticketA, ulong ticketB, DateTime confirmedAtUtc, int postOpenLockSeconds = 0)
    {
        TicketA = ticketA;
        TicketB = ticketB;
        OpenConfirmedAtUtc = confirmedAtUtc;
        SelectedPostOpenLockSeconds = Math.Max(0, postOpenLockSeconds);
        Status = PositionSlotStatus.Live;
    }

    public bool TryMarkCloseTriggered(
        DateTime triggerAtUtc,
        CloseExecutionOwner owner,
        CloseSignalReason closeReason = CloseSignalReason.Gap,
        CloseGapMode closeGapMode = CloseGapMode.Normal,
        int? closeConfirmGapPts = null,
        int? closeGapPts = null,
        int? closeHoldMs = null)
    {
        if (owner == CloseExecutionOwner.None
            || (IsCloseExecutionPending && CloseOwner != owner))
        {
            return false;
        }

        ClosedAtUtc = triggerAtUtc;
        LastCloseReason = closeReason;
        LastCloseGapMode = closeGapMode;
        LastCloseConfirmGapPts = closeConfirmGapPts;
        LastCloseGapPts = closeGapPts;
        LastCloseHoldMs = closeHoldMs;
        IsCloseExecutionPending = true;
        CloseOwner = owner;
        Status = PositionSlotStatus.PendingClose;
        return true;
    }

    public void MarkCloseTriggered(DateTime triggerAtUtc, CloseSignalReason closeReason = CloseSignalReason.Gap)
    {
        TryMarkCloseTriggered(triggerAtUtc, CloseExecutionOwner.Auto, closeReason);
    }

    public void MarkCloseConfirmed(DateTime closedAtUtc)
    {
        CloseConfirmedAtUtc = closedAtUtc;
        IsCloseExecutionPending = false;
        CloseOwner = CloseExecutionOwner.None;
        Status = PositionSlotStatus.Closed;
    }

    public void ClearCloseExecutionPending()
    {
        IsCloseExecutionPending = false;
        CloseOwner = CloseExecutionOwner.None;
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
        SelectedPostOpenLockSeconds = 0;
        Status = PositionSlotStatus.Live;
        IsCloseExecutionPending = false;
        CloseOwner = CloseExecutionOwner.None;
        ClearProfitSnapshot();
        LastCloseReason = null;
        ClearCloseSignalMetadata();
        IsSosActive = false;
        SosActivationSource = "NONE";
    }

    public void SetSelectedPostOpenLockSeconds(int seconds)
        => SelectedPostOpenLockSeconds = Math.Max(0, seconds);

    public void SetSelectedPostCloseLockSeconds(int seconds)
        => SelectedPostCloseLockSeconds = Math.Max(0, seconds);

    public void SetHwndProfile(int? profileIndex, ManualHwndColumnConfig profile)
    {
        HwndProfileIndex = profileIndex;
        var normalized = (profile ?? ManualHwndColumnConfig.Empty).Normalize();
        ChartHwndA = normalized.ChartHwndA;
        ChartHwndB = normalized.ChartHwndB;
        TradeHwndA = normalized.TradeHwndA;
        TradeHwndB = normalized.TradeHwndB;
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
    /// Replace CloseSignalEngine instance (dùng cho Phase 5 recovery — fresh engine, không recover window state).
    /// </summary>
    public void ResetCloseSignalEngine(ICloseSignalEngine engine)
    {
        CloseSignalEngine = engine;
    }

    public bool UpdateSosMode(bool isActive, string source)
    {
        var changed = IsSosActive != isActive;
        IsSosActive = isActive;
        SosActivationSource = isActive ? source : "NONE";
        return changed;
    }

    private void ClearProfitSnapshot()
    {
        LastProfitA = null;
        LastProfitB = null;
        LastProfitSnapshot = null;
    }

    private void ClearCloseSignalMetadata()
    {
        LastCloseGapMode = CloseGapMode.Normal;
        LastCloseConfirmGapPts = null;
        LastCloseGapPts = null;
        LastCloseHoldMs = null;
    }
}

public enum CloseExecutionOwner
{
    None = 0,
    Auto = 1,
    Manual = 2,
    Recovery = 3
}
