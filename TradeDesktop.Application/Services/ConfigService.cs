using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Helpers;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public interface IConfigService
{
    Task<ConfigLoadResult> LoadByMachineHostNameAsync(CancellationToken cancellationToken = default);
    Task<ConfigSaveResult> SaveByMachineHostNameAsync(
        string mapName1,
        string mapName2,
        string platformA,
        string platformB,
        IReadOnlyList<ManualHwndColumnConfig>? manualHwndColumns = null,
        CancellationToken cancellationToken = default);
    Task SaveCurrentTicksAsync(string currentTickA, string currentTickB, CancellationToken cancellationToken = default);
    Task SaveCurrentSlotsAsync(string currentSlotsJson, CancellationToken cancellationToken = default);
}

public sealed class ConfigService(
    IConfigRepository configRepository,
    IMachineIdentityService machineIdentityService) : IConfigService
{
    private static string NormalizePlatform(string? platform)
    {
        var normalized = (platform ?? string.Empty).Trim().ToLower();
        return normalized is "mt4" or "mt5" ? normalized : "mt5";
    }

    public async Task<ConfigLoadResult> LoadByMachineHostNameAsync(CancellationToken cancellationToken = default)
    {
        var hostName = machineIdentityService.GetHostName();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return ConfigLoadResult.Failed(string.Empty, "Không lấy được host name máy hiện tại.");
        }

        var record = await configRepository.GetByHostNameAsync(hostName, cancellationToken);
        if (record is null)
        {
            return ConfigLoadResult.NotFound(hostName);
        }

        SansJsonHelper.TryParseSans(record.SansJson, out var mapName1, out var mapName2, out var manualHwndColumns);
        return ConfigLoadResult.Success(
            hostName,
            mapName1,
            mapName2,
            manualHwndColumns,
            record.PlatformA,
            record.PlatformB,
            record.Point,
            record.OpenPts,
            record.ConfirmGapPts,
            record.HoldConfirmMs,
            record.OpenPriceFreezeMs,
            record.ClosePts,
            record.CloseConfirmGapPts,
            record.CloseTpProfit,
            record.CloseConfirmTpProfit,
            record.CloseMaxTpProfit,
            record.CloseHoldConfirmMs,
            record.ClosePriceFreezeMs,
            record.StartTimeHold,
            record.EndTimeHold,
            record.Id,
            record.SansJson,
            record.ConfirmLatencyMs,
            record.MaxGap,
            record.LimitMaxGap,
            record.MaxSpread,
            record.OpenMaxTimesTick,
            record.CloseMaxTimesTick,
            record.OpenPendingTimeMs,
            record.ClosePendingTimeMs,
            record.DelayOpenAMs,
            record.DelayOpenBMs,
            record.DelayCloseAMs,
            record.DelayCloseBMs,
            record.OpenNumberOfQualifyingTimes,
            record.CloseNumberOfQualifyingTimes,
            record.IsShowConfig,
            record.CurrentTickA,
            record.CurrentTickB,
            record.CurrentSlots,
            record.MaxLifeTimeBySecond,
            limitMaxTp: record.LimitMaxTp,
            maxBuyOpens: record.MaxBuyOpens,
            maxSellOpens: record.MaxSellOpens,
            maxTotalOpens: record.MaxTotalOpens,
            freezeLastN: record.FreezeLastN,
            closeMinProfit: record.CloseMinProfit,
            oppositeSideLockSeconds: record.OppositeSideLockSeconds,
            rdStartPostCloseLockSeconds: record.RdStartPostCloseLockSeconds,
            rdEndPostCloseLockSeconds: record.RdEndPostCloseLockSeconds,
            rdStartPostOpenLockSeconds: record.RdStartPostOpenLockSeconds,
            rdEndPostOpenLockSeconds: record.RdEndPostOpenLockSeconds,
            scheduleSleepingJson: record.ScheduleSleepingJson);
    }

    public async Task SaveCurrentTicksAsync(string currentTickA, string currentTickB, CancellationToken cancellationToken = default)
    {
        var hostName = machineIdentityService.GetHostName();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return;
        }

        await configRepository.UpdateCurrentTicksAsync(hostName, currentTickA, currentTickB, cancellationToken);
    }

    public async Task SaveCurrentSlotsAsync(string currentSlotsJson, CancellationToken cancellationToken = default)
    {
        var hostName = machineIdentityService.GetHostName();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return;
        }

        await configRepository.UpdateCurrentSlotsAsync(hostName, currentSlotsJson, cancellationToken);
    }

    public async Task<ConfigSaveResult> SaveByMachineHostNameAsync(
        string mapName1,
        string mapName2,
        string platformA,
        string platformB,
        IReadOnlyList<ManualHwndColumnConfig>? manualHwndColumns = null,
        CancellationToken cancellationToken = default)
    {
        var hostName = machineIdentityService.GetHostName();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return ConfigSaveResult.Failed("Không lấy được host name máy hiện tại.");
        }

        var normalizedPlatformA = NormalizePlatform(platformA);
        var normalizedPlatformB = NormalizePlatform(platformB);

        var sansJson = SansJsonHelper.BuildSans(mapName1, mapName2, manualHwndColumns);

        var updated = await configRepository.UpdateSansAndHostNameByHostNameAsync(
            hostName,
            sansJson,
            normalizedPlatformA,
            normalizedPlatformB,
            cancellationToken);
        if (!updated)
        {
            return ConfigSaveResult.Failed("Lưu thất bại: không có bản ghi nào được cập nhật.");
        }

        var refreshed = await configRepository.GetByHostNameAsync(hostName, cancellationToken);
        if (refreshed is null)
        {
            return ConfigSaveResult.Failed("Đã gọi lưu nhưng không đọc lại được record để xác nhận giá trị hostname.");
        }

        var savedHostName = (refreshed.HostName ?? string.Empty).Trim().ToLower();
        if (!string.Equals(savedHostName, hostName, StringComparison.Ordinal))
        {
            return ConfigSaveResult.Failed(
                $"Lưu chưa hoàn tất: hostname trong DB là '{savedHostName}' nhưng hostname local là '{hostName}'. Kiểm tra quyền update cột hostname/RLS hoặc trigger DB.");
        }

        return ConfigSaveResult.Success(hostName);
    }
}

