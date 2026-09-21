namespace TradeDesktop.Application.Services.CTrader;

public enum HedgeVolumeLevel
{
    Ok = 0,
    Warn = 1,
    Alert = 2,
    Invalid = 3
}

public sealed record HedgeVolumeCheckResult(HedgeVolumeLevel Level, double? Ratio, string Message);

// R5: CalculateTradeProfit bỏ qua lot size, nên lệch notional A/B là vô hình với TP/Rule D.
// Checker chỉ cảnh báo, KHÔNG cưỡng chế. Phase 2 chỉ định nghĩa + test; Phase 8 mới wire.
public static class HedgeVolumeConsistencyChecker
{
    public const double WarnLower = 0.95;
    public const double WarnUpper = 1.05;
    public const double AlertLower = 0.8;
    public const double AlertUpper = 1.2;

    public static HedgeVolumeCheckResult Check(double volumeALots, double volumeBUnits, double contractSizeB)
    {
        if (!double.IsFinite(volumeALots) || !double.IsFinite(volumeBUnits) || !double.IsFinite(contractSizeB))
        {
            return new HedgeVolumeCheckResult(HedgeVolumeLevel.Invalid, null, "Giá trị volume/contract size không hữu hạn.");
        }

        if (contractSizeB <= 0)
        {
            return new HedgeVolumeCheckResult(HedgeVolumeLevel.Invalid, null, $"contractSizeB phải > 0 (hiện {contractSizeB}).");
        }

        if (volumeALots <= 0 || volumeBUnits <= 0)
        {
            return new HedgeVolumeCheckResult(HedgeVolumeLevel.Invalid, null,
                $"volumeALots và volumeBUnits phải > 0 (hiện {volumeALots} / {volumeBUnits}).");
        }

        var ratio = volumeBUnits / contractSizeB / volumeALots;
        var level = ratio is >= WarnLower and <= WarnUpper
            ? HedgeVolumeLevel.Ok
            : ratio is >= AlertLower and <= AlertUpper
                ? HedgeVolumeLevel.Warn
                : HedgeVolumeLevel.Alert;

        return new HedgeVolumeCheckResult(level, ratio, $"hedge ratio={ratio:0.00}");
    }
}
