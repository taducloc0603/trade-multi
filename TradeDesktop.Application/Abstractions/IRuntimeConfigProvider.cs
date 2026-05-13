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
    int CurrentCloseHoldConfirmMs { get; }
    int CurrentClosePriceFreezeMs { get; }
    int CurrentStartTimeHold { get; }
    int CurrentEndTimeHold { get; }
    int CurrentStartWaitTime { get; }
    int CurrentEndWaitTime { get; }
    int CurrentConfirmLatencyMs { get; }
    int CurrentMaxGap { get; }
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
    int CurrentOpenGapTick { get; }
    int CurrentCloseGapTick { get; }
    int CurrentCoolDownGapTick { get; }
    string CurrentMapName1 { get; }
    string CurrentMapName2 { get; }
    DashboardMetrics? CurrentDashboardMetrics { get; }
}