public sealed record ConfigLoadResult(
    bool IsSuccess,
    bool Exists,
    string MachineHostName,
    IReadOnlyList<ManualHwndColumnConfig> ManualHwndColumns,
    string PlatformA,
    string PlatformB,
    int Point,
    int OpenPts,
    int ConfirmGapPts,
    int HoldConfirmMs,
    int OpenPriceFreezeMs,
    int ClosePts,
    int CloseConfirmGapPts,
    double CloseTpProfit,
    double CloseConfirmTpProfit,
    double CloseMaxTpProfit,
    double LimitMaxTp,
    int CloseHoldConfirmMs,
    int ClosePriceFreezeMs,
    int StartTimeHold,
    int EndTimeHold,
    string MapName1,
    string MapName2,
    string ConfigId,
    string SansJson,
    string? Error,
    int ConfirmLatencyMs,
    int MaxGap,
    int LimitMaxGap,
    int MaxSpread,
    int OpenMaxTimesTick,
    int CloseMaxTimesTick,
    int OpenPendingTimeMs,
    int ClosePendingTimeMs,
    int DelayOpenAMs,
    int DelayOpenBMs,
    int DelayCloseAMs,
    int DelayCloseBMs,
    int OpenNumberOfQualifyingTimes,
    int CloseNumberOfQualifyingTimes,
    int IsShowConfig = 0,
    string CurrentTickA = "",
    string CurrentTickB = "",
    string CurrentSlots = "",
    int MaxLifeTimeBySecond = 0,
    int MaxBuyOpens = 3,
    int MaxSellOpens = 3,
    int MaxTotalOpens = 5,
    int FreezeLastN = 0,
    double CloseMinProfit = 0,
    int OppositeSideLockSeconds = 300,
    int RdStartPostCloseLockSeconds = 300,
    int RdEndPostCloseLockSeconds = 300,
    int RdStartPostOpenLockSeconds = 0,
    int RdEndPostOpenLockSeconds = 0,
    string ScheduleSleepingJson = "")
{
    public static ConfigLoadResult Success(
        string machineHostName,
        string mapName1,
        string mapName2,
        IReadOnlyList<ManualHwndColumnConfig>? manualHwndColumns,
        string platformA,
        string platformB,
        int point,
        int openPts,
        int confirmGapPts,
        int holdConfirmMs,
        int openPriceFreezeMs,
        int closePts,
        int closeConfirmGapPts,
        double closeTpProfit,
        double closeConfirmTpProfit,
        double closeMaxTpProfit,
        int closeHoldConfirmMs,
        int closePriceFreezeMs,
        int startTimeHold,
        int endTimeHold,
        string configId,
        string sansJson,
        int confirmLatencyMs = 0,
        int maxGap = 0,
        int limitMaxGap = 0,
        int maxSpread = 0,
        int openMaxTimesTick = 0,
        int closeMaxTimesTick = 0,
        int openPendingTimeMs = 0,
        int closePendingTimeMs = 0,
        int delayOpenAMs = 0,
        int delayOpenBMs = 0,
        int delayCloseAMs = 0,
        int delayCloseBMs = 0,
        int openNumberOfQualifyingTimes = 1,
        int closeNumberOfQualifyingTimes = 1,
        int isShowConfig = 0,
        string currentTickA = "",
        string currentTickB = "",
        string currentSlots = "",
        int maxLifeTimeBySecond = 0,
        double limitMaxTp = 0,
        int maxBuyOpens = 3,
        int maxSellOpens = 3,
        int maxTotalOpens = 5,
        int freezeLastN = 0,
        double closeMinProfit = 0,
        int oppositeSideLockSeconds = 300,
        int rdStartPostCloseLockSeconds = 300,
        int rdEndPostCloseLockSeconds = 300,
        int rdStartPostOpenLockSeconds = 0,
        int rdEndPostOpenLockSeconds = 0,
        string scheduleSleepingJson = "") =>
        new(
            true,
            true,
            machineHostName,
            NormalizeColumns(manualHwndColumns),
            NormalizePlatform(platformA),
            NormalizePlatform(platformB),
            point > 0 ? point : 1,
            Math.Abs(openPts),
            Math.Abs(confirmGapPts),
            Math.Max(0, holdConfirmMs),
            Math.Max(0, openPriceFreezeMs),
            Math.Abs(closePts),
            Math.Abs(closeConfirmGapPts),
            Math.Abs(closeTpProfit),
            Math.Abs(closeConfirmTpProfit),
            Math.Abs(closeMaxTpProfit),
            Math.Abs(limitMaxTp),
            Math.Max(0, closeHoldConfirmMs),
            Math.Max(0, closePriceFreezeMs),
            Math.Max(0, startTimeHold),
            Math.Max(0, endTimeHold),
            mapName1,
            mapName2,
            configId,
            sansJson,
            null,
            Math.Max(0, confirmLatencyMs),
            Math.Max(0, maxGap),
            Math.Max(0, limitMaxGap),
            Math.Max(0, maxSpread),
            Math.Max(0, openMaxTimesTick),
            Math.Max(0, closeMaxTimesTick),
            Math.Max(0, openPendingTimeMs),
            Math.Max(0, closePendingTimeMs),
            Math.Max(0, delayOpenAMs),
            Math.Max(0, delayOpenBMs),
            Math.Max(0, delayCloseAMs),
            Math.Max(0, delayCloseBMs),
            Math.Max(1, openNumberOfQualifyingTimes),
            Math.Max(1, closeNumberOfQualifyingTimes),
            isShowConfig,
            currentTickA ?? string.Empty,
            currentTickB ?? string.Empty,
            currentSlots ?? string.Empty,
            Math.Max(0, maxLifeTimeBySecond),
            Math.Max(1, maxBuyOpens),
            Math.Max(1, maxSellOpens),
            Math.Max(1, maxTotalOpens),
            Math.Max(0, freezeLastN),
            Math.Abs(closeMinProfit),
            Math.Max(0, oppositeSideLockSeconds),
            Math.Max(0, rdStartPostCloseLockSeconds),
            Math.Max(0, rdEndPostCloseLockSeconds),
            Math.Max(0, rdStartPostOpenLockSeconds),
            Math.Max(0, rdEndPostOpenLockSeconds),
            scheduleSleepingJson ?? string.Empty);

    public static ConfigLoadResult NotFound(string machineHostName) =>
        new(
            IsSuccess: false,
            Exists: false,
            MachineHostName: machineHostName,
            ManualHwndColumns: [ManualHwndColumnConfig.Empty],
            PlatformA: "mt5",
            PlatformB: "mt5",
            Point: 1,
            OpenPts: 0,
            ConfirmGapPts: 0,
            HoldConfirmMs: 0,
            OpenPriceFreezeMs: 0,
            ClosePts: 0,
            CloseConfirmGapPts: 0,
            CloseTpProfit: 0,
            CloseConfirmTpProfit: 0,
            CloseMaxTpProfit: 0,
            LimitMaxTp: 0,
            CloseHoldConfirmMs: 0,
            ClosePriceFreezeMs: 0,
            StartTimeHold: 0,
            EndTimeHold: 0,
            MapName1: string.Empty,
            MapName2: string.Empty,
            ConfigId: string.Empty,
            SansJson: "[]",
            Error: null,
            ConfirmLatencyMs: 0,
            MaxGap: 0,
            LimitMaxGap: 0,
            MaxSpread: 0,
            OpenMaxTimesTick: 0,
            CloseMaxTimesTick: 0,
            OpenPendingTimeMs: 0,
            ClosePendingTimeMs: 0,
            DelayOpenAMs: 0,
            DelayOpenBMs: 0,
            DelayCloseAMs: 0,
            DelayCloseBMs: 0,
            OpenNumberOfQualifyingTimes: 1,
            CloseNumberOfQualifyingTimes: 1);

    public static ConfigLoadResult Failed(string machineHostName, string error) =>
        NotFound(machineHostName) with { Exists = true, Error = error };

    private static IReadOnlyList<ManualHwndColumnConfig> NormalizeColumns(IReadOnlyList<ManualHwndColumnConfig>? columns)
    {
        var normalized = (columns ?? [ManualHwndColumnConfig.Empty])
            .Select(x => (x ?? ManualHwndColumnConfig.Empty).Normalize())
            .ToList();

        return normalized.Count > 0 ? normalized : [ManualHwndColumnConfig.Empty];
    }

    private static string NormalizePlatform(string? platform)
    {
        var normalized = (platform ?? string.Empty).Trim().ToLower();
        return normalized is "mt4" or "mt5" ? normalized : "mt5";
    }
}

public sealed record ConfigSaveResult(bool IsSuccess, string? MachineHostName, string? Error)
{
    public static ConfigSaveResult Success(string machineHostName) => new(true, machineHostName, null);
    public static ConfigSaveResult Failed(string error) => new(false, null, error);
}
