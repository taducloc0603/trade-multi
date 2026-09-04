using TradeDesktop.Domain.Models;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IRuntimeConfigProvider
{
    string CurrentMachineHostName { get; }
    int CurrentPoint { get; }
    int CurrentOpenPts { get; }
    int CurrentConfirmGapPts { get; }
    int CurrentHoldConfirmMs => 0;
    int CurrentOpenPriceFreezeMs { get; }
    int CurrentClosePts { get; }
    int CurrentCloseConfirmGapPts { get; }
    double CurrentCloseTpProfit { get; }
    double CurrentCloseConfirmTpProfit { get; }
    int CurrentCloseHoldConfirmMs => 0;
    int CurrentClosePriceFreezeMs { get; }
    int CurrentStartTimeHold { get; }
    int CurrentEndTimeHold { get; }
    int CurrentConfirmLatencyMs { get; }
    int CurrentMaxGap { get; }
    int CurrentLimitMaxGap { get; }
    double CurrentLimitMaxTp { get; }
    double CurrentSosTriggerAOpenDistancePts { get; }
    int CurrentSosTriggerAfterSeconds { get; }
    int CurrentSosCloseConfirmGapPts { get; }
    int CurrentSosCloseGapPts { get; }
    int CurrentMaxSpread { get; }
    int CurrentOpenMaxTimesTick => 0;
    // Trần cho GAP CUỐI của Open Cycle. null = tắt gate.
    int? CurrentOpenMaxLastGapPts => null;
    int CurrentCloseMaxTimesTick => 0;
    int CurrentSignalCycleSize => 10;
    int CurrentOpenPendingTimeMs { get; }
    int CurrentClosePendingTimeMs { get; }
    int CurrentDelayOpenAMs { get; }
    int CurrentDelayOpenBMs { get; }
    int CurrentDelayCloseAMs { get; }
    int CurrentDelayCloseBMs { get; }
    int CurrentOpenNumberOfQualifyingTimes { get; }
    int CurrentCloseNumberOfQualifyingTimes { get; }
    GapStabilityConfig? CurrentOpenGapStability => null;
    GapStabilityConfig? CurrentCloseGapStability => null;
    int CurrentOppositeOpenMinDistancePts { get; }
    string CurrentMapName1 { get; }
    string CurrentMapName2 { get; }
    DashboardMetrics? CurrentDashboardMetrics { get; }

    // Thời điểm ứng dụng quan sát thấy Bid/Ask thực sự thay đổi trên từng sàn.
    // Default null giữ tương thích với các provider tối giản; production provider
    // phải cập nhật các giá trị này trên mỗi dashboard snapshot.
    DateTime? LastQuoteChangedAtUtcA => null;
    DateTime? LastQuoteChangedAtUtcB => null;
}
