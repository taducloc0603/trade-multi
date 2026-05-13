using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.App.State;

public sealed class RuntimeConfigState : IRuntimeConfigProvider, IRuntimeConfigStateUpdater
{
    private readonly Random _random = new();

    public string CurrentMachineHostName { get; private set; } = string.Empty;
    public int CurrentPoint { get; private set; }
    public int CurrentOpenPts { get; private set; }
    public int CurrentConfirmGapPts { get; private set; }
    public int CurrentHoldConfirmMs { get; private set; }
    public int CurrentOpenPriceFreezeMs { get; private set; }
    public int CurrentClosePts { get; private set; }
    public int CurrentCloseConfirmGapPts { get; private set; }
    public int CurrentCloseHoldConfirmMs { get; private set; }
    public int CurrentClosePriceFreezeMs { get; private set; }
    public int CurrentStartTimeHold { get; private set; }
    public int CurrentEndTimeHold { get; private set; }
    public int CurrentStartWaitTime { get; private set; }
    public int CurrentEndWaitTime { get; private set; }
    public int CurrentConfirmLatencyMs { get; private set; }
    public int CurrentMaxGap { get; private set; }
    public int CurrentMaxSpread { get; private set; }
    public int CurrentOpenMaxTimesTick { get; private set; }
    public int CurrentCloseMaxTimesTick { get; private set; }
    public int CurrentOpenPendingTimeMs { get; private set; } = 1000;
    public int CurrentClosePendingTimeMs { get; private set; } = 1000;
    public int CurrentDelayOpenAMs { get; private set; }
    public int CurrentDelayOpenBMs { get; private set; }
    public int CurrentDelayCloseAMs { get; private set; }
    public int CurrentDelayCloseBMs { get; private set; }
    public int CurrentOpenNumberOfQualifyingTimes { get; private set; } = 1;
    public int CurrentCloseNumberOfQualifyingTimes { get; private set; } = 1;
    public int CurrentOpenGapTick { get; private set; }
    public int CurrentCloseGapTick { get; private set; }
    public int CurrentCoolDownGapTick { get; private set; }
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
    public int HoldConfirmMs => CurrentHoldConfirmMs;
    public int OpenPriceFreezeMs => CurrentOpenPriceFreezeMs;
    public int ClosePts => CurrentClosePts;
    public int CloseConfirmGapPts => CurrentCloseConfirmGapPts;
    public int CloseHoldConfirmMs => CurrentCloseHoldConfirmMs;
    public int ClosePriceFreezeMs => CurrentClosePriceFreezeMs;
    public int StartTimeHold => CurrentStartTimeHold;
    public int EndTimeHold => CurrentEndTimeHold;
    public int StartWaitTime => CurrentStartWaitTime;
    public int EndWaitTime => CurrentEndWaitTime;
    public int ConfirmLatencyMs => CurrentConfirmLatencyMs;
    public int MaxGap => CurrentMaxGap;
    public int MaxSpread => CurrentMaxSpread;
    public int OpenMaxTimesTick => CurrentOpenMaxTimesTick;
    public int CloseMaxTimesTick => CurrentCloseMaxTimesTick;
    public int OpenPendingTimeMs => CurrentOpenPendingTimeMs;
    public int ClosePendingTimeMs => CurrentClosePendingTimeMs;
    public int DelayOpenAMs => CurrentDelayOpenAMs;
    public int DelayOpenBMs => CurrentDelayOpenBMs;
    public int DelayCloseAMs => CurrentDelayCloseAMs;
    public int DelayCloseBMs => CurrentDelayCloseBMs;
    public int OpenNumberOfQualifyingTimes => CurrentOpenNumberOfQualifyingTimes;
    public int CloseNumberOfQualifyingTimes => CurrentCloseNumberOfQualifyingTimes;
    public int OpenGapTick => CurrentOpenGapTick;
    public int CloseGapTick => CurrentCloseGapTick;
    public int CoolDownGapTick => CurrentCoolDownGapTick;

    public event EventHandler? StateChanged;
    public event EventHandler? QualifyingConfigChanged;

