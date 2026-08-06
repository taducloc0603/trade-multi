using TradeDesktop.Domain.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IRuntimeConfigProvider
{
    string CurrentMachineHostName { get; }
    int CurrentPoint { get; }
    int CurrentOpenPts { get; }
    int CurrentConfirmGapPts { get; }
    int CurrentHoldConfirmMs { get; }
    int CurrentOpenPriceFreezeMs { get; }
    int CurrentClosePts { get; }
    int CurrentCloseConfirmGapPts { get; }
    double CurrentCloseTpProfit { get; }
    double CurrentCloseConfirmTpProfit { get; }
    int CurrentCloseHoldConfirmMs { get; }
    int CurrentClosePriceFreezeMs { get; }
    int CurrentStartTimeHold { get; }
    int CurrentEndTimeHold { get; }
    int CurrentConfirmLatencyMs { get; }
    int CurrentMaxGap { get; }
    int CurrentLimitMaxGap { get; }
    double CurrentLimitMaxTp { get; }
    int CurrentMaxSpread { get; }
    int CurrentOpenMaxTimesTick { get; }
    int CurrentCloseMaxTimesTick { get; }
    int CurrentOpenPendingTimeMs { get; }
    int CurrentClosePendingTimeMs { get; }
    int CurrentDelayOpenAMs { get; }
    int CurrentDelayOpenBMs { get; }
    int CurrentDelayCloseAMs { get; }
    int CurrentDelayCloseBMs { get; }
    int CurrentOpenNumberOfQualifyingTimes { get; }
    int CurrentCloseNumberOfQualifyingTimes { get; }
    string CurrentMapName1 { get; }
    string CurrentMapName2 { get; }
    DashboardMetrics? CurrentDashboardMetrics { get; }
}
