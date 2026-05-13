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
            record.CloseHoldConfirmMs,
            record.ClosePriceFreezeMs,
            record.StartTimeHold,
            record.EndTimeHold,
            record.StartWaitTime,
            record.EndWaitTime,
            record.Id,
            record.SansJson,
            record.ConfirmLatencyMs,
            record.MaxGap,
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
            record.OpenGapTick,
            record.CloseGapTick,
            record.CoolDownGapTick,
            record.IsShowConfig,
            record.CurrentTickA,
            record.CurrentTickB);
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
    int CloseHoldConfirmMs,
    int ClosePriceFreezeMs,
    int StartTimeHold,
    int EndTimeHold,
    int StartWaitTime,
    int EndWaitTime,
    string MapName1,
    string MapName2,
    string ConfigId,
    string SansJson,
    string? Error,
    int ConfirmLatencyMs,
    int MaxGap,
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
    int OpenGapTick,
    int CloseGapTick,
    int CoolDownGapTick,
    int IsShowConfig = 0,
    string CurrentTickA = "",
    string CurrentTickB = "")
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
        int closeHoldConfirmMs,
        int closePriceFreezeMs,
        int startTimeHold,
        int endTimeHold,
        int startWaitTime,
        int endWaitTime,
        string configId,
        string sansJson,
        int confirmLatencyMs = 0,
        int maxGap = 0,
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
        int openGapTick = 0,
        int closeGapTick = 0,
        int coolDownGapTick = 0,
        int isShowConfig = 0,
        string currentTickA = "",
        string currentTickB = "") =>
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
            Math.Max(0, closeHoldConfirmMs),
            Math.Max(0, closePriceFreezeMs),
            Math.Max(0, startTimeHold),
            Math.Max(0, endTimeHold),
            Math.Max(0, startWaitTime),
            Math.Max(0, endWaitTime),
            mapName1,
            mapName2,
            configId,
            sansJson,
            null,
            Math.Max(0, confirmLatencyMs),
            Math.Max(0, maxGap),
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
            Math.Max(0, openGapTick),
            Math.Max(0, closeGapTick),
            Math.Max(0, coolDownGapTick),
            isShowConfig,
            currentTickA ?? string.Empty,
            currentTickB ?? string.Empty);

    public static ConfigLoadResult NotFound(string machineHostName) =>
        new(false, false, machineHostName, [ManualHwndColumnConfig.Empty], "mt5", "mt5", 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, "[]", null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, "", "");

    public static ConfigLoadResult Failed(string machineHostName, string error) =>
        new(false, true, machineHostName, [ManualHwndColumnConfig.Empty], "mt5", "mt5", 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty, "[]", error, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, "", "");

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