    public void Update(
        string machineHostName,
        string mapName1,
        string mapName2,
        int point,
        int openPts,
        int confirmGapPts,
        int holdConfirmMs,
        int openPriceFreezeMs,
        int closePts,
        int closeConfirmGapPts,
        int closeHoldConfirmMs,
        int closePriceFreezeMs,
        int startTimeHold,
        int endTimeHold,
        int startWaitTime,
        int endWaitTime,
        int confirmLatencyMs = 0,
        int maxGap = 0,
        int maxSpread = 0,
        int openMaxTimesTick = 0,
        int closeMaxTimesTick = 0,
        int openPendingTimeMs = -1,
        int closePendingTimeMs = -1,
        int delayOpenAMs = -1,
        int delayOpenBMs = -1,
        int delayCloseAMs = -1,
        int delayCloseBMs = -1,
        int openNumberOfQualifyingTimes = -1,
        int closeNumberOfQualifyingTimes = -1,
        int openGapTick = -1,
        int closeGapTick = -1,
        int coolDownGapTick = -1)
        => Update(
            machineHostName,
            mapName1,
            mapName2,
            CurrentPlatformA,
            CurrentPlatformB,
            point,
            openPts,
            confirmGapPts,
            holdConfirmMs,
            openPriceFreezeMs,
            closePts,
            closeConfirmGapPts,
            closeHoldConfirmMs,
            closePriceFreezeMs,
            startTimeHold,
            endTimeHold,
            startWaitTime,
            endWaitTime,
            confirmLatencyMs,
            maxGap,
            maxSpread,
            openMaxTimesTick,
            closeMaxTimesTick,
            openPendingTimeMs,
            closePendingTimeMs,
            delayOpenAMs,
            delayOpenBMs,
            delayCloseAMs,
            delayCloseBMs,
            openNumberOfQualifyingTimes,
            closeNumberOfQualifyingTimes,
            openGapTick,
            closeGapTick,
            coolDownGapTick);

