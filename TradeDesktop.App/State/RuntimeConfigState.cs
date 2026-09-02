using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.App.State;

public sealed class RuntimeConfigState : IRuntimeConfigProvider, IRuntimeConfigStateUpdater
{
    private readonly Random _random = new();
    private readonly object _manualHwndRandomLock = new();
    private int _lastRandomManualHwndColumnIndex = -1;

    public string CurrentMachineHostName { get; private set; } = string.Empty;
    public int CurrentPoint { get; private set; }
    public int CurrentOpenPts { get; private set; }
    public int CurrentConfirmGapPts { get; private set; }
    // Price-freeze khi thực thi Open. 0 = tắt kiểm tra.
    public int CurrentOpenPriceFreezeMs { get; private set; }
    public int CurrentClosePts { get; private set; }
    public int CurrentCloseConfirmGapPts { get; private set; }
    public double CurrentCloseTpProfit { get; private set; }
    public double CurrentCloseConfirmTpProfit { get; private set; }
    public double CurrentCloseMaxTpProfit { get; private set; }
    public double CurrentLimitMaxTp { get; private set; }
    public double CurrentSosTriggerAOpenDistancePts { get; private set; }
    public int CurrentSosTriggerAfterSeconds { get; private set; }
    public int CurrentSosCloseConfirmGapPts { get; private set; }
    public int CurrentSosCloseGapPts { get; private set; }
    // Price-freeze khi thực thi Close. 0 = tắt kiểm tra.
    public int CurrentClosePriceFreezeMs { get; private set; }
    public int CurrentStartTimeHold { get; private set; }
    public int CurrentEndTimeHold { get; private set; }
    public int CurrentConfirmLatencyMs { get; private set; }
    public int CurrentMaxGap { get; private set; }
    public int CurrentLimitMaxGap { get; private set; }
    public int CurrentMaxSpread { get; private set; }
    public int CurrentSignalCycleSize { get; private set; } = 10;
    public int CurrentOpenPendingTimeMs { get; private set; } = 1000;
    public int CurrentClosePendingTimeMs { get; private set; } = 1000;
    public int CurrentDelayOpenAMs { get; private set; }
    public int CurrentDelayOpenBMs { get; private set; }
    public int CurrentDelayCloseAMs { get; private set; }
    public int CurrentDelayCloseBMs { get; private set; }
    public int CurrentOpenNumberOfQualifyingTimes { get; private set; } = 1;
    public int CurrentCloseNumberOfQualifyingTimes { get; private set; } = 1;
    public int CurrentMaxLifeTimeBySecond { get; private set; }
    public double CurrentMinProfitToClose { get; private set; }
    public GapStabilityConfig? CurrentOpenGapStability { get; private set; }
    public GapStabilityConfig? CurrentCloseGapStability { get; private set; }

    // Rule C — opposite-side OPEN lock (sau OPEN, chỉ chặn chiều ngược). Default 300 khi DB
    // chưa có cột, override từ DB column opposite_side_lock_seconds.
    public int CurrentOppositeSideLockSeconds { get; private set; } = 300;
    public int CurrentOppositeOpenMinDistancePts { get; private set; }
    public int CurrentRdStartSameActionLockSeconds { get; private set; } = 3;
    public int CurrentRdEndSameActionLockSeconds { get; private set; } = 10;

    public int CurrentRdStartPostCloseLockSeconds { get; private set; } = 300;
    public int CurrentRdEndPostCloseLockSeconds { get; private set; } = 300;
    public int CurrentRdStartPostOpenLockSeconds { get; private set; }
    public int CurrentRdEndPostOpenLockSeconds { get; private set; }

    // Raw JSONB config; empty/null/malformed values are treated as disabled.
    public string CurrentScheduleSleepingJson { get; private set; } = string.Empty;

