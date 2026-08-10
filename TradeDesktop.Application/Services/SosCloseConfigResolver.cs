using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public static class SosCloseConfigResolver
{
    public static bool HasUsableSosGapThresholds(int confirmGapPts, int closeGapPts)
        => Math.Abs((long)confirmGapPts) > 0 && Math.Abs((long)closeGapPts) > 0;

    public static (int ConfirmGapPts, int CloseGapPts, bool UsesSos) ResolveGapThresholds(
        bool sosActive,
        int normalConfirmGapPts,
        int normalCloseGapPts,
        int sosConfirmGapPts,
        int sosCloseGapPts)
    {
        var usesSos = sosActive && HasUsableSosGapThresholds(sosConfirmGapPts, sosCloseGapPts);
        return usesSos
            ? (sosConfirmGapPts, sosCloseGapPts, true)
            : (normalConfirmGapPts, normalCloseGapPts, false);
    }

    public static string? ValidateLatestGap(
        GapSignalTriggerType triggerType,
        int? gapBuy,
        int? gapSell,
        int confirmGapPts,
        int closeGapPts,
        int limitMaxGap)
    {
        var threshold = Math.Max(Math.Abs((long)confirmGapPts), Math.Abs((long)closeGapPts));
        int? currentGap = triggerType switch
        {
            GapSignalTriggerType.CloseByGapBuy => gapBuy,
            GapSignalTriggerType.CloseByGapSell => gapSell,
            _ => null
        };
        var directionValid = triggerType switch
        {
            GapSignalTriggerType.CloseByGapBuy => currentGap is { } gap && gap >= threshold,
            GapSignalTriggerType.CloseByGapSell => currentGap is { } gap && gap <= -threshold,
            _ => false
        };
        if (!directionValid)
        {
            return "LATEST_CLOSE_CONDITION_INVALID";
        }

        var normalizedLimit = Math.Abs((long)limitMaxGap);
        if (normalizedLimit > 0 && currentGap.HasValue && Math.Abs((long)currentGap.Value) > normalizedLimit)
        {
            return "LATEST_GAP_EXCEEDS_LIMIT";
        }

        return null;
    }

    public static string? ValidateLatestSosGap(
        GapSignalTriggerType triggerType,
        int? gapBuy,
        int? gapSell,
        int closeGapPts,
        int limitMaxGap)
    {
        var threshold = Math.Abs((long)closeGapPts);
        int? currentGap = triggerType switch
        {
            GapSignalTriggerType.CloseByGapBuy => gapBuy,
            GapSignalTriggerType.CloseByGapSell => gapSell,
            _ => null
        };
        var directionValid = triggerType switch
        {
            GapSignalTriggerType.CloseByGapBuy => currentGap is { } gap && gap >= -threshold,
            GapSignalTriggerType.CloseByGapSell => currentGap is { } gap && gap <= threshold,
            _ => false
        };
        if (!directionValid)
        {
            return "LATEST_SOS_CLOSE_CONDITION_INVALID";
        }

        var normalizedLimit = Math.Abs((long)limitMaxGap);
        if (normalizedLimit > 0 && currentGap.HasValue && Math.Abs((long)currentGap.Value) > normalizedLimit)
        {
            return "LATEST_GAP_EXCEEDS_LIMIT";
        }

        return null;
    }
}
