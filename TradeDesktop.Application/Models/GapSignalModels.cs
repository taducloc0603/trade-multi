namespace TradeDesktop.Application.Models;

public enum GapSignalAction
{
    Open = 0,
    Close = 1
}

public enum GapSignalSide
{
    Buy = 0,
    Sell = 1
}

public enum CloseSignalReason
{
    Gap = 0,
    Tp = 1
}

public enum CloseGapMode
{
    Normal = 0,
    Sos = 1
}

public enum TradingFlowPhase
{
    WaitingOpen = 0,
    WaitingCloseFromGapBuy = 1,
    WaitingCloseFromGapSell = 2
}

public enum TradingOpenMode
{
    None = 0,
    GapBuy = 1,
    GapSell = 2
}

public enum TradingPositionSide
{
    None = 0,
    Buy = 1,
    Sell = 2
}

public sealed record GapSignalSnapshot(
    DateTime TimestampUtc,
    decimal? ExchangeABid,
    decimal? ExchangeAAsk,
    decimal? ExchangeBBid,
    decimal? ExchangeBAsk,
    int? GapBuy,
    int? GapSell,
    int PointMultiplier);

/// <summary>
/// Các giới hạn dùng để phân loại một chu kỳ Gap là ổn định.
/// Thời gian tối thiểu của chu kỳ tiếp tục lấy từ open/close_hold_confirm_ms.
/// </summary>
public sealed record GapStabilityConfig(
    int AbsoluteFloor,
    double RelativeTolerance,
    double MadMultiplier,
    int MinStableSamples,
    double MaxDispersion,
    double MaxDrift)
{
    public bool TryValidate(out string error)
    {
        if (AbsoluteFloor < 0)
        {
            error = "AbsoluteFloor phải >= 0.";
            return false;
        }

        if (!IsFiniteNonNegative(RelativeTolerance))
        {
            error = "RelativeTolerance phải là số hữu hạn và >= 0.";
            return false;
        }

        if (!IsFiniteNonNegative(MadMultiplier))
        {
            error = "MadMultiplier phải là số hữu hạn và >= 0.";
            return false;
        }

        if (MinStableSamples < 3)
        {
            error = "MinStableSamples phải >= 3.";
            return false;
        }

        if (!IsFiniteNonNegative(MaxDispersion))
        {
            error = "MaxDispersion phải là số hữu hạn và >= 0.";
            return false;
        }

        if (!IsFiniteNonNegative(MaxDrift))
        {
            error = "MaxDrift phải là số hữu hạn và >= 0.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsFiniteNonNegative(double value) =>
        double.IsFinite(value) && value >= 0d;
}

public sealed record GapSignalConfirmationConfig(
    int ConfirmGapPts,
    int OpenPts,
    int HoldConfirmMs,
    int CloseConfirmGapPts = 0,
    int ClosePts = 0,
    double CloseConfirmTpProfit = 0,
    double CloseTpProfit = 0,
    double CloseMaxTpProfit = 0,
    int CloseHoldConfirmMs = 0,
    int StartTimeHold = 0,
    int EndTimeHold = 0,
    int OpenMaxTimesTick = 0,
    int CloseMaxTimesTick = 0,
    int LimitMaxGap = 0,
    double LimitMaxTp = 0,
    double SosTriggerAOpenDistancePts = 0,
    int SosTriggerAfterSeconds = 0,
    int SosCloseConfirmGapPts = 0,
    int SosCloseGapPts = 0,
    CloseGapMode CloseGapMode = CloseGapMode.Normal,
    GapStabilityConfig? OpenGapStability = null,
    GapStabilityConfig? CloseGapStability = null);

public sealed record GapSignalTriggerResult(
    bool Triggered,
    GapSignalAction Action,
    GapSignalTriggerType TriggerType,
    GapSignalSide PrimarySide,
    IReadOnlyList<int> BuyGaps,
    IReadOnlyList<int> SellGaps,
    int? LastBuyGap,
    int? LastSellGap,
    DateTime TriggeredAtUtc,
    decimal? LastABid,
    decimal? LastAAsk,
    decimal? LastBBid,
    decimal? LastBAsk,
    decimal? GapBuySourceBBid,
    decimal? GapBuySourceAAsk,
    decimal? GapSellSourceBAsk,
    decimal? GapSellSourceABid,
    int PointMultiplier,
    CloseSignalReason CloseReason = CloseSignalReason.Gap,
    double? CloseTpProfit = null,
    double? CloseTpTarget = null,
    IReadOnlyList<double>? CloseTpProfits = null,
    CloseGapMode CloseGapMode = CloseGapMode.Normal,
    int? EffectiveCloseConfirmGapPts = null,
    int? EffectiveCloseGapPts = null,
    int? EffectiveCloseHoldMs = null);

public enum GapSignalTriggerType
{
    OpenByGapBuy = 0,
    OpenByGapSell = 1,
    CloseByGapBuy = 2,
    CloseByGapSell = 3
}

public sealed record TradeInstructionLeg(
    string Exchange,
    GapSignalAction Action,
    GapSignalSide Side,
    IReadOnlyList<int> Gaps,
    int? LastGap,
    decimal? Price);

public sealed record TradeSignalInstruction(
    DateTime TriggeredAtUtc,
    GapSignalTriggerType TriggerType,
    GapSignalAction Action,
    GapSignalSide PrimarySide,
    IReadOnlyList<int> TriggerGaps,
    int? LastTriggerGap,
    decimal? TriggerLeftPrice,
    decimal? TriggerRightPrice,
    string TriggerLeftLabel,
    string TriggerRightLabel,
    int PointMultiplier,
    TradeInstructionLeg ExchangeA,
    TradeInstructionLeg ExchangeB);