    // Multi-slot quota config. Min defaults to 1 để giữ behavior cũ khi DB chưa có cột.
    public int CurrentMaxTotalOpens { get; private set; } = 5;
    public int CurrentMinBuyOpens { get; private set; } = 1;
    public int CurrentMaxBuyOpens { get; private set; } = 3;
    public int CurrentMinSellOpens { get; private set; } = 1;
    public int CurrentMaxSellOpens { get; private set; } = 3;

    public string CurrentMapName1 { get; private set; } = string.Empty;
    public string CurrentMapName2 { get; private set; } = string.Empty;
    public string CurrentPlatformA { get; private set; } = "mt5";
    public string CurrentPlatformB { get; private set; } = "mt5";
    public string CurrentChartHwndA { get; private set; } = string.Empty;
    public string CurrentTradeHwndA { get; private set; } = string.Empty;
    public string CurrentChartHwndB { get; private set; } = string.Empty;
    public string CurrentTradeHwndB { get; private set; } = string.Empty;
    public IReadOnlyList<ManualHwndColumnConfig> CurrentManualHwndColumns { get; private set; } = [ManualHwndColumnConfig.Empty];
    public DashboardMetrics? CurrentDashboardMetrics { get; private set; }
    public DateTime? LastQuoteChangedAtUtcA { get; private set; }
    public DateTime? LastQuoteChangedAtUtcB { get; private set; }

    // Backward-compatible aliases for existing bindings/usages.
    public string MachineHostName => CurrentMachineHostName;
    public string MapName1 => CurrentMapName1;
    public string MapName2 => CurrentMapName2;
    public string PlatformA => CurrentPlatformA;
    public string PlatformB => CurrentPlatformB;
    public string ChartHwndA => CurrentChartHwndA;
    public string TradeHwndA => CurrentTradeHwndA;
    public string ChartHwndB => CurrentChartHwndB;
    public string TradeHwndB => CurrentTradeHwndB;
    public int OpenPts => CurrentOpenPts;
    public int ConfirmGapPts => CurrentConfirmGapPts;
    public int OpenPriceFreezeMs => CurrentOpenPriceFreezeMs;
    public int ClosePts => CurrentClosePts;
    public int CloseConfirmGapPts => CurrentCloseConfirmGapPts;
    public double CloseTpProfit => CurrentCloseTpProfit;
    public double CloseConfirmTpProfit => CurrentCloseConfirmTpProfit;
    public int ClosePriceFreezeMs => CurrentClosePriceFreezeMs;
    public int StartTimeHold => CurrentStartTimeHold;
    public int EndTimeHold => CurrentEndTimeHold;
    public int ConfirmLatencyMs => CurrentConfirmLatencyMs;
    public int MaxGap => CurrentMaxGap;
    public int LimitMaxGap => CurrentLimitMaxGap;
    public double LimitMaxTp => CurrentLimitMaxTp;
    public int MaxSpread => CurrentMaxSpread;
    public int SignalCycleSize => CurrentSignalCycleSize;
    public int OpenPendingTimeMs => CurrentOpenPendingTimeMs;
    public int ClosePendingTimeMs => CurrentClosePendingTimeMs;
    public int DelayOpenAMs => CurrentDelayOpenAMs;
    public int DelayOpenBMs => CurrentDelayOpenBMs;
    public int DelayCloseAMs => CurrentDelayCloseAMs;
    public int DelayCloseBMs => CurrentDelayCloseBMs;
    public int OpenNumberOfQualifyingTimes => CurrentOpenNumberOfQualifyingTimes;
    public int CloseNumberOfQualifyingTimes => CurrentCloseNumberOfQualifyingTimes;

    public event EventHandler? StateChanged;
    public event EventHandler? QualifyingConfigChanged;

    /// <summary>
    /// Bắn riêng khi cấu hình HWND (manual columns) thay đổi — KHÁC <see cref="StateChanged"/>
    /// (vốn bị raise mỗi ~50ms bởi UpdateDashboardMetrics). Dùng để chạy HWND health-check
    /// đắt (native) chỉ khi config HWND thật sự đổi.
    /// </summary>
    public event EventHandler? ManualHwndChanged;