    public void Update(
        string machineHostName,
        string mapName1,
        string mapName2,
        string platformA,
        string platformB,
        int point,
        int openPts,
        int confirmGapPts,
        int holdConfirmMs,
        int openPriceFreezeMs,
        int closePts,
        int closeConfirmGapPts,
        int closeHoldConfirmMs,
        int closePriceFreezeMs,
        int startTimeHold,
        int endTimeHold,
        int startWaitTime,
        int endWaitTime,
        int confirmLatencyMs = 0,
        int maxGap = 0,
        int maxSpread = 0,
        int openMaxTimesTick = 0,
        int closeMaxTimesTick = 0,
        int openPendingTimeMs = -1,
        int closePendingTimeMs = -1,
        int delayOpenAMs = -1,
        int delayOpenBMs = -1,
        int delayCloseAMs = -1,
        int delayCloseBMs = -1,
        int openNumberOfQualifyingTimes = -1,
        int closeNumberOfQualifyingTimes = -1,
        int openGapTick = -1,
        int closeGapTick = -1,
        int coolDownGapTick = -1)
    {
        var oldOpenN = CurrentOpenNumberOfQualifyingTimes;
        var oldCloseN = CurrentCloseNumberOfQualifyingTimes;

        CurrentMachineHostName = (machineHostName ?? string.Empty).Trim().ToLower();
        CurrentPoint = point > 0 ? point : 1;
        CurrentOpenPts = Math.Abs(openPts);
        CurrentConfirmGapPts = Math.Abs(confirmGapPts);
        CurrentHoldConfirmMs = Math.Max(0, holdConfirmMs);
        CurrentOpenPriceFreezeMs = openPriceFreezeMs >= 0
            ? Math.Max(0, openPriceFreezeMs)
            : CurrentHoldConfirmMs;
        CurrentClosePts = Math.Abs(closePts);
        CurrentCloseConfirmGapPts = Math.Abs(closeConfirmGapPts);
        CurrentCloseHoldConfirmMs = Math.Max(0, closeHoldConfirmMs);
        CurrentClosePriceFreezeMs = closePriceFreezeMs >= 0
            ? Math.Max(0, closePriceFreezeMs)
            : CurrentCloseHoldConfirmMs;
        CurrentStartTimeHold = Math.Max(0, startTimeHold);
        CurrentEndTimeHold = Math.Max(0, endTimeHold);
        CurrentStartWaitTime = Math.Max(0, startWaitTime);
        CurrentEndWaitTime = Math.Max(0, endWaitTime);
        CurrentConfirmLatencyMs = Math.Max(0, confirmLatencyMs);
        CurrentMaxGap = Math.Max(0, maxGap);
        CurrentMaxSpread = Math.Max(0, maxSpread);
        CurrentOpenMaxTimesTick = Math.Max(0, openMaxTimesTick);
        CurrentCloseMaxTimesTick = Math.Max(0, closeMaxTimesTick);
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
        if (openGapTick >= 0)
        {
            CurrentOpenGapTick = Math.Max(0, openGapTick);
        }
        if (closeGapTick >= 0)
        {
            CurrentCloseGapTick = Math.Max(0, closeGapTick);
        }
        if (coolDownGapTick >= 0)
        {
            CurrentCoolDownGapTick = Math.Max(0, coolDownGapTick);
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
            CurrentHoldConfirmMs,
            CurrentOpenPriceFreezeMs,
            CurrentClosePts,
            CurrentCloseConfirmGapPts,
            CurrentCloseHoldConfirmMs,
            CurrentClosePriceFreezeMs,
            CurrentStartTimeHold,
            CurrentEndTimeHold,
            CurrentStartWaitTime,
            CurrentEndWaitTime,
            CurrentConfirmLatencyMs,
            CurrentMaxGap,
            CurrentMaxSpread,
            CurrentOpenMaxTimesTick,
            CurrentCloseMaxTimesTick,
            CurrentOpenPendingTimeMs,
            CurrentClosePendingTimeMs,
            CurrentDelayOpenAMs,
            CurrentDelayOpenBMs,
            CurrentDelayCloseAMs,
            CurrentDelayCloseBMs,
            CurrentOpenNumberOfQualifyingTimes,
            CurrentCloseNumberOfQualifyingTimes,
            CurrentOpenGapTick,
            CurrentCloseGapTick,
            CurrentCoolDownGapTick);

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
            CurrentHoldConfirmMs,
            CurrentOpenPriceFreezeMs,
            CurrentClosePts,
            CurrentCloseConfirmGapPts,
            CurrentCloseHoldConfirmMs,
            CurrentClosePriceFreezeMs,
            CurrentStartTimeHold,
            CurrentEndTimeHold,
            CurrentStartWaitTime,
            CurrentEndWaitTime,
            CurrentConfirmLatencyMs,
            CurrentMaxGap,
            CurrentMaxSpread,
            CurrentOpenMaxTimesTick,
            CurrentCloseMaxTimesTick,
            CurrentOpenPendingTimeMs,
            CurrentClosePendingTimeMs,
            CurrentDelayOpenAMs,
            CurrentDelayOpenBMs,
            CurrentDelayCloseAMs,
            CurrentDelayCloseBMs,
            CurrentOpenNumberOfQualifyingTimes,
            CurrentCloseNumberOfQualifyingTimes,
            CurrentOpenGapTick,
            CurrentCloseGapTick,
            CurrentCoolDownGapTick);

    public void UpdatePlatform(string platformA, string platformB)
    {
        CurrentPlatformA = NormalizePlatform(platformA);
        CurrentPlatformB = NormalizePlatform(platformB);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateDashboardMetrics(DashboardMetrics snapshot)
    {
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

        CurrentManualHwndColumns = normalizedColumns;

        var first = normalizedColumns[0];
        CurrentChartHwndA = first.ChartHwndA;
        CurrentTradeHwndA = first.TradeHwndA;
        CurrentChartHwndB = first.ChartHwndB;
        CurrentTradeHwndB = first.TradeHwndB;

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public (int Index, ManualHwndColumnConfig Column) GetRandomManualHwndColumn()
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

        var index = _random.Next(0, columns.Count);
        return (index, columns[index]);
    }
}