    public void UpdateSignalCycleSize(int signalCycleSize)
    {
        if (signalCycleSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(signalCycleSize),
                "signal_cycle_size phải >= 1.");
        }

        CurrentSignalCycleSize = signalCycleSize;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateGapStability(
        GapStabilityConfig openGapStability,
        GapStabilityConfig closeGapStability)
    {
        ArgumentNullException.ThrowIfNull(openGapStability);
        ArgumentNullException.ThrowIfNull(closeGapStability);

        if (!openGapStability.TryValidate(out var openError))
        {
            throw new ArgumentOutOfRangeException(
                nameof(openGapStability),
                $"[OPEN GAP STABILITY] {openError}");
        }

        if (!closeGapStability.TryValidate(out var closeError))
        {
            throw new ArgumentOutOfRangeException(
                nameof(closeGapStability),
                $"[NORMAL CLOSE GAP STABILITY] {closeError}");
        }

        CurrentOpenGapStability = openGapStability;
        CurrentCloseGapStability = closeGapStability;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Update(
        string machineHostName,
        string mapName1,
        string mapName2,
        int point,
        int openPts,
        int confirmGapPts,
        int openPriceFreezeMs,
        int closePts,
        int closeConfirmGapPts,
        double closeTpProfit,
        double closeConfirmTpProfit,
        int closePriceFreezeMs,
        int startTimeHold,
        int endTimeHold,
        int confirmLatencyMs = 0,
        int maxGap = 0,
        int limitMaxGap = 0,
        int maxSpread = 0,
        int openPendingTimeMs = -1,
        int closePendingTimeMs = -1,
        int delayOpenAMs = -1,
        int delayOpenBMs = -1,
        int delayCloseAMs = -1,
        int delayCloseBMs = -1,
        int openNumberOfQualifyingTimes = -1,
        int closeNumberOfQualifyingTimes = -1,
        int maxLifeTimeBySecond = -1,
        double closeMaxTpProfit = 0,
        double limitMaxTp = 0,
        double sosTriggerAOpenDistancePts = 0,
        int sosTriggerAfterSeconds = 0,
        int sosCloseConfirmGapPts = 0,
        int sosCloseGapPts = 0,
        int oppositeSideLockSeconds = -1,
        int oppositeOpenMinDistancePts = -1,
        int rdStartSameActionLockSeconds = -1,
        int rdEndSameActionLockSeconds = -1,
        int rdStartPostCloseLockSeconds = -1,
        int rdEndPostCloseLockSeconds = -1,
        int rdStartPostOpenLockSeconds = -1,
        int rdEndPostOpenLockSeconds = -1,
        double minProfitToClose = 0)
        => Update(
            machineHostName,
            mapName1,
            mapName2,
            CurrentPlatformA,
            CurrentPlatformB,
            point,
            openPts,
            confirmGapPts,
            openPriceFreezeMs,
            closePts,
            closeConfirmGapPts,
            closeTpProfit,
            closeConfirmTpProfit,
            closePriceFreezeMs,
            startTimeHold,
            endTimeHold,
            confirmLatencyMs,
            maxGap,
            limitMaxGap,
            maxSpread,
            openPendingTimeMs,
            closePendingTimeMs,
            delayOpenAMs,
            delayOpenBMs,
            delayCloseAMs,
            delayCloseBMs,
            openNumberOfQualifyingTimes,
            closeNumberOfQualifyingTimes,
            maxLifeTimeBySecond,
            closeMaxTpProfit,
            limitMaxTp,
            sosTriggerAOpenDistancePts,
            sosTriggerAfterSeconds,
            sosCloseConfirmGapPts,
            sosCloseGapPts,
            oppositeSideLockSeconds,
            oppositeOpenMinDistancePts,
            rdStartSameActionLockSeconds,
            rdEndSameActionLockSeconds,
            rdStartPostCloseLockSeconds,
            rdEndPostCloseLockSeconds,
            rdStartPostOpenLockSeconds,
            rdEndPostOpenLockSeconds,
            minProfitToClose);

    public void Update(
        string machineHostName,
        string mapName1,
        string mapName2,
        string platformA,
        string platformB,
        int point,
        int openPts,
        int confirmGapPts,
        int openPriceFreezeMs,
        int closePts,
        int closeConfirmGapPts,
        double closeTpProfit,
        double closeConfirmTpProfit,
        int closePriceFreezeMs,
        int startTimeHold,
        int endTimeHold,
        int confirmLatencyMs = 0,
        int maxGap = 0,
        int limitMaxGap = 0,
        int maxSpread = 0,
        int openPendingTimeMs = -1,
        int closePendingTimeMs = -1,
        int delayOpenAMs = -1,
        int delayOpenBMs = -1,
        int delayCloseAMs = -1,
        int delayCloseBMs = -1,
        int openNumberOfQualifyingTimes = -1,
        int closeNumberOfQualifyingTimes = -1,
        int maxLifeTimeBySecond = -1,
        double closeMaxTpProfit = 0,
        double limitMaxTp = 0,
        double sosTriggerAOpenDistancePts = 0,
        int sosTriggerAfterSeconds = 0,
        int sosCloseConfirmGapPts = 0,
        int sosCloseGapPts = 0,
        int oppositeSideLockSeconds = -1,
        int oppositeOpenMinDistancePts = -1,
        int rdStartSameActionLockSeconds = -1,
        int rdEndSameActionLockSeconds = -1,
        int rdStartPostCloseLockSeconds = -1,
        int rdEndPostCloseLockSeconds = -1,
        int rdStartPostOpenLockSeconds = -1,
        int rdEndPostOpenLockSeconds = -1,
        double minProfitToClose = 0)
    {
        var oldOpenN = CurrentOpenNumberOfQualifyingTimes;
        var oldCloseN = CurrentCloseNumberOfQualifyingTimes;

        CurrentMachineHostName = (machineHostName ?? string.Empty).Trim().ToLower();
        CurrentPoint = point > 0 ? point : 1;
        // 4 ngưỡng gap thường giữ nguyên dấu như cặp SOS: ÂM nghĩa là nới ngưỡng về phía trong.
        CurrentOpenPts = openPts;
        CurrentConfirmGapPts = confirmGapPts;
        CurrentOpenPriceFreezeMs = Math.Max(0, openPriceFreezeMs);
        CurrentClosePts = closePts;
        CurrentCloseConfirmGapPts = closeConfirmGapPts;
        CurrentCloseTpProfit = Math.Abs(closeTpProfit);
        CurrentCloseConfirmTpProfit = Math.Abs(closeConfirmTpProfit);
        CurrentCloseMaxTpProfit = Math.Abs(closeMaxTpProfit);
        CurrentLimitMaxTp = Math.Abs(limitMaxTp);
        CurrentSosTriggerAOpenDistancePts = Math.Max(0d, sosTriggerAOpenDistancePts);
        CurrentSosTriggerAfterSeconds = Math.Max(0, sosTriggerAfterSeconds);
        CurrentSosCloseConfirmGapPts = sosCloseConfirmGapPts;
        CurrentSosCloseGapPts = sosCloseGapPts;
        CurrentClosePriceFreezeMs = Math.Max(0, closePriceFreezeMs);
        CurrentStartTimeHold = Math.Max(0, startTimeHold);
        CurrentEndTimeHold = Math.Max(0, endTimeHold);
        CurrentConfirmLatencyMs = Math.Max(0, confirmLatencyMs);
        CurrentMaxGap = Math.Max(0, maxGap);
        CurrentLimitMaxGap = Math.Max(0, limitMaxGap);
        CurrentMaxSpread = Math.Max(0, maxSpread);
        if (openPendingTimeMs >= 0)
        {
            CurrentOpenPendingTimeMs = Math.Max(0, openPendingTimeMs);
        }
        if (closePendingTimeMs >= 0)
        {
            CurrentClosePendingTimeMs = Math.Max(0, closePendingTimeMs);
        }
        if (delayOpenAMs >= 0)
        {
            CurrentDelayOpenAMs = Math.Max(0, delayOpenAMs);
        }
        if (delayOpenBMs >= 0)
        {
            CurrentDelayOpenBMs = Math.Max(0, delayOpenBMs);
        }
        if (delayCloseAMs >= 0)
        {
            CurrentDelayCloseAMs = Math.Max(0, delayCloseAMs);
        }
        if (delayCloseBMs >= 0)
        {
            CurrentDelayCloseBMs = Math.Max(0, delayCloseBMs);
        }
        if (openNumberOfQualifyingTimes >= 0)
        {
            CurrentOpenNumberOfQualifyingTimes = Math.Max(1, openNumberOfQualifyingTimes);
        }
        if (closeNumberOfQualifyingTimes >= 0)
        {
            CurrentCloseNumberOfQualifyingTimes = Math.Max(1, closeNumberOfQualifyingTimes);
        }
        if (maxLifeTimeBySecond >= 0)
        {
            CurrentMaxLifeTimeBySecond = Math.Max(0, maxLifeTimeBySecond);
        }
        CurrentMinProfitToClose = Math.Max(0d, minProfitToClose);
        if (oppositeSideLockSeconds >= 0)
        {
            // 0 = tắt lock nguy hiểm → giữ default 300 khi <= 0.
            CurrentOppositeSideLockSeconds = oppositeSideLockSeconds > 0 ? oppositeSideLockSeconds : 300;
        }
        if (oppositeOpenMinDistancePts >= 0)
        {
            CurrentOppositeOpenMinDistancePts = Math.Max(0, oppositeOpenMinDistancePts);
        }
        if (rdStartSameActionLockSeconds >= 0 || rdEndSameActionLockSeconds >= 0)
        {
            var start = rdStartSameActionLockSeconds > 0 ? rdStartSameActionLockSeconds : 3;
            var end = rdEndSameActionLockSeconds > 0 ? rdEndSameActionLockSeconds : 10;
            CurrentRdStartSameActionLockSeconds = Math.Min(start, end);
            CurrentRdEndSameActionLockSeconds = Math.Max(start, end);
        }
        if (rdStartPostCloseLockSeconds >= 0 || rdEndPostCloseLockSeconds >= 0)
        {
            var start = rdStartPostCloseLockSeconds > 0 ? rdStartPostCloseLockSeconds : 300;
            var end = rdEndPostCloseLockSeconds > 0 ? rdEndPostCloseLockSeconds : 300;
            CurrentRdStartPostCloseLockSeconds = Math.Min(start, end);
            CurrentRdEndPostCloseLockSeconds = Math.Max(start, end);
        }
        if (rdStartPostOpenLockSeconds >= 0 || rdEndPostOpenLockSeconds >= 0)
        {
            var start = Math.Max(0, rdStartPostOpenLockSeconds);
            var end = Math.Max(0, rdEndPostOpenLockSeconds);
            CurrentRdStartPostOpenLockSeconds = Math.Min(start, end);
            CurrentRdEndPostOpenLockSeconds = Math.Max(start, end);
        }
        CurrentMapName1 = (mapName1 ?? string.Empty).Trim();
        CurrentMapName2 = (mapName2 ?? string.Empty).Trim();
        CurrentPlatformA = NormalizePlatform(platformA);
        CurrentPlatformB = NormalizePlatform(platformB);

        if (oldOpenN != CurrentOpenNumberOfQualifyingTimes
            || oldCloseN != CurrentCloseNumberOfQualifyingTimes)
        {
            QualifyingConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizePlatform(string? platform)
    {
        var normalized = (platform ?? string.Empty).Trim().ToLower();
        return normalized is "mt4" or "mt5" ? normalized : "mt5";
    }

    public void Update(string machineHostName, string mapName1, string mapName2, int point)
        => Update(
            machineHostName,
            mapName1,
            mapName2,
            CurrentPlatformA,
            CurrentPlatformB,
            point,
            CurrentOpenPts,
            CurrentConfirmGapPts,
            CurrentOpenPriceFreezeMs,
            CurrentClosePts,
            CurrentCloseConfirmGapPts,
            CurrentCloseTpProfit,
            CurrentCloseConfirmTpProfit,
            CurrentClosePriceFreezeMs,
            CurrentStartTimeHold,
            CurrentEndTimeHold,
            CurrentConfirmLatencyMs,
            CurrentMaxGap,
            CurrentLimitMaxGap,
            CurrentMaxSpread,
            CurrentOpenPendingTimeMs,
            CurrentClosePendingTimeMs,
            CurrentDelayOpenAMs,
            CurrentDelayOpenBMs,
            CurrentDelayCloseAMs,
            CurrentDelayCloseBMs,
            CurrentOpenNumberOfQualifyingTimes,
            CurrentCloseNumberOfQualifyingTimes,
            closeMaxTpProfit: CurrentCloseMaxTpProfit,
            limitMaxTp: CurrentLimitMaxTp,
            sosTriggerAOpenDistancePts: CurrentSosTriggerAOpenDistancePts,
            sosTriggerAfterSeconds: CurrentSosTriggerAfterSeconds,
            sosCloseConfirmGapPts: CurrentSosCloseConfirmGapPts,
            sosCloseGapPts: CurrentSosCloseGapPts);

    public void Update(string machineHostName, string mapName1, string mapName2)
        => Update(
            machineHostName,
            mapName1,
            mapName2,
            CurrentPlatformA,
            CurrentPlatformB,
            CurrentPoint,
            CurrentOpenPts,
            CurrentConfirmGapPts,
            CurrentOpenPriceFreezeMs,
            CurrentClosePts,
            CurrentCloseConfirmGapPts,
            CurrentCloseTpProfit,
            CurrentCloseConfirmTpProfit,
            CurrentClosePriceFreezeMs,
            CurrentStartTimeHold,
            CurrentEndTimeHold,
            CurrentConfirmLatencyMs,
            CurrentMaxGap,
            CurrentLimitMaxGap,
            CurrentMaxSpread,
            CurrentOpenPendingTimeMs,
            CurrentClosePendingTimeMs,
            CurrentDelayOpenAMs,
            CurrentDelayOpenBMs,
            CurrentDelayCloseAMs,
            CurrentDelayCloseBMs,
            CurrentOpenNumberOfQualifyingTimes,
            CurrentCloseNumberOfQualifyingTimes,
            closeMaxTpProfit: CurrentCloseMaxTpProfit,
            limitMaxTp: CurrentLimitMaxTp,
            sosTriggerAOpenDistancePts: CurrentSosTriggerAOpenDistancePts,
            sosTriggerAfterSeconds: CurrentSosTriggerAfterSeconds,
            sosCloseConfirmGapPts: CurrentSosCloseConfirmGapPts,
            sosCloseGapPts: CurrentSosCloseGapPts);

    // Quota từ DB (max_total_opens / max_buy_opens / max_sell_opens). Floor về 1 để không
    // bao giờ khoá toàn bộ open. Raise StateChanged để ApplyRuntimeConfig → SyncPortfolioCoordinatorConfig
    // đẩy giá trị mới xuống coordinator.
    public void UpdateQuota(
        int maxTotalOpens,
        int minBuyOpens,
        int maxBuyOpens,
        int minSellOpens,
        int maxSellOpens)
    {
        CurrentMaxTotalOpens = Math.Max(1, maxTotalOpens);
        CurrentMaxBuyOpens = Math.Max(1, maxBuyOpens);
        CurrentMaxSellOpens = Math.Max(1, maxSellOpens);
        CurrentMinBuyOpens = Math.Clamp(minBuyOpens, 1, CurrentMaxBuyOpens);
        CurrentMinSellOpens = Math.Clamp(minSellOpens, 1, CurrentMaxSellOpens);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateScheduleSleeping(string? scheduleSleepingJson)
    {
        CurrentScheduleSleepingJson = scheduleSleepingJson ?? string.Empty;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdatePlatform(string platformA, string platformB)
    {
        CurrentPlatformA = NormalizePlatform(platformA);
        CurrentPlatformB = NormalizePlatform(platformB);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateDashboardMetrics(DashboardMetrics snapshot)
    {
        var observedAtUtc = DateTime.UtcNow;
        var previous = CurrentDashboardMetrics;

        if (previous is null
            || previous.ExchangeA.Bid != snapshot.ExchangeA.Bid
            || previous.ExchangeA.Ask != snapshot.ExchangeA.Ask)
        {
            LastQuoteChangedAtUtcA = observedAtUtc;
        }

        if (previous is null
            || previous.ExchangeB.Bid != snapshot.ExchangeB.Bid
            || previous.ExchangeB.Ask != snapshot.ExchangeB.Ask)
        {
            LastQuoteChangedAtUtcB = observedAtUtc;
        }

        CurrentDashboardMetrics = snapshot;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateManualTradeHwnd(string chartHwndA, string tradeHwndA, string chartHwndB, string tradeHwndB)
        => UpdateManualTradeHwnd([
            new ManualHwndColumnConfig(chartHwndA, tradeHwndA, chartHwndB, tradeHwndB)
        ]);

    public void UpdateManualTradeHwnd(IReadOnlyList<ManualHwndColumnConfig>? columns)
    {
        var normalizedColumns = (columns ?? [ManualHwndColumnConfig.Empty])
            .Select(x => (x ?? ManualHwndColumnConfig.Empty).Normalize())
            .ToList();

        if (normalizedColumns.Count == 0)
        {
            normalizedColumns.Add(ManualHwndColumnConfig.Empty);
        }

        lock (_manualHwndRandomLock)
        {
            CurrentManualHwndColumns = normalizedColumns;
            _lastRandomManualHwndColumnIndex = -1;

            var first = normalizedColumns[0];
            CurrentChartHwndA = first.ChartHwndA;
            CurrentTradeHwndA = first.TradeHwndA;
            CurrentChartHwndB = first.ChartHwndB;
            CurrentTradeHwndB = first.TradeHwndB;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        ManualHwndChanged?.Invoke(this, EventArgs.Empty);
    }

    public (int Index, ManualHwndColumnConfig Column) GetRandomManualHwndColumn()
    {
        lock (_manualHwndRandomLock)
        {
            var columns = CurrentManualHwndColumns;
            if (columns.Count == 0)
            {
                return (0, ManualHwndColumnConfig.Empty);
            }

            if (columns.Count == 1)
            {
                return (0, columns[0]);
            }

            // Với 1-2 cột, giữ nguyên hành vi random độc lập hiện tại.
            if (columns.Count == 2)
            {
                var twoColumnIndex = _random.Next(0, columns.Count);
                return (twoColumnIndex, columns[twoColumnIndex]);
            }

            // Từ 3 cột trở lên, random trong toàn bộ các cột ngoại trừ cột vừa chọn.
            // Chọn trong [0..Count-2], rồi dịch qua last index để không cần tạo list phụ.
            var index = _lastRandomManualHwndColumnIndex < 0
                ? _random.Next(0, columns.Count)
                : _random.Next(0, columns.Count - 1);
            if (_lastRandomManualHwndColumnIndex >= 0 &&
                index >= _lastRandomManualHwndColumnIndex)
            {
                index++;
            }

            _lastRandomManualHwndColumnIndex = index;
            return (index, columns[index]);
        }
    }
}
